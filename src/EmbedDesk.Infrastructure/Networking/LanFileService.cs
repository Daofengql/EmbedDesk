using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmbedDesk.Infrastructure.Networking;

public sealed record LanFileEntry(string Name, bool IsDirectory, long Length);
public sealed record LanFileServerInfo(string Name, string Address, int Port)
{
    public string Endpoint => $"http://{Address}:{Port}";
    public string FileTransport { get; init; } = "tcp-http";
    public string DisplayName => $"{Name} · {Address}:{Port} · TCP";
}

/// <summary>LAN HTTP file share, bounded streaming and a root that excludes reparse points.</summary>
public sealed partial class LanFileService : IAsyncDisposable
{
    public const int DiscoveryPort = 39127;
    private static readonly byte[] Probe = Encoding.ASCII.GetBytes("EmbedDesk files discovery v1");
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly ConcurrentDictionary<int, Task> _requests = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private TcpListener? _listener;
    private UdpClient? _discovery;
    private CancellationTokenSource? _life;
    private Task _accept = Task.CompletedTask, _announce = Task.CompletedTask;
    private string _root = string.Empty, _name = "EmbedDesk 文件服务";
    private int _sequence;
    public bool IsRunning => _listener is not null;
    public int Port { get; private set; }
    public string AccessCode { get; private set; } = string.Empty;
    public IReadOnlyList<string> EndpointAddresses => LocalAddresses().Select(a => $"http://{a}:{Port}").ToArray();
    public string Endpoints => string.Join("  /  ", EndpointAddresses);
    public event EventHandler<string>? Error;

    public async Task StartServerAsync(string root, string name = "EmbedDesk 文件服务", int port = 0, CancellationToken token = default)
    {
        root = Path.GetFullPath(root); if (!Directory.Exists(root)) throw new DirectoryNotFoundException("共享根目录不存在。");
        RejectLinks(root);
        if (port != 0 && port is < 1024 or > 65535) throw new ArgumentException("端口须为 1024–65535，或 0 自动分配。");
        await _lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            _root = root; _name = name.Trim(); if (_name.Length is < 1 or > 64) throw new ArgumentException("服务名称长度须为 1–64。");
            AccessCode = Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
            _life = new CancellationTokenSource(); _listener = new TcpListener(IPAddress.Any, port);
            try
            {
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _discovery = new UdpClient(AddressFamily.InterNetwork);
                _discovery.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _discovery.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
                _accept = AcceptAsync(_life.Token); _announce = DiscoverServerAsync(_life.Token);
            }
            catch { await StopCoreAsync().ConfigureAwait(false); throw; }
        }
        finally { _lifecycle.Release(); }
    }
    private async Task AcceptAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(token).ConfigureAwait(false);
                client.NoDelay = true;
                if (_requests.Count >= 16) { client.Dispose(); continue; }
                var id = Interlocked.Increment(ref _sequence); var task = HandleAsync(client, token); _requests[id] = task;
                _ = task.ContinueWith(_ => _requests.TryRemove(id, out var ignored), TaskScheduler.Default);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { if (!token.IsCancellationRequested) Error?.Invoke(this, "文件监听停止：" + ex.Message); }
    }
    private async Task HandleAsync(TcpClient client, CancellationToken token)
    {
        using var owned = client; var stream = client.GetStream(); var started = false;
        using var requestLife = CancellationTokenSource.CreateLinkedTokenSource(token);
        var watching = WatchClientAsync(client, requestLife); token = requestLife.Token;
        try
        {
            if (client.Client.RemoteEndPoint is not IPEndPoint remote || !IsLan(remote.Address)) { await RespondAsync(stream, 403, [], token).ConfigureAwait(false); return; }
            var request = await ReadRequestAsync(stream, token).ConfigureAwait(false);
            var supplied = request.Headers.GetValueOrDefault("X-EmbedDesk-Code", "");
            if (supplied.Length != AccessCode.Length || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(AccessCode))) { await RespondAsync(stream, 401, [], token).ConfigureAwait(false); return; }
            var query = request.Url.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).FirstOrDefault(p => p[0] == "path");
            var relative = query is { Length: 2 } ? Uri.UnescapeDataString(query[1]) : ""; var file = Resolve(relative);
            switch (request.Method, request.Url.AbsolutePath)
            {
                case ("GET", "/api/list"):
                    if (!Directory.Exists(file)) throw new DirectoryNotFoundException("目录不存在。");
                    var entries = Directory.EnumerateFileSystemEntries(file).Select(p => new FileInfo(p)).Where(i => (i.Attributes & FileAttributes.ReparsePoint) == 0 && !IsTransferFile(i.Name))
                        .Take(5001).Select(i => new LanFileEntry(i.Name, (i.Attributes & FileAttributes.Directory) != 0, (i.Attributes & FileAttributes.Directory) == 0 ? i.Length : 0)).ToArray();
                    if (entries.Length > 5000) throw new IOException("此目录超过 5000 项，暂不支持浏览。");
                    await RespondAsync(stream, 200, JsonSerializer.SerializeToUtf8Bytes(entries.OrderBy(e => !e.IsDirectory).ThenBy(e => e.Name)), token).ConfigureAwait(false); break;
                case ("GET", "/api/download"):
                    await using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
                    {
                        var version = FileVersion(file, input.Length);
                        if (request.Headers.TryGetValue("If-Match", out var expected) && expected != version) { await RespondAsync(stream, 412, [], token).ConfigureAwait(false); break; }
                        var offset = 0L;
                        var ranged = request.Headers.TryGetValue("Range", out var range);
                        if (ranged && (range!.Length < 7 || !range.StartsWith("bytes=", StringComparison.Ordinal) || !range.EndsWith('-')
                            || !long.TryParse(range.AsSpan(6, range.Length - 7), out offset) || offset < 0 || offset >= input.Length))
                        { await RespondAsync(stream, 416, [], token).ConfigureAwait(false); break; }
                        input.Position = offset;
                        var extra = "Accept-Ranges: bytes\r\nETag: " + version + "\r\n";
                        if (ranged) extra += $"Content-Range: bytes {offset}-{input.Length - 1}/{input.Length}\r\n";
                        await HeadersAsync(stream, ranged ? 206 : 200, input.Length - offset, token, extra).ConfigureAwait(false); started = true;
                        await CopyAsync(input, stream, input.Length - offset, null, token).ConfigureAwait(false);
                    }
                    break;
                case ("GET", "/api/download-chunk"):
                    await using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
                    {
                        var version = FileVersion(file, input.Length);
                        if (request.Headers.GetValueOrDefault("If-Match") != version) { await RespondAsync(stream, 412, [], token).ConfigureAwait(false); break; }
                        var range = request.Headers.GetValueOrDefault("Range", ""); var parts = range.StartsWith("bytes=", StringComparison.Ordinal) ? range[6..].Split('-') : [];
                        if (parts.Length != 2 || !long.TryParse(parts[0], out var from) || !long.TryParse(parts[1], out var to)
                            || from < 0 || to < from || to >= input.Length || to - from >= TransferChunkSize)
                        { await RespondAsync(stream, 416, [], token).ConfigureAwait(false); break; }
                        var bytes = new byte[(int)(to - from + 1)]; input.Position = from;
                        await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
                        if (FileVersion(file, input.Length) != version) { await RespondAsync(stream, 412, [], token).ConfigureAwait(false); break; }
                        var extra = $"Content-Range: bytes {from}-{to}/{input.Length}\r\nETag: {version}\r\nX-EmbedDesk-Chunk-Hash: {Convert.ToHexString(SHA256.HashData(bytes))}\r\n";
                        await HeadersAsync(stream, 206, bytes.Length, token, extra).ConfigureAwait(false); started = true;
                        using var data = new MemoryStream(bytes, false); await CopyAsync(data, stream, bytes.Length, null, token).ConfigureAwait(false);
                    }
                    break;
                case ("GET", "/api/file-info"):
                    await using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
                    {
                        var info = new RemoteFileInfo(input.Length, await FastFileIdentityAsync(input, file, token).ConfigureAwait(false), FileVersion(file, input.Length));
                        await RespondAsync(stream, 200, JsonSerializer.SerializeToUtf8Bytes(info), token).ConfigureAwait(false);
                    }
                    break;
                case ("POST", "/api/upload-state"):
                case ("PUT", "/api/upload-chunk"):
                case ("POST", "/api/upload-commit"):
                    await HandleResumableUploadAsync(stream, request, relative, file, token).ConfigureAwait(false);
                    break;
                case ("POST", "/api/upload-v2-state"):
                case ("PUT", "/api/upload-v2-chunk"):
                case ("POST", "/api/upload-v2-commit"):
                    await HandleParallelUploadAsync(stream, request, relative, file, token).ConfigureAwait(false);
                    break;
                case ("PUT", "/api/upload"):
                    var length = request.Length;
                    if (length < 0) throw new IOException("上传必须声明有效的文件长度。");
                    if (!Directory.Exists(Path.GetDirectoryName(file))) throw new DirectoryNotFoundException("目标目录不存在。");
                    CheckDiskSpace(file, length, length);
                    var overwrite = request.Headers.GetValueOrDefault("X-EmbedDesk-Overwrite") == "true";
                    if (File.Exists(file) && !overwrite) { await RespondAsync(stream, 409, [], token).ConfigureAwait(false); return; }
                    if (request.Headers.GetValueOrDefault("Expect") == "100-continue") await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"), token).ConfigureAwait(false);
                    var temporary = Path.Combine(Path.GetDirectoryName(file)!, ".embeddesk-upload-" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
                        {
                            if (request.BodyPrefix.Length > length) throw new InvalidDataException("文件数据超过声明长度。");
                            await output.WriteAsync(request.BodyPrefix, token).ConfigureAwait(false);
                            await CopyAsync(stream, output, length - request.BodyPrefix.Length, null, token).ConfigureAwait(false);
                        }
                        token.ThrowIfCancellationRequested(); Resolve(relative); File.Move(temporary, file, overwrite); await RespondAsync(stream, 204, [], token).ConfigureAwait(false);
                    }
                    finally { File.Delete(temporary); }
                    break;
                default: await RespondAsync(stream, 404, [], token).ConfigureAwait(false); break;
            }
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException or SocketException or OperationCanceledException)
        { if (!started && !token.IsCancellationRequested) { try { await RespondAsync(stream, ex is UnauthorizedAccessException ? 403 : ex is TransferConflictException ? 409 : 400, Encoding.UTF8.GetBytes(ex.Message), token).ConfigureAwait(false); } catch (Exception error) when (error is IOException or SocketException or OperationCanceledException) { } } }
        finally { requestLife.Cancel(); await watching.ConfigureAwait(false); }
    }
    private static async Task WatchClientAsync(TcpClient client, CancellationTokenSource life)
    {
        try
        {
            while (true)
            {
                await Task.Delay(500, life.Token).ConfigureAwait(false);
                if (client.Client.Poll(0, SelectMode.SelectRead) && client.Client.Available == 0) { life.Cancel(); return; }
            }
        }
        catch (OperationCanceledException) when (life.IsCancellationRequested) { }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { life.Cancel(); }
    }
    private sealed record FileRequest(string Method, Uri Url, Dictionary<string, string> Headers, long Length, byte[] BodyPrefix);
    private static async Task<FileRequest> ReadRequestAsync(Stream stream, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var buffer = new MemoryStream(); var chunk = new byte[4096]; var headerEnd = -1;
        while (headerEnd < 0)
        {
            var count = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException();
            buffer.Write(chunk, 0, count);
            headerEnd = buffer.GetBuffer().AsSpan(0, (int)buffer.Length).IndexOf("\r\n\r\n"u8);
            if (headerEnd >= 0 ? headerEnd + 4 > 16384 : buffer.Length >= 16384) throw new InvalidDataException("请求头过大。");
        }
        var bytes = buffer.ToArray();
        var lines = Encoding.ASCII.GetString(bytes, 0, headerEnd).Split("\r\n"); var start = lines[0].Split(' ');
        if (start.Length != 3 || start[2] != "HTTP/1.1" || !start[1].StartsWith('/') || start[1].StartsWith("//", StringComparison.Ordinal)) throw new InvalidDataException("请求格式无效。");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1).Where(l => l.Length > 0))
        { var colon = line.IndexOf(':'); if (colon < 1 || !headers.TryAdd(line[..colon], line[(colon + 1)..].Trim())) throw new InvalidDataException("请求头无效。"); }
        if (headers.ContainsKey("Transfer-Encoding")) throw new InvalidDataException("上传需要 Content-Length。");
        var length = headers.TryGetValue("Content-Length", out var text) && long.TryParse(text, out var size) ? size : start[0] == "GET" ? 0 : -1;
        return new(start[0], new Uri("http://localhost" + start[1]), headers, length, bytes[(headerEnd + 4)..]);
    }
    private static Task HeadersAsync(Stream stream, int code, long length, CancellationToken token, string extra = "")
    {
        var label = code switch { 200 => "OK", 204 => "No Content", 206 => "Partial Content", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found", 409 => "Conflict", 412 => "Precondition Failed", 416 => "Range Not Satisfiable", _ => "Bad Request" };
        return stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {label}\r\nContent-Length: {length}\r\nConnection: close\r\nContent-Type: application/octet-stream\r\n{extra}\r\n"), token).AsTask();
    }
    private static async Task RespondAsync(Stream stream, int code, byte[] body, CancellationToken token)
    { await HeadersAsync(stream, code, body.Length, token).ConfigureAwait(false); if (body.Length > 0) await stream.WriteAsync(body, token).ConfigureAwait(false); }
    private string Resolve(string relative)
    {
        if (relative.Length > 2048 || relative.Contains('\\') || relative.Contains(':') || relative.StartsWith('/') || relative.Split('/').Any(s => s is ".." || IsTransferFile(s))) throw new UnauthorizedAccessException("路径超出共享目录或包含非法名称。");
        var file = Path.GetFullPath(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (file != _root && !file.StartsWith(Path.TrimEndingDirectorySeparator(_root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("路径超出共享目录。");
        RejectLinks(file); return file;
    }
    private static void RejectLinks(string path, bool includeParents = true)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("共享目录不能经过符号链接或目录联接。");
            if (!includeParents) break;
        }
    }
    private async Task DiscoverServerAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var packet = await _discovery!.ReceiveAsync(token).ConfigureAwait(false);
                if (!IsLan(packet.RemoteEndPoint.Address) || !packet.Buffer.AsSpan().SequenceEqual(Probe)) continue;
                var payload = JsonSerializer.SerializeToUtf8Bytes(new { name = _name, port = Port, protocol = 1, fileTransport = "tcp-http" });
                await _discovery.SendAsync(payload, packet.RemoteEndPoint, token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { if (!token.IsCancellationRequested) Error?.Invoke(this, "发现服务停止：" + ex.Message); }
    }
    public async Task<IReadOnlyList<LanFileServerInfo>> DiscoverAsync(TimeSpan timeout, CancellationToken token = default)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(timeout));
        token.ThrowIfCancellationRequested();
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        var result = new Dictionary<string, LanFileServerInfo>(); using var timer = CancellationTokenSource.CreateLinkedTokenSource(token); timer.CancelAfter(timeout);
        var probing = ProbeAsync(udp, timer.Token);
        try
        {
            while (true)
            {
                UdpReceiveResult packet;
                try { packet = await udp.ReceiveAsync(timer.Token).ConfigureAwait(false); }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset) { continue; }
                if (packet.Buffer.Length > 2048 || !IsLan(packet.RemoteEndPoint.Address)) continue;
                try
                {
                    using var json = JsonDocument.Parse(packet.Buffer); var root = json.RootElement;
                    var port = root.GetProperty("port").GetInt32(); var name = root.GetProperty("name").GetString();
                    if (root.GetProperty("protocol").GetInt32() == 1 && port is >= 1024 and <= 65535 && name is { Length: > 0 and <= 64 })
                    {
                        var transport = root.TryGetProperty("fileTransport", out var advertised) ? advertised.GetString() : "tcp-http";
                        if (transport != "tcp-http") continue;
                        var item = new LanFileServerInfo(name, packet.RemoteEndPoint.Address.ToString(), port) { FileTransport = transport };
                        if (result.Count < 128 || result.ContainsKey(item.Endpoint)) result[item.Endpoint] = item;
                    }
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        finally { timer.Cancel(); await probing.ConfigureAwait(false); }
        return result.Values.ToArray();
    }
    private static async Task ProbeAsync(UdpClient udp, CancellationToken token)
    {
        var targets = new[] { IPAddress.Broadcast, IPAddress.Loopback }.Concat(BroadcastAddresses()).Distinct().ToArray();
        try
        {
            // Repeat a small, bounded number of UDP probes so one lost broadcast need not hide a server.
            for (var attempt = 0; attempt < 4; attempt++)
            {
                foreach (var address in targets)
                    try { await udp.SendAsync(Probe, new IPEndPoint(address, DiscoveryPort), token).ConfigureAwait(false); } catch (SocketException) { }
                await Task.Delay(350, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private static IEnumerable<IPAddress> LocalAddresses() => NetworkInterface.GetAllNetworkInterfaces().Where(i => i.OperationalStatus == OperationalStatus.Up).SelectMany(i => i.GetIPProperties().UnicastAddresses).Select(a => a.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork && IsLan(a) && !IPAddress.IsLoopback(a)).Distinct().DefaultIfEmpty(IPAddress.Loopback);
    private static IEnumerable<IPAddress> BroadcastAddresses() => NetworkInterface.GetAllNetworkInterfaces().Where(i => i.OperationalStatus == OperationalStatus.Up).SelectMany(i => i.GetIPProperties().UnicastAddresses).Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && IsLan(a.Address)).Select(a => new IPAddress(a.Address.GetAddressBytes().Zip(a.IPv4Mask.GetAddressBytes(), (ip, mask) => (byte)(ip | ~mask)).ToArray()));
    internal static bool IsLan(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4(); if (IPAddress.IsLoopback(address)) return true;
        var b = address.GetAddressBytes(); return address.AddressFamily == AddressFamily.InterNetwork && (b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 169 && b[1] == 254);
    }
    private static async Task<Uri> ValidateAddressAsync(string address, CancellationToken token)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0) throw new ArgumentException("请输入局域网 HTTP 地址。");
        var resolved = await Dns.GetHostAddressesAsync(uri.Host, token).ConfigureAwait(false); var ip = resolved.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && IsLan(a)) ?? throw new ArgumentException("只允许局域网 IPv4 地址。");
        return new UriBuilder(uri) { Host = ip.ToString() }.Uri;
    }
    private async Task<HttpRequestMessage> RequestAsync(HttpMethod method, string address, string code, string relative, string route, CancellationToken token)
    {
        var uri = await ValidateAddressAsync(address, token).ConfigureAwait(false);
        var request = new HttpRequestMessage(method, new Uri(uri, "api/" + route + "?path=" + Uri.EscapeDataString(relative)));
        request.Headers.Add("X-EmbedDesk-Code", code); return request;
    }
    public async Task<IReadOnlyList<LanFileEntry>> ListRemoteAsync(string address, string code, string relative = "", CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = await RequestAsync(HttpMethod.Get, address, code, relative, "list", timeout.Token).ConfigureAwait(false);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false); await EnsureSuccessAsync(response, timeout.Token).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new InvalidDataException("目录响应过大。");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var memory = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0) { if (memory.Length + count > 2 * 1024 * 1024) throw new InvalidDataException("目录响应过大。"); memory.Write(buffer, 0, count); }
        var entries = JsonSerializer.Deserialize<LanFileEntry[]>(memory.ToArray()) ?? [];
        if (entries.Length > 5000 || entries.Any(e => e is null || e.Name is not { Length: > 0 and <= 255 } || e.Name.Any(char.IsControl) || e.Name.IndexOfAny(['/', '\\', ':']) >= 0 || e.Name is "." or ".." || e.Length < 0)) throw new InvalidDataException("目录项目无效。");
        return entries;
    }
    private static async Task CopyAsync(Stream input, Stream output, long length, IProgress<long>? progress, CancellationToken token)
    {
        var bytes = new byte[65536]; long total = 0, last = 0;
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        while (total < length)
        {
            idle.CancelAfter(TimeSpan.FromSeconds(30));
            var count = await input.ReadAsync(bytes.AsMemory(0, (int)Math.Min(bytes.Length, length - total)), idle.Token).ConfigureAwait(false); if (count == 0) throw new EndOfStreamException("文件传输未完成。");
            idle.CancelAfter(TimeSpan.FromSeconds(30));
            await output.WriteAsync(bytes.AsMemory(0, count), idle.Token).ConfigureAwait(false); total += count;
            if (Environment.TickCount64 - last > 100) { progress?.Report(total); last = Environment.TickCount64; }
        }
        progress?.Report(total);
    }
    public async Task StopServerAsync() { await _lifecycle.WaitAsync().ConfigureAwait(false); try { await StopCoreAsync().ConfigureAwait(false); } finally { _lifecycle.Release(); } }
    private async Task StopCoreAsync()
    {
        _life?.Cancel(); _listener?.Stop(); _discovery?.Dispose();
        await Task.WhenAll(_accept, _announce).ConfigureAwait(false); await Task.WhenAll(_requests.Values).ConfigureAwait(false); _requests.Clear();
        _listener = null; _discovery = null; _life?.Dispose(); _life = null;
    }
    public async ValueTask DisposeAsync() { await StopServerAsync().ConfigureAwait(false); _http.Dispose(); }
}
