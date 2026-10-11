using System.Buffers.Binary;
using System.IO;
using System.Threading.Channels;
using EmbeddedDebugger.Core.Abstractions;

namespace EmbeddedDebugger.Infrastructure.Firmware;

/// <summary>
/// Small, dependency-free ESP ROM loader implementation.
/// It intentionally stays at the ROM protocol layer: no external esptool.exe or
/// downloaded flasher stub is required.  It covers the stable sync/flash commands
/// shared by ESP8266 and ESP32-class ROM loaders.
/// </summary>
public sealed class EspRomBootloaderClient : IAsyncDisposable
{
    private const byte Sync = 0x08;
    private const byte FlashBegin = 0x02;
    private const byte FlashData = 0x03;
    private const byte FlashEnd = 0x04;
    private const byte ReadReg = 0x0A;
    private const byte SlipEnd = 0xC0;
    private const byte SlipEsc = 0xDB;
    private const byte SlipEscEnd = 0xDC;
    private const byte SlipEscEsc = 0xDD;
    private const byte ChecksumSeed = 0xEF;
    private const int BlockSize = 0x400;

    private readonly ISerialPortChannel _channel;
    private readonly Channel<byte> _received = Channel.CreateBounded<byte>(1 << 20);
    private readonly CancellationTokenSource _lifetime = new();
    private EventHandler<ReadOnlyMemory<byte>>? _monitorHandler;
    private bool _disposed;

    public EspRomBootloaderClient(ISerialPortChannel channel)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _channel.DataReceived += OnDataReceived;
    }

    public bool IsConnected { get; private set; }
    public string ChipFamily { get; private set; } = "ESP ROM bootloader";
    public uint? ChipId { get; private set; }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        DrainReceived();
        var syncPayload = new byte[36];
        syncPayload[0] = 0x07;
        syncPayload[1] = 0x07;
        syncPayload[2] = 0x12;
        syncPayload[3] = 0x20;
        syncPayload.AsSpan(4).Fill(0x55);

        Exception? last = null;
        for (var attempt = 0; attempt < 7; attempt++)
        {
            try
            {
                await CommandAsync(Sync, syncPayload, 0, TimeSpan.FromMilliseconds(900), cancellationToken).ConfigureAwait(false);
                // ROMs can emit several sync replies. A few empty commands drain
                // them and make the next command deterministic.
                for (var i = 0; i < 2; i++)
                {
                    try { await CommandAsync(null, Array.Empty<byte>(), 0, TimeSpan.FromMilliseconds(120), cancellationToken).ConfigureAwait(false); }
                    catch (TimeoutException) { break; }
                }
                IsConnected = true;
                ChipFamily = "ESP8266 / ESP32 系列 ROM";
                return;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or InvalidDataException)
            {
                last = ex;
                DrainReceived();
                await Task.Delay(60, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new IOException("ESP ROM 同步失败。请让芯片进入下载模式（按住 BOOT 后点 RESET），并确认没有其他程序占用串口。", last);
    }

    /// <summary>Best-effort chip identification. ROM register maps differ by family;</summary>
    /// <summary>the value is therefore informational and never gates flashing.</summary>
    public async Task<uint?> TryReadChipIdAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected) throw new InvalidOperationException("ESP 尚未连接。");
        foreach (var address in new[] { 0x40001000u, 0x60008800u })
        {
            try
            {
                var (_, data) = await CommandAsync(ReadReg, UInt32(address), 0, TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                if (data.Length >= 6 && data[4] == 0 && data[5] == 0)
                {
                    ChipId = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4));
                    return ChipId;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or TimeoutException)
            {
                // Some ESP ROMs reject both addresses. Keep the connection usable.
            }
        }
        return null;
    }

    public async Task FlashAsync(uint address, ReadOnlyMemory<byte> image, IProgress<long>? progress = null, bool reboot = true, CancellationToken cancellationToken = default)
    {
        if (!IsConnected) throw new InvalidOperationException("ESP 尚未连接。");
        if (image.Length == 0) throw new ArgumentException("固件为空。", nameof(image));
        if (address % 0x1000 != 0) throw new ArgumentException("ESP 固件地址必须按 0x1000 对齐。", nameof(address));

        var size = image.Length;
        var blockCount = (size + BlockSize - 1) / BlockSize;
        var eraseSize = (size + 0xFFF) & ~0xFFF;
        var begin = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(begin.AsSpan(0, 4), (uint)eraseSize);
        BinaryPrimitives.WriteUInt32LittleEndian(begin.AsSpan(4, 4), (uint)blockCount);
        BinaryPrimitives.WriteUInt32LittleEndian(begin.AsSpan(8, 4), BlockSize);
        BinaryPrimitives.WriteUInt32LittleEndian(begin.AsSpan(12, 4), address);
        await CommandCheckedAsync(FlashBegin, begin, 0, TimeSpan.FromSeconds(Math.Clamp(size / 250_000d, 3, 120)), cancellationToken).ConfigureAwait(false);

        var buffer = new byte[BlockSize];
        for (var sequence = 0; sequence < blockCount; sequence++)
        {
            var offset = sequence * BlockSize;
            var count = Math.Min(BlockSize, size - offset);
            buffer.AsSpan().Fill(0xFF);
            image.Span.Slice(offset, count).CopyTo(buffer);

            var payload = new byte[16 + BlockSize];
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), BlockSize);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), (uint)sequence);
            buffer.CopyTo(payload, 16);
            await CommandCheckedAsync(FlashData, payload, XorChecksum(buffer), TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
            progress?.Report(Math.Min(size, offset + count));
        }

        var end = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(end, reboot ? 0u : 1u);
        await CommandCheckedAsync(FlashEnd, end, 0, TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Switches the channel to raw serial monitor mode after flashing.</summary>
    public void StartMonitor(EventHandler<ReadOnlyMemory<byte>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (_monitorHandler is not null) return;
        _channel.DataReceived -= OnDataReceived;
        _monitorHandler = handler;
        _channel.DataReceived += handler;
    }

    public void StopMonitor()
    {
        if (_monitorHandler is null) return;
        _channel.DataReceived -= _monitorHandler;
        _monitorHandler = null;
        _channel.DataReceived += OnDataReceived;
        DrainReceived();
    }

    private async Task<(uint Value, byte[] Data)> CommandAsync(byte? command, ReadOnlyMemory<byte> payload, uint checksum, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (command.HasValue)
        {
            var packet = new byte[8 + payload.Length];
            packet[0] = 0;
            packet[1] = command.Value;
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), checked((ushort)payload.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), checksum);
            payload.CopyTo(packet.AsMemory(8));
            await _channel.SendAsync(SlipEncode(packet), cancellationToken).ConfigureAwait(false);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        deadline.CancelAfter(timeout);
        while (true)
        {
            var frame = await ReadFrameAsync(deadline.Token).ConfigureAwait(false);
            if (frame.Length < 8 || frame[0] != 1) continue;
            var operation = frame[1];
            if (command.HasValue && operation != command.Value) continue;
            var length = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(2, 2));
            if (length > frame.Length - 8) continue;
            var value = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4, 4));
            return (value, frame.AsSpan(8, length).ToArray());
        }
    }

    private async Task CommandCheckedAsync(byte command, ReadOnlyMemory<byte> payload, uint checksum, TimeSpan timeout, CancellationToken token)
    {
        var (_, data) = await CommandAsync(command, payload, checksum, timeout, token).ConfigureAwait(false);
        if (data.Length >= 2 && (data[0] != 0 || data[1] != 0))
            throw new IOException($"ESP ROM 命令 0x{command:X2} 失败：状态 0x{data[0]:X2}{data[1]:X2}。");
    }

    private async Task<byte[]> ReadFrameAsync(CancellationToken token)
    {
        var frame = new List<byte>(256);
        var escaped = false;
        while (true)
        {
            var value = await _received.Reader.ReadAsync(token).ConfigureAwait(false);
            if (value == SlipEnd)
            {
                if (frame.Count == 0) continue;
                return frame.ToArray();
            }
            if (escaped)
            {
                escaped = false;
                frame.Add(value switch { SlipEscEnd => SlipEnd, SlipEscEsc => SlipEsc, _ => throw new InvalidDataException("ESP SLIP 转义无效。") });
            }
            else if (value == SlipEsc) escaped = true;
            else frame.Add(value);
            if (frame.Count > 131_072) throw new InvalidDataException("ESP 响应包过大。");
        }
    }

    private static byte[] SlipEncode(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(data.Length + 2) { SlipEnd };
        foreach (var value in data)
        {
            if (value == SlipEnd) output.AddRange([SlipEsc, SlipEscEnd]);
            else if (value == SlipEsc) output.AddRange([SlipEsc, SlipEscEsc]);
            else output.Add(value);
        }
        output.Add(SlipEnd);
        return output.ToArray();
    }

    private static uint XorChecksum(ReadOnlySpan<byte> data)
    {
        var checksum = ChecksumSeed;
        foreach (var value in data) checksum ^= value;
        return checksum;
    }

    private void OnDataReceived(object? sender, ReadOnlyMemory<byte> data)
    {
        if (_disposed) return;
        foreach (var value in data.Span)
            if (!_received.Writer.TryWrite(value))
            {
                _received.Writer.TryComplete(new IOException("ESP 串口接收缓存溢出。"));
                break;
            }
    }

    private void DrainReceived()
    {
        while (_received.Reader.TryRead(out _)) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        StopMonitor();
        _lifetime.Cancel();
        _channel.DataReceived -= OnDataReceived;
        _received.Writer.TryComplete();
        await _channel.DisposeAsync().ConfigureAwait(false);
        IsConnected = false;
    }

    private static byte[] UInt32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }
}
