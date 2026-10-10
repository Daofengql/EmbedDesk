using System.Net.Sockets;
using System.Text;
using EmbeddedDebugger.Core.Abstractions;
using EmbeddedDebugger.Core.Models;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace EmbeddedDebugger.Infrastructure.Remote;

public sealed class RemoteDebugService : IRemoteDebugService
{
    public IRemoteDebugSession CreateSession(RemoteConnectionProfile profile) => new RemoteDebugSession(profile);
}

internal sealed class RemoteDebugSession : IRemoteDebugSession
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _resizeGate = new(1, 1);
    private SshClient? _ssh;
    private SftpClient? _sftp;
    private ShellStream? _shell;
    private TcpClient? _tcp;
    private NetworkStream? _networkStream;
    private CancellationTokenSource? _sessionCts;
    private Task? _receiveTask;
    private Task? _performanceTask;
    private RemoteSessionState _state;
    private readonly object _logGate = new();
    private int _columns = 120;
    private int _rows = 40;
    private LinuxPerformanceReader _performanceReader = new();
    private string? _lastPerformanceError;

    private void Log(string text)
    {
        lock (_logGate) ConnectionLogReceived?.Invoke(this, $"[{DateTime.Now:HH:mm:ss.fff}] {text}");
    }

    public RemoteDebugSession(RemoteConnectionProfile profile)
    {
        Profile = profile;
        _state = RemoteSessionState.Disconnected;
    }

    public RemoteConnectionProfile Profile { get; }
    public RemoteSessionState State { get { lock (_gate) return _state; } private set { lock (_gate) _state = value; StateChanged?.Invoke(this, value); } }
    public string? HomeDirectory { get; private set; }
    public string? HostPlatform { get; private set; }
    public bool SupportsDirectoryManagement => _sftp?.IsConnected == true;
    public event EventHandler<string>? TextReceived;
    public event EventHandler<string>? ConnectionLogReceived;
    public event EventHandler<RemotePerformanceSnapshot>? PerformanceChanged;
    public event EventHandler<RemoteSessionState>? StateChanged;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (State is RemoteSessionState.Connected or RemoteSessionState.Connecting) return;
        State = RemoteSessionState.Connecting;
        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            if (Profile.Protocol == RemoteProtocol.Ssh) await ConnectSshAsync(_sessionCts.Token);
            else
            {
                await ConnectTelnetAsync(_sessionCts.Token);
                StartReceiving(_sessionCts.Token);
            }
        }
        catch (Exception ex)
        {
            Log($"连接失败：{ex.Message}");
            await DisconnectAsync();
            State = RemoteSessionState.Faulted;
            throw;
        }
    }

    private async Task ConnectSshAsync(CancellationToken cancellationToken)
    {
        Log($"连接 {Profile.Host}:{Profile.Port}，用户 {Profile.Username}");
        var logger = new SshConnectionLogger(Log);
        _ssh = new SshClient(CreateConnectionInfo(logger, true));
        _ssh.HostKeyReceived += (_, args) =>
        {
            Log($"主机密钥 {args.HostKeyName} · {args.KeyLength} 位 · SHA256:{args.FingerPrintSHA256}");
            if (Profile.AcceptUnknownHostKey) args.CanTrust = true;
        };
        // SSH.NET authentication contains synchronous waits even in ConnectAsync.
        // Keep those off WPF's dispatcher so handshake output can render live.
        await Task.Run(() => _ssh.ConnectAsync(cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Log("身份验证成功，申请 PTY 与交互 Shell");
        await _resizeGate.WaitAsync(cancellationToken);
        try
        {
            _shell = await Task.Run(() => _ssh.CreateShellStream("xterm-256color", (uint)_columns, (uint)_rows, 0, 0, 4096,
                new Dictionary<TerminalModes, uint>
                {
                    [TerminalModes.VERASE] = 127,
                    [TerminalModes.IUTF8] = 1
                }), cancellationToken);
        }
        finally { _resizeGate.Release(); }
        logger.Handshaking = false;
        Log("PTY 与交互 Shell 已就绪");
        StartReceiving(cancellationToken);
        _performanceReader = new LinuxPerformanceReader();
        _lastPerformanceError = null;
        _performanceTask = Task.Run(() => PerformanceLoopAsync(cancellationToken), cancellationToken);
        HostPlatform = await DetectHostPlatformAsync(cancellationToken);
        // Optional services must never delay reception of the login output.
        try
        {
            Log("正在初始化 SFTP 文件服务");
            _sftp = new SftpClient(CreateConnectionInfo(new SshConnectionLogger(line => Log("SFTP · " + line)) { Handshaking = false }, false));
            await Task.Run(() => _sftp.ConnectAsync(cancellationToken), cancellationToken);
            HomeDirectory = _sftp.WorkingDirectory;
            Log("SFTP 文件服务已就绪");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is SshException or IOException or SocketException)
        {
            _sftp?.Dispose();
            _sftp = null;
            HomeDirectory = null;
            Log($"SFTP 不可用：{ex.Message}");
        }
    }

    private ConnectionInfo CreateConnectionInfo(SshConnectionLogger logger, bool forwardBanner)
    {
        AuthenticationMethod authentication = Profile.AuthenticationMode == RemoteAuthenticationMode.PrivateKey
            ? new PrivateKeyAuthenticationMethod(Profile.Username, CreatePrivateKeyFile(Profile))
            : new PasswordAuthenticationMethod(Profile.Username, Profile.Password);
        var connection = new ConnectionInfo(Profile.Host, Profile.Port, Profile.Username, authentication)
        {
            Timeout = TimeSpan.FromSeconds(12), LoggerFactory = logger
        };
        connection.AuthenticationBanner += (_, args) =>
        {
            Log($"{(forwardBanner ? "SSH" : "SFTP")} Banner: {args.BannerMessage}");
            if (forwardBanner) TextReceived?.Invoke(this, args.BannerMessage.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        };
        return connection;
    }

    private void StartReceiving(CancellationToken cancellationToken)
    {
        State = RemoteSessionState.Connected;
        _receiveTask = Task.Run(() => ReceiveLoopAsync(cancellationToken), cancellationToken);
    }

    private async Task<string?> DetectHostPlatformAsync(CancellationToken cancellationToken)
    {
        foreach (var commandText in new[] { "cat /etc/os-release", "uname -s", "cmd /c ver" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var command = _ssh!.CreateCommand(commandText);
                command.CommandTimeout = TimeSpan.FromSeconds(3);
                var output = await Task.Run(command.Execute, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (commandText == "cat /etc/os-release")
                {
                    var id = output.Split('\n').Select(line => line.Trim())
                        .FirstOrDefault(line => line.StartsWith("ID=", StringComparison.Ordinal));
                    if (id is not null)
                    {
                        var platform = id[3..].Trim().Trim('"', '\'').ToLowerInvariant();
                        if (platform.Length > 0) return platform;
                    }
                }
                else if (commandText == "uname -s")
                {
                    var kernel = output.Trim().ToLowerInvariant();
                    if (kernel is "linux" or "darwin" or "freebsd") return kernel;
                    if (kernel.StartsWith("mingw", StringComparison.Ordinal) || kernel.StartsWith("msys", StringComparison.Ordinal) || kernel.StartsWith("cygwin", StringComparison.Ordinal)) return "windows";
                }
                else if (output.Contains("Windows", StringComparison.OrdinalIgnoreCase)) return "windows";
            }
            catch (Exception ex) when (ex is SshException or IOException or SocketException)
            {
                // System identification is optional; it must not prevent a usable shell.
            }
        }
        return null;
    }

    private static PrivateKeyFile CreatePrivateKeyFile(RemoteConnectionProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.PrivateKeyPath)) throw new InvalidOperationException("未选择 SSH 私钥文件");
        if (!File.Exists(profile.PrivateKeyPath)) throw new FileNotFoundException("SSH 私钥文件不存在", profile.PrivateKeyPath);
        return string.IsNullOrEmpty(profile.PrivateKeyPassphrase)
            ? new PrivateKeyFile(profile.PrivateKeyPath)
            : new PrivateKeyFile(profile.PrivateKeyPath, profile.PrivateKeyPassphrase);
    }

    private async Task ConnectTelnetAsync(CancellationToken cancellationToken)
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(Profile.Host, Profile.Port, cancellationToken);
        _networkStream = _tcp.GetStream();
        HomeDirectory = null;
        HostPlatform = null;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (State is RemoteSessionState.Disconnected or RemoteSessionState.Disconnecting) return;
        State = RemoteSessionState.Disconnecting;
        try
        {
            _sessionCts?.Cancel();
            var tasks = new[] { _receiveTask, _performanceTask }.Where(task => task is not null).Cast<Task>().ToArray();
            if (tasks.Length > 0)
            {
                try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                catch (TimeoutException) { }
            }
            await Task.Run(() =>
            {
                _shell?.Dispose();
                _networkStream?.Dispose();
                _tcp?.Dispose();
                _sftp?.Disconnect();
                _sftp?.Dispose();
                _ssh?.Disconnect();
                _ssh?.Dispose();
            }, cancellationToken);
            _shell = null;
            _networkStream = null;
            _tcp = null;
            _sftp = null;
            _ssh = null;
            HomeDirectory = null;
        }
        finally
        {
            _sessionCts?.Dispose();
            _sessionCts = null;
            _receiveTask = null;
            _performanceTask = null;
            State = RemoteSessionState.Disconnected;
        }
    }

    public async Task SendAsync(string text, CancellationToken cancellationToken = default)
    {
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            if (State != RemoteSessionState.Connected) throw new InvalidOperationException("远程会话尚未连接");
            var bytes = Encoding.UTF8.GetBytes(text);
            if (Profile.Protocol == RemoteProtocol.Ssh)
            {
                await Task.Run(() =>
                {
                    _shell!.Write(text);
                    _shell.Flush();
                }, cancellationToken);
            }
            else
            {
                await _networkStream!.WriteAsync(bytes, cancellationToken);
                await _networkStream.FlushAsync(cancellationToken);
            }
        }
        finally { _sendGate.Release(); }
    }

    public async Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        await _resizeGate.WaitAsync(cancellationToken);
        try
        {
            _columns = Math.Clamp(columns, 2, ushort.MaxValue);
            _rows = Math.Clamp(rows, 1, ushort.MaxValue);
            if (Profile.Protocol != RemoteProtocol.Ssh || _shell is null) return;
            await Task.Run(() => _shell.ChangeWindowSize((uint)_columns, (uint)_rows, 0, 0), cancellationToken);
        }
        finally { _resizeGate.Release(); }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var decoder = Encoding.UTF8.GetDecoder();
        var characters = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        void Publish(int count)
        {
            var length = decoder.GetChars(buffer, 0, count, characters, 0);
            if (length > 0) TextReceived?.Invoke(this, new string(characters, 0, length));
        }
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (Profile.Protocol == RemoteProtocol.Ssh)
                {
                    if (_shell is null || !_shell.DataAvailable) { await Task.Delay(25, cancellationToken); continue; }
                    var count = _shell.Read(buffer, 0, buffer.Length);
                    if (count > 0) Publish(count);
                }
                else
                {
                    var count = await _networkStream!.ReadAsync(buffer, cancellationToken);
                    if (count == 0) break;
                    Publish(count);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) { break; }
            catch (IOException) { break; }
        }
    }

    private async Task PerformanceLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var command = _ssh!.CreateCommand(LinuxPerformanceReader.Command);
                command.CommandTimeout = TimeSpan.FromSeconds(5);
                await command.ExecuteAsync(cancellationToken);
                if (command.ExitStatus != 0)
                    throw new IOException(string.IsNullOrWhiteSpace(command.Error) ? "远程主机不支持 Linux /proc 采集" : command.Error.Trim());
                var snapshot = _performanceReader.Parse(command.Result);
                _lastPerformanceError = null;
                PerformanceChanged?.Invoke(this, snapshot);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is SshException or InvalidOperationException or IOException or SocketException)
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (_lastPerformanceError != ex.Message) Log($"性能采集失败：{ex.Message}");
                _lastPerformanceError = ex.Message;
                PerformanceChanged?.Invoke(this, new RemotePerformanceSnapshot(DateTimeOffset.Now, null, null, null, null, null, null, "读取失败：" + ex.Message));
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task<IReadOnlyList<RemoteDirectoryEntry>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        EnsureSftp();
        var entries = await Task.Run(() => _sftp!.ListDirectory(path)
            .Where(item => item.Name is not "." and not "..")
            .OrderByDescending(item => item.IsDirectory)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => ToDirectoryEntry(item))
            .ToArray(), cancellationToken);
        return entries;
    }

    public Task DownloadFileAsync(string remotePath, string localPath, CancellationToken cancellationToken = default)
    {
        EnsureSftp();
        return Task.Run(() => usingWriteFile(localPath, stream => _sftp!.DownloadFile(remotePath, stream)), cancellationToken);
    }

    public Task UploadFileAsync(string localPath, string remotePath, CancellationToken cancellationToken = default)
    {
        EnsureSftp();
        return Task.Run(() => usingReadFile(localPath, stream => _sftp!.UploadFile(stream, remotePath, true)), cancellationToken);
    }

    public Task DownloadPreviewAsync(string remotePath, string localPath, long maxBytes, CancellationToken cancellationToken = default)
    {
        EnsureSftp();
        return Task.Run(() =>
        {
            using var source = _sftp!.OpenRead(remotePath);
            if (source.Length > maxBytes) throw new IOException("文件超过预览大小上限，请使用下载功能。");
            using var destination = File.Create(localPath);
            var buffer = new byte[64 * 1024];
            long total = 0;
            int count;
            while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                total += count;
                if (total > maxBytes) throw new IOException("文件超过预览大小上限，请使用下载功能。");
                destination.Write(buffer, 0, count);
            }
        }, cancellationToken);
    }

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        EnsureSftp();
        return Task.Run(() => _sftp!.CreateDirectory(path), cancellationToken);
    }

    public Task DeleteAsync(string path, bool directory, CancellationToken cancellationToken = default)
    {
        EnsureSftp();
        return Task.Run(() => { if (directory) _sftp!.DeleteDirectory(path); else _sftp!.DeleteFile(path); }, cancellationToken);
    }

    private void EnsureSftp()
    {
        if (Profile.Protocol != RemoteProtocol.Ssh) throw new NotSupportedException("Telnet 不支持远程目录管理");
        if (!SupportsDirectoryManagement) throw new NotSupportedException("该 SSH 服务未启用 SFTP");
    }

    private static void usingWriteFile(string path, Action<FileStream> action)
    {
        using var stream = File.Create(path);
        action(stream);
    }

    private static void usingReadFile(string path, Action<FileStream> action)
    {
        using var stream = File.OpenRead(path);
        action(stream);
    }

    private static RemoteDirectoryEntry ToDirectoryEntry(ISftpFile item) => new(item.Name, item.FullName, item.IsDirectory, item.Attributes.Size, item.Attributes.LastWriteTimeUtc == DateTime.MinValue ? null : new DateTimeOffset(item.Attributes.LastWriteTimeUtc), FormatPermissions(item));
    private static string FormatPermissions(ISftpFile item)
    {
        static string Flags(bool read, bool write, bool execute) => $"{(read ? 'r' : '-').ToString()}{(write ? 'w' : '-').ToString()}{(execute ? 'x' : '-').ToString()}";
        return Flags(item.OwnerCanRead, item.OwnerCanWrite, item.OwnerCanExecute) + Flags(item.GroupCanRead, item.GroupCanWrite, item.GroupCanExecute) + Flags(item.OthersCanRead, item.OthersCanWrite, item.OthersCanExecute);
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
