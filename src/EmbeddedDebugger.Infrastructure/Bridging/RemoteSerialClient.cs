using System.Net.Sockets;
using System.Text;
using EmbeddedDebugger.Core.Abstractions;
using EmbeddedDebugger.Core.Models;
using EmbeddedDebugger.Protocols;

namespace EmbeddedDebugger.Infrastructure.Bridging;

public sealed class RemoteSerialClient : IRemoteSerialClient
{
    private sealed class Connection(TcpClient client) : IDisposable
    {
        public TcpClient Client { get; } = client;
        public NetworkStream Stream { get; } = client.GetStream();
        public CancellationTokenSource Lifetime { get; } = new();
        public Task Reader { get; set; } = Task.CompletedTask;
        public void Abort() { Lifetime.Cancel(); Client.Dispose(); }
        public void Dispose() { Client.Dispose(); Lifetime.Dispose(); }
    }
    private readonly object _gate = new();
    private readonly Dictionary<ushort, RemoteSerialPort> _ports = new();
    private readonly HashSet<RemoteChannel> _channels = new();
    private readonly Dictionary<ushort, RemoteChannel> _active = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1), _connectionLock = new(1, 1);
    private Connection? _connection;
    private bool _disposed;

    public bool IsConnected => _connection is { } connection && !connection.Lifetime.IsCancellationRequested;
    public IReadOnlyList<RemoteSerialPort> Ports { get { lock (_gate) return _ports.Values.OrderBy(port => port.ChannelId).ToArray(); } }
    public event EventHandler? PortsChanged;
    public event EventHandler? ConnectionChanged;

    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await DisconnectCoreAsync().ConfigureAwait(false);
            var client = new TcpClient { NoDelay = true };
            try { await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false); }
            catch { client.Dispose(); throw; }
            var connection = new Connection(client);
            _connection = connection;
            connection.Reader = Task.Run(() => ReadAsync(connection));
            ConnectionChanged?.Invoke(this, EventArgs.Empty);
        }
        finally { _connectionLock.Release(); }
    }

    public async Task DisconnectAsync()
    {
        await _connectionLock.WaitAsync().ConfigureAwait(false);
        try { await DisconnectCoreAsync().ConfigureAwait(false); }
        finally { _connectionLock.Release(); }
    }
    private async Task DisconnectCoreAsync()
    {
        var connection = _connection;
        if (connection is null) return;
        connection.Abort();
        await connection.Reader.ConfigureAwait(false);
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try { _connection = null; connection.Dispose(); }
        finally { _writeLock.Release(); }
    }

    public ISerialPortChannel CreateChannel(string portName, SerialPortSettings settings)
    {
        lock (_gate)
        {
            if (!IsConnected || _connection is null) throw new InvalidOperationException("网络串口服务未连接");
            var port = _ports.Values.FirstOrDefault(port => port.PortName == portName) ?? throw new InvalidOperationException("远程串口已离线");
            var wireSettings = new SerialPortSettings
            {
                PortName = port.DisplayName, BaudRate = settings.BaudRate, DataBits = settings.DataBits,
                Parity = settings.Parity, StopBits = settings.StopBits, Handshake = settings.Handshake,
                DtrEnable = settings.DtrEnable, RtsEnable = settings.RtsEnable
            };
            var channel = new RemoteChannel(this, _connection, port.ChannelId, settings, wireSettings);
            _channels.Add(channel);
            return channel;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _connectionLock.WaitAsync().ConfigureAwait(false);
        try { if (_disposed) return; _disposed = true; await DisconnectCoreAsync().ConfigureAwait(false); }
        finally { _connectionLock.Release(); }
        // Semaphores may still have cancellation continuations from a closing tab.
    }

    private async Task ReadAsync(Connection connection)
    {
        try
        {
            while (await MultiplexFrameCodec.ReadAsync(connection.Stream, connection.Lifetime.Token).ConfigureAwait(false) is { } frame)
            {
                if (frame.Flags == MultiplexFrameFlags.Open)
                {
                    var name = Encoding.UTF8.GetString(frame.Payload.Span);
                    if (frame.ChannelId == 0 || string.IsNullOrWhiteSpace(name) || name.Length > 128)
                        throw new InvalidDataException("服务端返回了无效的串口映射。");
                    lock (_gate) _ports[frame.ChannelId] = new(frame.ChannelId, $"REMOTE:{frame.ChannelId}", name);
                    PortsChanged?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    RemoteChannel? channel;
                    lock (_gate) _active.TryGetValue(frame.ChannelId, out channel);
                    if (frame.Flags == MultiplexFrameFlags.OpenAcknowledged) channel?.Acknowledge();
                    else if (frame.Flags == MultiplexFrameFlags.Error)
                    {
                        var message = frame.Payload.IsEmpty ? "服务端串口操作失败" : Encoding.UTF8.GetString(frame.Payload.Span);
                        // After opening, a COM failure must also take the visible session offline.
                        if (channel is null || !channel.RejectOpen(message)) throw new IOException(message);
                    }
                    else if (frame.Flags == MultiplexFrameFlags.Data) channel?.Receive(frame.Payload);
                    else if (frame.Flags == MultiplexFrameFlags.Close)
                    {
                        channel?.MarkClosed();
                        lock (_gate) { _active.Remove(frame.ChannelId); _ports.Remove(frame.ChannelId); }
                        PortsChanged?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException or InvalidDataException) { }
        finally
        {
            connection.Abort();
            lock (_gate)
            {
                _ports.Clear();
                foreach (var channel in _channels) channel.MarkClosed();
                _active.Clear();
            }
            PortsChanged?.Invoke(this, EventArgs.Empty);
            ConnectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task SendAsync(Connection connection, ushort id, MultiplexFrameFlags flags, ReadOnlyMemory<byte> data, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        // Check connection identity before touching a potentially retired CancellationTokenSource.
        await _writeLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (connection != _connection || !IsConnected) throw new InvalidOperationException("网络串口服务未连接");
            using var disconnected = connection.Lifetime.Token.Register(lifetime.Cancel);
            var chunkSize = flags == MultiplexFrameFlags.Data ? MultiplexFrameCodec.DataChunkSize : Math.Max(1, data.Length);
            var offset = 0;
            do
            {
                lifetime.Token.ThrowIfCancellationRequested();
                var count = Math.Min(chunkSize, data.Length - offset);
                await connection.Stream.WriteAsync(MultiplexFrameCodec.Encode(new(id, flags, data.Slice(offset, count))), lifetime.Token).ConfigureAwait(false);
                offset += count;
            } while (offset < data.Length);
        }
        catch
        {
            // An interrupted frame cannot safely share the byte stream with another request.
            if (connection == _connection) connection.Abort();
            throw;
        }
        finally { _writeLock.Release(); }
    }

    private sealed class RemoteChannel(RemoteSerialClient owner, Connection connection, ushort id,
        SerialPortSettings settings, SerialPortSettings wireSettings) : ISerialPortChannel
    {
        private readonly SemaphoreSlim _operations = new(1, 1);
        private TaskCompletionSource<bool>? _ready;
        private volatile bool _isOpen;
        private bool _disposed;
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public SerialPortSettings Settings => settings;
        public bool IsOpen => _isOpen;
        public event EventHandler<ReadOnlyMemory<byte>>? DataReceived;

        public async Task OpenAsync(CancellationToken cancellationToken = default)
        {
            await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (IsOpen) return;
                var payload = SerialBridgeControlCodec.EncodeSettings(wireSettings);
                if (!SerialBridgeControlCodec.TryDecodeSettings(payload, out _)) throw new ArgumentException("串口参数无效。");
                lock (owner._gate)
                {
                    if (connection != owner._connection || !owner.IsConnected) throw new InvalidOperationException("远程串口已离线");
                    if (!owner._active.TryAdd(id, this)) throw new InvalidOperationException("远程串口已在其他标签打开");
                    _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                try
                {
                    await owner.SendAsync(connection, id, MultiplexFrameFlags.Open, payload, cancellationToken).ConfigureAwait(false);
                    await _ready.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsOpen) throw new IOException("远程串口打开期间连接已断开。");
                }
                catch (Exception ex)
                {
                    // Canceling a handshake must release any COM the server opened just before cancellation.
                    if (ex is OperationCanceledException or TimeoutException && connection == owner._connection) connection.Abort();
                    lock (owner._gate) if (owner._active.GetValueOrDefault(id) == this) owner._active.Remove(id);
                    MarkClosed();
                    throw;
                }
                finally { _ready = null; }
            }
            finally { _operations.Release(); }
        }
        public void Acknowledge()
        {
            if (_ready is { } ready && !ready.Task.IsCompleted) { _isOpen = true; ready.TrySetResult(true); }
        }
        public bool RejectOpen(string message) => _ready?.TrySetException(new InvalidOperationException(message)) == true;
        public void Receive(ReadOnlyMemory<byte> data)
        {
            // Preserve immediate startup output received while the Open acknowledgement is in flight.
            if (IsOpen || _ready is not null) DataReceived?.Invoke(this, data);
        }
        public void MarkClosed() { _isOpen = false; _ready?.TrySetException(new IOException("网络串口连接已断开")); }
        public async Task CloseAsync(CancellationToken cancellationToken = default)
        {
            await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await CloseCoreAsync(cancellationToken).ConfigureAwait(false); }
            finally { _operations.Release(); }
        }
        private async Task CloseCoreAsync(CancellationToken token)
        {
            var wasOpen = IsOpen; _isOpen = false;
            try
            {
                if (wasOpen && connection == owner._connection && owner.IsConnected)
                    await owner.SendAsync(connection, id, MultiplexFrameFlags.Close, ReadOnlyMemory<byte>.Empty, token).ConfigureAwait(false);
            }
            finally { lock (owner._gate) if (owner._active.GetValueOrDefault(id) == this) owner._active.Remove(id); }
        }
        public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!IsOpen) throw new InvalidOperationException("远程串口未打开");
                await owner.SendAsync(connection, id, MultiplexFrameFlags.Data, data, cancellationToken).ConfigureAwait(false);
            }
            finally { _operations.Release(); }
        }
        public async ValueTask DisposeAsync()
        {
            await _operations.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) return;
                _disposed = true;
                try { await CloseCoreAsync(CancellationToken.None).ConfigureAwait(false); }
                finally { lock (owner._gate) owner._channels.Remove(this); }
            }
            finally { _operations.Release(); }
        }
    }
}
