using System.Threading.Channels;
using EmbeddedDebugger.Core.Abstractions;

namespace EmbeddedDebugger.Infrastructure.Firmware;

/// <summary>AN3155 USART bootloader. Caller opens 8E1 and runs operations sequentially.</summary>
public sealed class Stm32BootloaderClient : IAsyncDisposable
{
    private readonly ISerialPortChannel _channel;
    private readonly Channel<byte> _received = Channel.CreateBounded<byte>(65536);
    private readonly CancellationTokenSource _lifetime = new();
    private byte[] _commands = [];
    private bool _disposed;
    public byte Version { get; private set; }

    public Stm32BootloaderClient(ISerialPortChannel channel)
    {
        _channel = channel;
        _channel.DataReceived += OnDataReceived;
    }

    public async Task ConnectAsync(CancellationToken token = default)
    {
        await SendAsync(new byte[] { 0x7F }, token).ConfigureAwait(false);
        await AckAsync(token).ConfigureAwait(false);
        await CommandAsync(0x00, token).ConfigureAwait(false);
        var count = await ReadAsync(token).ConfigureAwait(false);
        Version = await ReadAsync(token).ConfigureAwait(false);
        _commands = new byte[count];
        for (var i = 0; i < count; i++) _commands[i] = await ReadAsync(token).ConfigureAwait(false);
        await AckAsync(token).ConfigureAwait(false);
    }

    public async Task<ushort> ReadDeviceIdAsync(CancellationToken token = default)
    {
        Require(0x02);
        await CommandAsync(0x02, token).ConfigureAwait(false);
        var count = await ReadAsync(token).ConfigureAwait(false);
        if (count != 1) throw new InvalidDataException("设备 ID 响应长度无效。");
        var high = await ReadAsync(token).ConfigureAwait(false);
        var low = await ReadAsync(token).ConfigureAwait(false);
        await AckAsync(token).ConfigureAwait(false);
        return (ushort)((high << 8) | low);
    }

    public async Task MassEraseAsync(CancellationToken token = default)
    {
        var command = _commands.Contains((byte)0x44) ? (byte)0x44 : (byte)0x43;
        Require(command);
        await CommandAsync(command, token).ConfigureAwait(false);
        await SendAsync(command == 0x44 ? new byte[] { 0xFF, 0xFF, 0 } : new byte[] { 0xFF, 0 }, token).ConfigureAwait(false);
        await AckAsync(token, TimeSpan.FromSeconds(120)).ConfigureAwait(false);
    }

    public static void ValidateFlashSegments(IReadOnlyList<FirmwareMemorySegment> segments)
    {
        if (segments.Count == 0) throw new ArgumentException("固件没有数据。");
        ulong previousEnd = 0;
        foreach (var segment in segments.OrderBy(s => s.Address))
        {
            var paddedLength = ((ulong)segment.Data.Length + 3) & ~3UL;
            if (segment.Data.Length == 0 || segment.Address % 4 != 0 || segment.Address < 0x08000000 ||
                (ulong)segment.Address + paddedLength > 0x08200000 || segment.Address < previousEnd)
                throw new ArgumentException("仅允许 4 字节对齐且不重叠的 0x08000000–0x081FFFFF 主 Flash 数据；请检查链接地址。");
            previousEnd = (ulong)segment.Address + paddedLength;
        }
    }

    public async Task WriteMemoryAsync(uint address, ReadOnlyMemory<byte> data, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        Require(0x31); Require(0x11);
        ValidateFlashSegments([new(address, data.ToArray())]);
        for (var offset = 0; offset < data.Length; offset += 256)
        {
            var count = Math.Min(256, data.Length - offset);
            var paddedCount = (count + 3) & ~3;
            var packet = new byte[paddedCount + 2];
            packet.AsSpan(1, paddedCount).Fill(0xFF);
            packet[0] = (byte)(paddedCount - 1);
            data.Slice(offset, count).CopyTo(packet.AsMemory(1));
            for (var i = 0; i < packet.Length - 1; i++) packet[^1] ^= packet[i];
            var current = checked(address + (uint)offset);
            await CommandAsync(0x31, cancellationToken).ConfigureAwait(false);
            await AddressAsync(current, cancellationToken).ConfigureAwait(false);
            await SendAsync(packet, cancellationToken).ConfigureAwait(false);
            await AckAsync(cancellationToken).ConfigureAwait(false);
            var readBack = await ReadMemoryAsync(current, paddedCount, cancellationToken).ConfigureAwait(false);
            if (!readBack.AsSpan().SequenceEqual(packet.AsSpan(1, paddedCount)))
                throw new IOException($"Flash 回读校验失败：0x{current:X8}。");
            progress?.Report(offset + count);
        }
    }

    public async Task<byte[]> ReadMemoryAsync(uint address, int count, CancellationToken token = default)
    {
        Require(0x11);
        if (count is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(count));
        await CommandAsync(0x11, token).ConfigureAwait(false);
        await AddressAsync(address, token).ConfigureAwait(false);
        await SendAsync(new byte[] { (byte)(count - 1), (byte)~(count - 1) }, token).ConfigureAwait(false);
        await AckAsync(token).ConfigureAwait(false);
        var result = new byte[count];
        for (var i = 0; i < count; i++) result[i] = await ReadAsync(token).ConfigureAwait(false);
        return result;
    }

    private void Require(byte command)
    {
        if (!_commands.Contains(command)) throw new NotSupportedException($"此 Bootloader 不支持命令 0x{command:X2}。");
    }
    private async Task AddressAsync(uint address, CancellationToken token)
    {
        var bytes = new byte[] { (byte)(address >> 24), (byte)(address >> 16), (byte)(address >> 8), (byte)address, 0 };
        for (var i = 0; i < 4; i++) bytes[4] ^= bytes[i];
        await SendAsync(bytes, token).ConfigureAwait(false);
        await AckAsync(token).ConfigureAwait(false);
    }
    private async Task CommandAsync(byte command, CancellationToken token)
    {
        await SendAsync(new byte[] { command, (byte)~command }, token).ConfigureAwait(false);
        await AckAsync(token).ConfigureAwait(false);
    }
    private Task SendAsync(byte[] bytes, CancellationToken token) => Task.Run(() => _channel.SendAsync(bytes, token), token);

    private async Task AckAsync(CancellationToken token, TimeSpan? timeout = null)
    {
        var value = await ReadAsync(token, timeout).ConfigureAwait(false);
        if (value == 0x1F) throw new IOException("Bootloader 返回 NACK，请检查读写保护、芯片容量和引脚状态。");
        if (value != 0x79) throw new InvalidDataException($"Bootloader 响应无效：0x{value:X2}。");
    }
    private async Task<byte> ReadAsync(CancellationToken token, TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(3));
        try { return await _received.Reader.ReadAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        { throw new TimeoutException("Bootloader 响应超时，请确认 BOOT 引脚和复位状态。"); }
    }
    private void OnDataReceived(object? sender, ReadOnlyMemory<byte> data)
    {
        if (_disposed) return;
        foreach (var value in data.Span)
            if (!_received.Writer.TryWrite(value)) { _received.Writer.TryComplete(new IOException("Bootloader 接收缓存溢出。")); break; }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _channel.DataReceived -= OnDataReceived;
        _received.Writer.TryComplete();
        await Task.Run(async () => await _channel.DisposeAsync()).ConfigureAwait(false);
        // Pending cancellation continuations may still refer to the lifetime token.
    }
}

