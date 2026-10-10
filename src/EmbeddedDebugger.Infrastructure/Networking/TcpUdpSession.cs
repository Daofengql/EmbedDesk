using System.Net;
using System.Net.Sockets;
using System.Text;
using EmbeddedDebugger.Core.Abstractions;
using EmbeddedDebugger.Core.Models;
using EmbeddedDebugger.Infrastructure.Diagnostics;

namespace EmbeddedDebugger.Infrastructure.Networking;

internal sealed class TcpUdpSession : ITransportSession
{
    private readonly object _gate = new();
    private readonly List<TcpClient> _clients = new();
    private readonly TransportOptions _options;
    private CancellationTokenSource? _lifetime;
    private TcpListener? _listener;
    private UdpClient? _udpClient;
    private TcpClient? _activeClient;
    private IPEndPoint? _lastUdpRemote;
    private Task? _worker;
    private Task? _scheduledSender;
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

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_state is not (TransportSessionState.Created or TransportSessionState.Stopped or TransportSessionState.Faulted))
            {
                return;
            }
            _state = TransportSessionState.Starting;
            BoundLocalEndpoint = null;
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
                var endpoint = new IPEndPoint(ParseAddress(_options.LocalHost), _options.LocalPort);
                _udpClient = new UdpClient(endpoint);
                BoundLocalEndpoint = _udpClient.Client.LocalEndPoint?.ToString();
                if (_options.Mode == TransportMode.Client)
                {
                    _udpClient.Connect(_options.RemoteHost, _options.RemotePort);
                    SetState(TransportSessionState.Connected);
                }
                else
                {
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
            SetState(TransportSessionState.Faulted);
            Publish(TransportEventKind.Error, null, ReadOnlyMemory<byte>.Empty, ex.Message, ex);
            throw;
        }

        await Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
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
        _listener?.Stop();
        _udpClient?.Dispose();

        TcpClient[] clients;
        lock (_gate)
        {
            clients = _clients.ToArray();
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
                await Task.WhenAll(tasks).WaitAsync(cancellationToken);
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

        lifetime?.Dispose();
        _lifetime = null;
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
                var target = _lastUdpRemote ?? new IPEndPoint(ParseAddress(_options.RemoteHost), _options.RemotePort);
                cancellationToken.ThrowIfCancellationRequested();
                await _udpClient.SendAsync(data.ToArray(), target);
                Publish(TransportEventKind.Sent, target.ToString(), data, null, null);
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _udpClient.SendAsync(data.ToArray());
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
                client.NoDelay = true;
                lock (_gate) _clients.Add(client);
                SetState(TransportSessionState.Connected);
                Publish(TransportEventKind.Connected, client.Client?.RemoteEndPoint?.ToString(), ReadOnlyMemory<byte>.Empty, null, null);
                _ = HandleTcpClientAsync(client, cancellationToken);
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

    private async Task RunTcpClientAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var shouldReconnect = false;
            try
            {
                using var client = new TcpClient(new IPEndPoint(ParseAddress(_options.LocalHost), _options.LocalPort)) { NoDelay = true };
                await client.ConnectAsync(_options.RemoteHost, _options.RemotePort, cancellationToken);
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
        try
        {
            await ReadTcpStreamAsync(client, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Publish(TransportEventKind.Error, client.Client?.RemoteEndPoint?.ToString(), ReadOnlyMemory<byte>.Empty, ex.Message, ex);
        }
        finally
        {
            lock (_gate) _clients.Remove(client);
            string? endpoint = null;
            try { endpoint = client.Client?.RemoteEndPoint?.ToString(); } catch (ObjectDisposedException) { }
            client.Dispose();
            Publish(TransportEventKind.Disconnected, endpoint, ReadOnlyMemory<byte>.Empty, null, null);
            if (State == TransportSessionState.Connected && _clients.Count == 0)
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
        const string body = "Embedded Debugger OK";
        var response = $"HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: keep-alive\r\nX-Embedded-Debugger: true\r\n\r\n{body}";
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
                _lastUdpRemote = result.RemoteEndPoint;
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
        lock (_gate) _state = state;
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
}






