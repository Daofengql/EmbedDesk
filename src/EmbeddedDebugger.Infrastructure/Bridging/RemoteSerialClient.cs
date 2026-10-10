using System.Buffers;
using System.Net.Sockets;
using System.Text;
using EmbeddedDebugger.Core.Abstractions;
using EmbeddedDebugger.Core.Models;
using EmbeddedDebugger.Protocols;

namespace EmbeddedDebugger.Infrastructure.Bridging;

public sealed class RemoteSerialClient : IRemoteSerialClient
{
    private readonly object _gate = new();
    private readonly Dictionary<ushort, RemoteSerialPort> _ports = new();
    private readonly HashSet<RemoteChannel> _channels = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private TcpClient? _client;
    private CancellationTokenSource? _lifetime;
    private Task? _reader;

    public bool IsConnected => _client?.Connected == true && _lifetime?.IsCancellationRequested == false;
    public IReadOnlyList<RemoteSerialPort> Ports { get { lock (_gate) return _ports.Values.OrderBy(port => port.ChannelId).ToArray(); } }
    public event EventHandler? PortsChanged;
    public event EventHandler? ConnectionChanged;

    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        await DisconnectAsync();
        var client = new TcpClient { NoDelay = true };
        try { await client.ConnectAsync(host, port, cancellationToken); }
        catch { client.Dispose(); throw; }
        _client = client;
        _lifetime = new CancellationTokenSource();
        _reader = ReadAsync(client.GetStream(), _lifetime.Token);
        ConnectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task DisconnectAsync()
    {
        _lifetime?.Cancel();
        _client?.Dispose();
        if (_reader is not null) await _reader;
        _reader = null;
        _client = null;
        _lifetime?.Dispose();
        _lifetime = null;
        ClearPorts();
    }

    public ISerialPortChannel CreateChannel(string portName, SerialPortSettings settings)
    {
        lock (_gate)
        {
            var port = _ports.Values.FirstOrDefault(port => port.PortName == portName) ?? throw new InvalidOperationException("远程串口已离线");
            var channel = new RemoteChannel(this, port.ChannelId, settings);
            _channels.Add(channel);
            return channel;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _writeLock.Dispose();
    }

    private async Task ReadAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var bytes = new byte[16384];
        try
        {
            while (!token.IsCancellationRequested)
            {
                var count = await stream.ReadAsync(bytes, token);
                if (count == 0) break;
                buffer.Write(bytes.AsSpan(0, count));
                while (MultiplexFrameCodec.TryDecode(ref buffer, out var frame) && frame is not null)
                {
                    if (frame.Flags == MultiplexFrameFlags.Open)
                    {
                        var name = frame.Payload.IsEmpty ? $"通道 {frame.ChannelId}" : Encoding.UTF8.GetString(frame.Payload.Span);
                        lock (_gate) _ports[frame.ChannelId] = new RemoteSerialPort(frame.ChannelId, $"REMOTE:{frame.ChannelId}", name);
                        PortsChanged?.Invoke(this, EventArgs.Empty);
                    }
                    else if (frame.Flags == MultiplexFrameFlags.Close)
                    {
                        lock (_gate) _ports.Remove(frame.ChannelId);
                        PortsChanged?.Invoke(this, EventArgs.Empty);
                    }
                    else if (frame.Flags == MultiplexFrameFlags.Data)
                    {
                        RemoteChannel[] channels;
                        lock (_gate) channels = _channels.Where(channel => channel.ChannelId == frame.ChannelId && channel.IsOpen).ToArray();
                        foreach (var channel in channels) channel.Receive(frame.Payload);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException or InvalidDataException) { }
        finally
        {
            _lifetime?.Cancel();
            ClearPorts();
            ConnectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ClearPorts()
    {
        lock (_gate)
        {
            _ports.Clear();
            foreach (var channel in _channels) channel.MarkClosed();
        }
        PortsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task SendAsync(ushort id, ReadOnlyMemory<byte> data, CancellationToken token)
    {
        if (!IsConnected || _client is null) throw new InvalidOperationException("网络串口服务未连接");
        var frame = MultiplexFrameCodec.Encode(new MultiplexFrame(id, MultiplexFrameFlags.Data, data));
        await _writeLock.WaitAsync(token);
        try { await _client.GetStream().WriteAsync(frame, token); }
        finally { _writeLock.Release(); }
    }

    private sealed class RemoteChannel(RemoteSerialClient owner, ushort channelId, SerialPortSettings settings) : ISerialPortChannel
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public ushort ChannelId => channelId;
        public SerialPortSettings Settings => settings;
        public bool IsOpen { get; private set; }
        public event EventHandler<ReadOnlyMemory<byte>>? DataReceived;
        public Task OpenAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!owner.IsConnected) throw new InvalidOperationException("远程串口已离线");
            IsOpen = true;
            return Task.CompletedTask;
        }
        public Task CloseAsync(CancellationToken cancellationToken = default) { IsOpen = false; return Task.CompletedTask; }
        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => IsOpen
            ? owner.SendAsync(channelId, data, cancellationToken)
            : throw new InvalidOperationException("远程串口未打开");
        public void Receive(ReadOnlyMemory<byte> data) => DataReceived?.Invoke(this, data);
        public void MarkClosed() => IsOpen = false;
        public ValueTask DisposeAsync()
        {
            IsOpen = false;
            lock (owner._gate) owner._channels.Remove(this);
            return ValueTask.CompletedTask;
        }
    }
}
