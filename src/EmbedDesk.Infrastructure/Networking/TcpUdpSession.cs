using System.Net;
using System.Net.Sockets;
using System.Text;
using EmbedDesk.Core.Abstractions;
using EmbedDesk.Core.Models;
using EmbedDesk.Infrastructure.Diagnostics;

namespace EmbedDesk.Infrastructure.Networking;

internal sealed class TcpUdpSession : ITransportSession
{
    private readonly object _gate = new();
    private readonly List<TcpClient> _clients = new();
    private readonly HashSet<Task> _clientReaders = [];
    private readonly TransportOptions _options;
    private CancellationTokenSource? _lifetime;
    private TcpListener? _listener;
    private UdpClient? _udpClient;
    private TcpClient? _activeClient;
    private IPEndPoint? _lastUdpRemote;
    private Task? _worker;
    private Task? _scheduledSender;
    private Task? _startTask, _stopTask;
    private TransportSessionState _state = TransportSessionState.Created;
    public string? BoundLocalEndpoint { get; private set; }

    public TcpUdpSession(TransportOptions options)
    {
        _options = options;
        Id = Guid.NewGuid().ToString("N");
    }

    public string Id { get; }
    public TransportOptions Options => _options;
    public TransportSessionState State
    {
        get { lock (_gate) return _state; }
    }

    public event EventHandler<TransportMessage>? MessageReceived;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_startTask is { IsCompleted: false }) return _startTask;
            if (_stopTask is { IsCompleted: false }) throw new InvalidOperationException("连接正在停止。");
            return _startTask = StartCoreAsync(cancellationToken);
        }
    }
    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_state is not (TransportSessionState.Created or TransportSessionState.Stopped or TransportSessionState.Faulted))
            {
                return;
            }
            _state = TransportSessionState.Starting;
            BoundLocalEndpoint = null;
            _lastUdpRemote = null;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }
        Publish(TransportEventKind.StateChanged, null, ReadOnlyMemory<byte>.Empty, null, null);

        try
        {
            if (_options.Protocol == TransportProtocol.Tcp)
            {
                if (_options.Mode == TransportMode.Server)
                {
                    var endpoint = new IPEndPoint(ParseAddress(_options.LocalHost), _options.LocalPort);
                    _listener = new TcpListener(endpoint);
                    _listener.Start();
                    BoundLocalEndpoint = _listener.LocalEndpoint.ToString();
                    SetState(TransportSessionState.Listening);
                    _worker = AcceptTcpClientsAsync(_lifetime.Token);
                }
                else
                {
                    SetState(TransportSessionState.Connecting);
                    _worker = RunTcpClientAsync(_lifetime.Token);
                }
            }
            else
            {
                if (_options.Mode == TransportMode.Client)
                {
                    var localAddress = GetClientLocalAddress();
                    var remoteAddress = await ResolveRemoteAddressAsync(_options.RemoteHost, localAddress?.AddressFamily, _lifetime.Token).ConfigureAwait(false);
                    _udpClient = new UdpClient(remoteAddress.AddressFamily);
                    if (localAddress is not null || _options.LocalPort != 0)
                        _udpClient.Client.Bind(new IPEndPoint(localAddress ?? GetAnyAddress(remoteAddress.AddressFamily), _options.LocalPort));
                    _udpClient.Connect(remoteAddress, _options.RemotePort);
                    BoundLocalEndpoint = _udpClient.Client.LocalEndPoint?.ToString();
                    SetState(TransportSessionState.Connected);
                }
                else
                {
                    var endpoint = new IPEndPoint(ParseAddress(_options.LocalHost), _options.LocalPort);
                    _udpClient = new UdpClient(endpoint);
                    BoundLocalEndpoint = _udpClient.Client.LocalEndPoint?.ToString();
                    SetState(TransportSessionState.Listening);
                }
                _worker = ReceiveUdpAsync(_lifetime.Token);
            }

            if (_options.SendInterval is { } interval && interval > TimeSpan.Zero && !string.IsNullOrEmpty(_options.ScheduledPayload))
            {
                _scheduledSender = RunScheduledSenderAsync(interval, _options.ScheduledPayload, _lifetime.Token);
            }
        }
        catch (Exception ex)
        {
            if (_lifetime?.IsCancellationRequested == true) return;
            SetState(TransportSessionState.Faulted);
            Publish(TransportEventKind.Error, null, ReadOnlyMemory<byte>.Empty, ex.Message, ex);
            throw;
        }

        await Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) return _stopTask is { IsCompleted: false } ? _stopTask : _stopTask = StopCoreAsync(cancellationToken);
    }
    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? lifetime;
        lock (_gate)
        {
            if (_state is TransportSessionState.Stopped or TransportSessionState.Created)
            {
                return;
            }
            _state = TransportSessionState.Stopping;
            lifetime = _lifetime;
        }
        Publish(TransportEventKind.StateChanged, null, ReadOnlyMemory<byte>.Empty, null, null);
        lifetime?.Cancel();
        if (_startTask is { IsCompleted: false } starting)
        {
            try { await starting; }
            catch (Exception) when (lifetime?.IsCancellationRequested == true) { }
        }
        _listener?.Stop();
        _udpClient?.Dispose();

        TcpClient[] clients;
        lock (_gate)
        {
            clients = _activeClient is { } active ? _clients.Append(active).ToArray() : _clients.ToArray();
            _clients.Clear();
            _activeClient = null;
        }
        foreach (var client in clients)
        {
            client.Dispose();
        }

        var tasks = new[] { _worker, _scheduledSender }.Where(task => task is not null).Cast<Task>().ToArray();
        if (tasks.Length > 0)
        {
            try
            {
                // Complete cleanup even if the caller cancels its wait.
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException) when (lifetime?.IsCancellationRequested == true)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (SocketException) when (lifetime?.IsCancellationRequested == true)
            {
            }
        }

        Task[] readers;
        lock (_gate) readers = _clientReaders.ToArray();
        await Task.WhenAll(readers);

        lifetime?.Dispose();
        _lifetime = null;
        _udpClient = null; _listener = null; _worker = _scheduledSender = null;
        SetState(TransportSessionState.Stopped);
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (data.IsEmpty)
        {
            return;
        }

        if (_options.Protocol == TransportProtocol.Udp)
        {
            if (_udpClient is null)
            {
                throw new InvalidOperationException("Session is not started.");
            }
            if (_options.Mode == TransportMode.Server)
            {
                var target = _lastUdpRemote ?? (_options.LockFirstUdpPeer ? throw new InvalidOperationException("尚未锁定 UDP 对端，请等待首个数据报。") : new IPEndPoint(ParseAddress(_options.RemoteHost), _options.RemotePort));
                cancellationToken.ThrowIfCancellationRequested();
                await _udpClient.SendAsync(data, target, cancellationToken);
                Publish(TransportEventKind.Sent, target.ToString(), data, null, null);
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _udpClient.SendAsync(data, cancellationToken);
                Publish(TransportEventKind.Sent, $"{_options.RemoteHost}:{_options.RemotePort}", data, null, null);
            }
            return;
        }

        TcpClient[] clients;
        lock (_gate)
        {
            clients = _options.Mode == TransportMode.Client
                ? (_activeClient is null ? Array.Empty<TcpClient>() : new[] { _activeClient })
                : _clients.ToArray();
        }
        if (clients.Length == 0)
        {
            throw new InvalidOperationException("No TCP peer is connected.");
        }
        foreach (var client in clients)
        {
            await client.GetStream().WriteAsync(data, cancellationToken);
            Publish(TransportEventKind.Sent, client.Client?.RemoteEndPoint?.ToString(), data, null, null);
        }
    }

    public async Task SendToAsync(ReadOnlyMemory<byte> data, string remoteEndpoint, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(remoteEndpoint))
        {
            await SendAsync(data, cancellationToken);
            return;
        }

        if (_options.Protocol == TransportProtocol.Udp)
        {
            if (_udpClient is null) throw new InvalidOperationException("Session is not started.");
            if (!IPEndPoint.TryParse(remoteEndpoint, out var target)) throw new FormatException("无效的 UDP 对端地址。");
            if (_options.LockFirstUdpPeer && !Equals(target, _lastUdpRemote)) throw new InvalidOperationException("只能向本次会话锁定的 UDP 对端发送。");
            cancellationToken.ThrowIfCancellationRequested();
            await _udpClient.SendAsync(data, target, cancellationToken);
            Publish(TransportEventKind.Sent, target.ToString(), data, null, null);
            return;
        }

        TcpClient? client;
        lock (_gate)
        {
            client = _options.Mode == TransportMode.Client
                ? _activeClient
                : _clients.FirstOrDefault(item => string.Equals(item.Client?.RemoteEndPoint?.ToString(), remoteEndpoint, StringComparison.OrdinalIgnoreCase));
        }
        if (client is null) throw new InvalidOperationException($"客户端 {remoteEndpoint} 已断开。");
        await client.GetStream().WriteAsync(data, cancellationToken);
        Publish(TransportEventKind.Sent, client.Client?.RemoteEndPoint?.ToString(), data, null, null);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _listener?.Dispose();
        _udpClient?.Dispose();
    }

    private async Task AcceptTcpClientsAsync(CancellationToken cancellationToken)
    {
        if (_listener is null)
        {
            return;
        }
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested) { client.Dispose(); break; }
                client.NoDelay = true;
                lock (_gate) _clients.Add(client);
                SetState(TransportSessionState.Connected);
                Publish(TransportEventKind.Connected, client.Client?.RemoteEndPoint?.ToString(), ReadOnlyMemory<byte>.Empty, null, null);
                var reader = HandleTcpClientAsync(client, cancellationToken);
                lock (_gate) _clientReaders.Add(reader);
                _ = RemoveFinishedReaderAsync(reader);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Publish(TransportEventKind.Error, null, ReadOnlyMemory<byte>.Empty, ex.Message, ex);
        }
    }

    private async Task RemoveFinishedReaderAsync(Task reader)
    {
        try { await reader; }
        finally { lock (_gate) _clientReaders.Remove(reader); }
    }

    private async Task RunTcpClientAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var shouldReconnect = false;
            try
            {
                var localAddress = GetClientLocalAddress();
                var remoteAddress = await ResolveRemoteAddressAsync(_options.RemoteHost, localAddress?.AddressFamily, cancellationToken).ConfigureAwait(false);
                using var client = new TcpClient(remoteAddress.AddressFamily) { NoDelay = true };
                if (localAddress is not null || _options.LocalPort != 0)
                    client.Client.Bind(new IPEndPoint(localAddress ?? GetAnyAddress(remoteAddress.AddressFamily), _options.LocalPort));
                using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    connectTimeout.CancelAfter(_options.ConnectTimeoutMilliseconds);
                    try { await client.ConnectAsync(remoteAddress, _options.RemotePort, connectTimeout.Token); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    { throw new TimeoutException($"TCP 连接超时（{_options.ConnectTimeoutMilliseconds} ms）。"); }
                }
                cancellationToken.ThrowIfCancellationRequested();
                BoundLocalEndpoint = client.Client.LocalEndPoint?.ToString();
                lock (_gate) _activeClient = client;
                SetState(TransportSessionState.Connected);
                Publish(TransportEventKind.Connected, client.Client?.RemoteEndPoint?.ToString(), ReadOnlyMemory<byte>.Empty, null, null);
                await ReadTcpStreamAsync(client, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Publish(TransportEventKind.Error, $"{_options.RemoteHost}:{_options.RemotePort}", ReadOnlyMemory<byte>.Empty, ex.Message, ex);
            }
            finally
            {
                lock (_gate) _activeClient = null;
                if (!cancellationToken.IsCancellationRequested)
                {
                    shouldReconnect = _options.AutoReconnect;
                    SetState(shouldReconnect ? TransportSessionState.Connecting : TransportSessionState.Faulted);
                    if (shouldReconnect)
                    {
                        try { await Task.Delay(_options.ReconnectDelay, cancellationToken); }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                    }
                }
            }

            if (cancellationToken.IsCancellationRequested || !shouldReconnect)
            {
                break;
            }
        }
    }

    private async Task HandleTcpClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        string? endpoint = null;
        try
        {
            endpoint = client.Client?.RemoteEndPoint?.ToString();
            await ReadTcpStreamAsync(client, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            if (!cancellationToken.IsCancellationRequested) Publish(TransportEventKind.Error, endpoint, ReadOnlyMemory<byte>.Empty, ex.Message, ex);
        }
        finally
        {
            bool noClients;
            lock (_gate) { _clients.Remove(client); noClients = _clients.Count == 0; }
            client.Dispose();
            Publish(TransportEventKind.Disconnected, endpoint, ReadOnlyMemory<byte>.Empty, null, null);
            if (!cancellationToken.IsCancellationRequested && State == TransportSessionState.Connected && noClients)
            {
                SetState(TransportSessionState.Listening);
            }
        }
    }

    private async Task ReadTcpStreamAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        var buffer = new byte[_options.ReceiveBufferSize];
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return;
            }
            var data = buffer.AsMemory(0, read).ToArray();
            var text = Encoding.UTF8.GetString(data);
            if (_options.TreatAsHttp)
            {
                var http = new HttpDiagnosticsParser().Parse(data);
                text = FormatHttpDiagnostics(http, text);
                if (_options.Mode == TransportMode.Server && http.IsRequest && http.StartLine is not null)
                {
                    await SendHttpResponseAsync(client, cancellationToken);
                }
            }
            Publish(TransportEventKind.Received, client.Client?.RemoteEndPoint?.ToString(), data, text, null);
        }
    }

    private static string FormatHttpDiagnostics(HttpMessageInfo info, string fallback)
    {
        if (info.StartLine is null) return fallback;
        var headers = string.Join("\r\n", info.Headers.Select(header => $"{header.Name}: {header.Value}"));
        return string.IsNullOrEmpty(headers) ? info.StartLine : $"{info.StartLine}\r\n{headers}";
    }

    private async Task SendHttpResponseAsync(TcpClient client, CancellationToken cancellationToken)
    {
        const string body = "EmbedDesk OK";
        var response = $"HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: keep-alive\r\nX-EmbedDesk: true\r\n\r\n{body}";
        var bytes = Encoding.UTF8.GetBytes(response);
        await client.GetStream().WriteAsync(bytes, cancellationToken);
        Publish(TransportEventKind.Sent, client.Client?.RemoteEndPoint?.ToString(), bytes, response, null);
    }
    private async Task ReceiveUdpAsync(CancellationToken cancellationToken)
    {
        if (_udpClient is null)
        {
            return;
        }
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await _udpClient.ReceiveAsync(cancellationToken);
                if (_options.LockFirstUdpPeer && _lastUdpRemote is not null && !_lastUdpRemote.Equals(result.RemoteEndPoint)) continue;
                var firstPeer = _lastUdpRemote is null;
                _lastUdpRemote = result.RemoteEndPoint;
                if (firstPeer && _options.LockFirstUdpPeer) Publish(TransportEventKind.Info, result.RemoteEndPoint.ToString(), ReadOnlyMemory<byte>.Empty, "UDP 已锁定首个发送端", null);
                SetState(TransportSessionState.Connected);
                Publish(TransportEventKind.Received, result.RemoteEndPoint.ToString(), result.Buffer, Encoding.UTF8.GetString(result.Buffer), null);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Publish(TransportEventKind.Error, null, ReadOnlyMemory<byte>.Empty, ex.Message, ex);
        }
    }

    private async Task RunScheduledSenderAsync(TimeSpan interval, string payload, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        var data = Encoding.UTF8.GetBytes(_options.UseCrLf ? payload + "\r\n" : payload);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try { await SendAsync(data, cancellationToken); }
                catch (Exception ex) when (ex is InvalidOperationException or SocketException or IOException)
                {
                    Publish(TransportEventKind.Error, null, ReadOnlyMemory<byte>.Empty, $"定时发送失败: {ex.Message}", ex);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void SetState(TransportSessionState state)
    {
        lock (_gate)
        {
            if (_state == TransportSessionState.Stopping && state != TransportSessionState.Stopped) return;
            if (state == _state) return;
            _state = state;
        }
        Publish(TransportEventKind.StateChanged, null, ReadOnlyMemory<byte>.Empty, null, null);
    }

    private void Publish(TransportEventKind kind, string? remote, ReadOnlyMemory<byte> data, string? text, Exception? error)
    {
        MessageReceived?.Invoke(this, new TransportMessage(
            DateTimeOffset.Now,
            Id,
            kind,
            State,
            remote,
            data,
            text,
            error?.ToString()));
    }

    private static IPAddress ParseAddress(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host == "*") return IPAddress.Any;
        if (IPAddress.TryParse(host, out var address)) return address;
        return Dns.GetHostAddresses(host).First(address => address.AddressFamily == AddressFamily.InterNetwork);
    }

    private IPAddress? GetClientLocalAddress()
    {
        var value = _options.ClientLocalAddress;
        // Preserve callers that used LocalHost to choose a TCP/UDP client's source address before ClientLocalAddress existed.
        if (string.IsNullOrWhiteSpace(value) && _options.LocalHost is not ("0.0.0.0" or "::" or "*")) value = _options.LocalHost;
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!IPAddress.TryParse(value, out var address)) throw new ArgumentException("客户端出口地址必须是本机 IP 地址。", nameof(_options.ClientLocalAddress));
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    private static async Task<IPAddress> ResolveRemoteAddressAsync(string host, AddressFamily? requiredFamily, CancellationToken cancellationToken)
    {
        var addresses = IPAddress.TryParse(host, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        var usable = addresses.Select(address => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address)
            .Where(address => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            .Where(address => requiredFamily is null || address.AddressFamily == requiredFamily)
            .OrderBy(address => address.AddressFamily != AddressFamily.InterNetwork)
            .FirstOrDefault();
        return usable ?? throw new SocketException((int)SocketError.AddressFamilyNotSupported);
    }

    private static IPAddress GetAnyAddress(AddressFamily family) => family switch
    {
        AddressFamily.InterNetwork => IPAddress.Any,
        AddressFamily.InterNetworkV6 => IPAddress.IPv6Any,
        _ => throw new ArgumentOutOfRangeException(nameof(family))
    };
}






