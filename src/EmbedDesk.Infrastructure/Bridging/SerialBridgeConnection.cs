using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using EmbedDesk.Core.Abstractions;
using EmbedDesk.Protocols;
using Microsoft.Extensions.Logging;

namespace EmbedDesk.Infrastructure.Bridging;

/// <summary>Owns one TCP peer and the COM handles opened by that peer.</summary>
internal sealed class SerialBridgeConnection : IAsyncDisposable
{
    private sealed record OpenChannel(ISerialPortChannel Port, EventHandler<ReadOnlyMemory<byte>> Handler, CancellationTokenSource Lifetime);
    private sealed record Outgoing(MultiplexFrame Frame, CancellationToken ChannelToken);
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly IReadOnlyDictionary<ushort, string> _mappings;
    private readonly ISerialPortService _serial;
    private readonly ILogger _logger;
    private readonly Dictionary<ushort, OpenChannel> _opened = new();
    private readonly CancellationTokenSource _lifetime;
    // At most 4 MiB of 32 KiB data chunks are queued; serial callbacks apply backpressure.
    private readonly Channel<Outgoing> _outgoing = Channel.CreateBounded<Outgoing>(new BoundedChannelOptions(128)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, AllowSynchronousContinuations = false });
    private readonly Task _completion;
    private int _disposed;

    public SerialBridgeConnection(TcpClient client, IReadOnlyDictionary<ushort, string> mappings,
        ISerialPortService serial, ILogger logger, CancellationToken token)
    {
        _client = client; client.NoDelay = true; _stream = client.GetStream();
        _mappings = mappings; _serial = serial; _logger = logger;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        // SerialPort.Open/Write/Close are synchronous. Never run them on the WPF dispatcher.
        _completion = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        var sender = SendAsync();
        try
        {
            foreach (var pair in _mappings)
                await QueueAsync(pair.Key, MultiplexFrameFlags.Open, Encoding.UTF8.GetBytes(pair.Value)).ConfigureAwait(false);
            while (await MultiplexFrameCodec.ReadAsync(_stream, _lifetime.Token).ConfigureAwait(false) is { } frame)
            {
                switch (frame.Flags)
                {
                    case MultiplexFrameFlags.Open:
                        await OpenAsync(frame).ConfigureAwait(false);
                        break;
                    case MultiplexFrameFlags.Close:
                        await CloseAsync(frame.ChannelId).ConfigureAwait(false);
                        break;
                    case MultiplexFrameFlags.Data:
                        if (!_opened.TryGetValue(frame.ChannelId, out var opened))
                        { await ErrorAsync(frame.ChannelId, "串口尚未打开，请先发送串口参数。").ConfigureAwait(false); break; }
                        try
                        {
                            for (var offset = 0; offset < frame.Payload.Length; offset += MultiplexFrameCodec.DataChunkSize)
                                await opened.Port.SendAsync(frame.Payload.Slice(offset, Math.Min(MultiplexFrameCodec.DataChunkSize, frame.Payload.Length - offset)), _lifetime.Token).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or UnauthorizedAccessException)
                        {
                            await CloseAsync(frame.ChannelId).ConfigureAwait(false);
                            await ErrorAsync(frame.ChannelId, "串口发送失败：" + ex.Message).ConfigureAwait(false);
                        }
                        break;
                    case MultiplexFrameFlags.Heartbeat:
                        break;
                    default:
                        throw new InvalidDataException("客户端发送了无效的控制帧。");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException or OperationCanceledException or ObjectDisposedException or ChannelClosedException)
        { if (!_lifetime.IsCancellationRequested) _logger.LogWarning(ex, "Serial bridge peer disconnected"); }
        finally
        {
            Abort();
            foreach (var id in _opened.Keys.ToArray()) await CloseAsync(id).ConfigureAwait(false);
            _outgoing.Writer.TryComplete();
            await sender.ConfigureAwait(false);
        }
    }

    private async Task OpenAsync(MultiplexFrame frame)
    {
        if (!_mappings.TryGetValue(frame.ChannelId, out var name) ||
            !SerialBridgeControlCodec.TryDecodeSettings(frame.Payload.Span, out var settings) ||
            !string.Equals(settings.PortName, name, StringComparison.OrdinalIgnoreCase))
        { await ErrorAsync(frame.ChannelId, "串口参数无效或 COM 未映射。").ConfigureAwait(false); return; }
        if (_opened.ContainsKey(frame.ChannelId))
        { await ErrorAsync(frame.ChannelId, "该通道已打开，请先关闭后再修改参数。").ConfigureAwait(false); return; }
        try
        {
            var port = _serial.CreateChannel(settings);
            var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var token = lifetime.Token;
            EventHandler<ReadOnlyMemory<byte>> handler = (_, bytes) => Publish(frame.ChannelId, bytes, token);
            _opened.Add(frame.ChannelId, new(port, handler, lifetime));
            port.DataReceived += handler;
            await port.OpenAsync(_lifetime.Token).ConfigureAwait(false);
            await QueueAsync(frame.ChannelId, MultiplexFrameFlags.OpenAcknowledged, ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or TimeoutException)
        {
            await CloseAsync(frame.ChannelId).ConfigureAwait(false);
            await ErrorAsync(frame.ChannelId, $"打开 {name} 失败：{ex.Message}").ConfigureAwait(false);
        }
    }

    private async Task CloseAsync(ushort id)
    {
        if (!_opened.Remove(id, out var opened)) return;
        opened.Lifetime.Cancel(); // Unblock callbacks before closing the serial driver.
        opened.Port.DataReceived -= opened.Handler;
        try { await opened.Port.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not close mapped COM channel {ChannelId}", id); }
        finally { opened.Lifetime.Dispose(); }
    }

    private void Publish(ushort id, ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        try
        {
            for (var offset = 0; offset < bytes.Length; offset += MultiplexFrameCodec.DataChunkSize)
            {
                token.ThrowIfCancellationRequested();
                var chunk = bytes.Slice(offset, Math.Min(MultiplexFrameCodec.DataChunkSize, bytes.Length - offset)).ToArray();
                _outgoing.Writer.WriteAsync(new(new(id, MultiplexFrameFlags.Data, chunk), token), token).AsTask().GetAwaiter().GetResult();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException or ObjectDisposedException) { }
    }

    private ValueTask QueueAsync(ushort id, MultiplexFrameFlags flags, ReadOnlyMemory<byte> payload) =>
        _outgoing.Writer.WriteAsync(new(new(id, flags, payload), _lifetime.Token), _lifetime.Token);
    private ValueTask ErrorAsync(ushort id, string message) => QueueAsync(id, MultiplexFrameFlags.Error, Encoding.UTF8.GetBytes(message.Length > 500 ? message[..500] : message));

    private async Task SendAsync()
    {
        try
        {
            await foreach (var item in _outgoing.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (item.ChannelToken.IsCancellationRequested) continue;
                await _stream.WriteAsync(MultiplexFrameCodec.Encode(item.Frame), _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        { if (!_lifetime.IsCancellationRequested) _logger.LogWarning(ex, "Serial bridge send failed"); Abort(); }
    }

    private void Abort() { _lifetime.Cancel(); _client.Dispose(); }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Abort(); await _completion.ConfigureAwait(false); _lifetime.Dispose();
    }
}
