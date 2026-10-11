// Protocol adapted from stcgal (MIT), copyright 2013-2015 Grigori Goronzy.
// See docs/third-party/stcgal-MIT.txt. No external flasher executable is used.
using System.Buffers.Binary;
using System.Threading.Channels;
using EmbeddedDebugger.Core.Abstractions;

namespace EmbeddedDebugger.Infrastructure.Firmware;

public sealed record Stc89Device(ushort Id, string Name, int CodeBytes);
public sealed record Stc89Info(Stc89Device Device, string Bootloader, double ClockHz, bool SixClockMode, byte Options);

/// <summary>Original STC89 UART ISP only. STC89A, STC12, STC15 and STC8 use other protocols.</summary>
public sealed class Stc89BootloaderClient : IAsyncDisposable
{
    public static IReadOnlyList<Stc89Device> SupportedDevices { get; } = Array.AsReadOnly(new Stc89Device[]
    {
        new(0xF050, "STC89C51", 4096), new(0xF051, "STC89C52", 8192), new(0xF052, "STC89C53", 12288), new(0xF053, "STC89C14", 14336),
        new(0xF060, "STC89LE51", 4096), new(0xF061, "STC89LE52", 8192), new(0xF062, "STC89LE53", 12288), new(0xF063, "STC89LE14", 14336),
        new(0xF001, "STC89C51RC / LE51RC", 4096), new(0xF002, "STC89C52RC / LE52RC", 8192), new(0xF004, "STC89C53RC / LE53RC", 14336),
        new(0xF006, "STC89C06RC / LE06RC", 6144), new(0xF007, "STC89C07RC / LE07RC", 7168), new(0xF00A, "STC89C10RC / LE10RC", 10240), new(0xF00C, "STC89C12RC / LE12RC", 12288)
    });
    private readonly ISerialPortChannel _channel;
    private readonly Channel<byte> _receive = Channel.CreateBounded<byte>(65536);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    private bool _ready;
    public Stc89Info? Info { get; private set; }

    public Stc89BootloaderClient(ISerialPortChannel channel)
    { _channel = channel; _channel.DataReceived += OnData; }

    public async Task<Stc89Info> ConnectAsync(int transferBaud = 19200, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_channel is not ISerialPortControl control) throw new NotSupportedException("STC ISP 需要支持动态波特率的本地物理串口。");
        if (transferBaud is < 1200 or > 115200) throw new ArgumentOutOfRangeException(nameof(transferBaud));
        var handshakeBaud = _channel.Settings.BaudRate;
        if (handshakeBaud is < 1200 or > 9600) throw new ArgumentException("STC89 握手波特率应为 1200–9600。");
        _ready = false; Info = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            while (Info is null)
            {
                await _channel.SendAsync(new byte[] { 0x7F }, deadline.Token).ConfigureAwait(false);
                using var pulse = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                pulse.CancelAfter(30);
                try { await _receive.Reader.WaitToReadAsync(pulse.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { continue; }
                try
                {
                    var status = await ReadPacketAsync(TimeSpan.FromSeconds(1), deadline.Token).ConfigureAwait(false);
                    Info = ParseStatus(status, handshakeBaud);
                }
                catch (InvalidDataException) { /* Ignore boot noise until a valid status arrives. */ }
                catch (TimeoutException) { }
            }
            var info = Info;
            var settings = CalculateBaud(info, transferBaud);
            await SendPacketAsync(new byte[] { 0x8F, (byte)(settings.Reload >> 8), (byte)settings.Reload, (byte)(0xFF - (settings.Reload >> 8)), settings.Checksum, 0xA0, settings.WaitStates }, deadline.Token).ConfigureAwait(false);
            await Task.Delay(100, deadline.Token).ConfigureAwait(false);
            await control.SetBaudRateAsync(transferBaud, deadline.Token).ConfigureAwait(false);
            await ExpectAsync(0x8F, deadline.Token).ConfigureAwait(false);
            await control.SetBaudRateAsync(handshakeBaud, deadline.Token).ConfigureAwait(false);
            await SendPacketAsync(new byte[] { 0x8E, (byte)(settings.Reload >> 8), (byte)settings.Reload, (byte)(0xFF - (settings.Reload >> 8)), settings.Checksum, 0xA0 }, deadline.Token).ConfigureAwait(false);
            await Task.Delay(100, deadline.Token).ConfigureAwait(false);
            await control.SetBaudRateAsync(transferBaud, deadline.Token).ConfigureAwait(false);
            await ExpectAsync(0x8E, deadline.Token).ConfigureAwait(false);
            for (var i = 0; i < 4; i++)
            {
                await SendPacketAsync(new byte[] { 0x80, 0, 0, 0x36, 1, (byte)(info.Device.Id >> 8), (byte)info.Device.Id }, deadline.Token).ConfigureAwait(false);
                await ExpectAsync(0x80, deadline.Token).ConfigureAwait(false);
            }
            _ready = true;
            return info;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        { throw new TimeoutException("等待 STC89 上电握手超时，请点击连接后给目标芯片断电重上电。"); }
    }

    public static Stc89Info ParseStatus(ReadOnlySpan<byte> data, int handshakeBaud)
    {
        if (data.Length < 23 || data[0] != 0 || handshakeBaud is < 1200 or > 9600) throw new InvalidDataException("STC89 状态包无效。");
        var id = BinaryPrimitives.ReadUInt16BigEndian(data[20..]);
        var device = SupportedDevices.FirstOrDefault(d => d.Id == id)
            ?? throw new NotSupportedException($"MCU ID 0x{id:X4} 尚未支持；当前仅支持列表中的原始 STC89 系列，不会执行擦写。");
        var sixT = (data[19] & 1) == 0;
        double counter = 0;
        for (var i = 0; i < 8; i++) counter += BinaryPrimitives.ReadUInt16BigEndian(data.Slice(1 + i * 2, 2));
        var frequency = handshakeBaud * (counter / 8) * (sixT ? 6 : 12) / 7;
        if (frequency is < 100000 or > 60000000) throw new InvalidDataException("STC89 时钟测量无效，请检查连接和晶振。");
        var stepping = data[18] is >= 32 and < 127 ? ((char)data[18]).ToString() : $"(0x{data[18]:X2})";
        return new(device, $"{data[17] >> 4}.{data[17] & 15}{stepping}", frequency, sixT, data[19]);
    }

    public static (ushort Reload, byte Checksum, byte WaitStates) CalculateBaud(Stc89Info info, int transferBaud)
    {
        if (transferBaud is < 1200 or > 115200) throw new ArgumentOutOfRangeException(nameof(transferBaud));
        var sample = info.SixClockMode ? 16 : 32;
        var divisor = (int)Math.Round(info.ClockHz / (transferBaud * sample), MidpointRounding.ToEven);
        if (divisor is < 1 or > 65535) throw new ArgumentException("目标晶振不支持所选波特率。");
        var actual = info.ClockHz / (sample * divisor);
        if (Math.Abs(actual - transferBaud) / transferBaud > .05) throw new ArgumentException("目标波特率误差超过 5%，请选择更低的传输波特率。");
        var reload = (ushort)(65536 - divisor);
        var wait = info.ClockHz < 5000000 ? 0x83 : info.ClockHz < 10000000 ? 0x82 : info.ClockHz < 20000000 ? 0x81 : 0x80;
        return (reload, unchecked((byte)(2 * (256 - reload))), (byte)wait);
    }

    public static byte[] PrepareFirmware(IReadOnlyList<FirmwareMemorySegment> segments, Stc89Device device)
    {
        if (segments.Count == 0) throw new ArgumentException("固件没有数据。");
        var ordered = segments.OrderBy(s => s.Address).ToArray();
        if (ordered[0].Address != 0) throw new ArgumentException("STC89 固件必须从复位向量 0x0000 开始；不支持只写 EEPROM。");
        ulong end = 0;
        foreach (var segment in ordered)
        {
            if (segment.Data.Length == 0 || segment.Address < end || (ulong)segment.Address + (ulong)segment.Data.Length > (uint)device.CodeBytes)
                throw new ArgumentException("固件地址重叠、为空或超出已识别芯片的代码 Flash 容量。");
            end = (ulong)segment.Address + (ulong)segment.Data.Length;
        }
        var image = new byte[checked((int)((end + 127) & ~127UL))];
        Array.Fill(image, (byte)0xFF);
        foreach (var segment in ordered) segment.Data.CopyTo(image, checked((int)segment.Address));
        return image;
    }

    public async Task EraseCodeAsync(CancellationToken token = default)
    {
        RequireReady();
        await EraseInternalAsync(token).ConfigureAwait(false);
        await RestoreOptionsAsync(token).ConfigureAwait(false);
    }

    public async Task ProgramAsync(IReadOnlyList<FirmwareMemorySegment> segments, IProgress<long>? progress = null, CancellationToken token = default)
    {
        RequireReady();
        var image = PrepareFirmware(segments, Info!.Device);
        await EraseInternalAsync(token).ConfigureAwait(false);
        for (var offset = 0; offset < image.Length; offset += 128)
        {
            var payload = new byte[135];
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(3), checked((ushort)offset));
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(5), 128);
            image.AsSpan(offset, 128).CopyTo(payload.AsSpan(7));
            var checksum = (byte)(image.AsSpan(offset, 128).ToArray().Sum(value => value) & 0xFF);
            await SendPacketAsync(payload, token).ConfigureAwait(false);
            var reply = await ExpectAsync(0x80, token).ConfigureAwait(false);
            if (reply.Length < 2 || reply[1] != checksum)
            { _ready = false; throw new IOException($"STC89 设备分块校验和不匹配：0x{offset:X4}。请重新上电连接后烧录。"); }
            progress?.Report(offset + 128);
        }
        await RestoreOptionsAsync(token).ConfigureAwait(false);
    }

    private async Task EraseInternalAsync(CancellationToken token)
    {
        var blocks = checked((byte)(((Info!.Device.CodeBytes + 511) / 512) * 2));
        await SendPacketAsync(new byte[] { 0x84, blocks, 0x33, 0x33, 0x33, 0x33, 0x33, 0x33 }, token).ConfigureAwait(false);
        await ExpectAsync(0x80, token, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
    }
    private async Task RestoreOptionsAsync(CancellationToken token)
    {
        await SendPacketAsync(new byte[] { 0x8D, Info!.Options, 0xFF, 0xFF, 0xFF }, token).ConfigureAwait(false);
        await ExpectAsync(0x8D, token).ConfigureAwait(false);
    }
    public async Task RunAsync(CancellationToken token = default)
    { RequireReady(); await SendPacketAsync(new byte[] { 0x82 }, token).ConfigureAwait(false); _ready = false; }
    private void RequireReady()
    { ObjectDisposedException.ThrowIf(_disposed, this); if (!_ready || Info is null) throw new InvalidOperationException("请先连接并识别 STC89 芯片。"); }

    public static byte[] EncodePacket(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is < 1 or > 2048) throw new ArgumentOutOfRangeException(nameof(payload));
        var packet = new byte[payload.Length + 7]; packet[0] = 0x46; packet[1] = 0xB9; packet[2] = 0x6A;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(3), (ushort)(payload.Length + 5));
        payload.CopyTo(packet.AsSpan(5));
        for (var i = 2; i < packet.Length - 2; i++) packet[^2] = unchecked((byte)(packet[^2] + packet[i]));
        packet[^1] = 0x16;
        return packet;
    }
    public static byte[] DecodePacket(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 8 || packet[0] != 0x46 || packet[1] != 0xB9 || packet[2] != 0x68 || packet[^1] != 0x16 || BinaryPrimitives.ReadUInt16BigEndian(packet[3..]) != packet.Length - 2)
            throw new InvalidDataException("STC89 响应帧格式或长度无效。");
        byte sum = 0; for (var i = 2; i < packet.Length - 2; i++) sum = unchecked((byte)(sum + packet[i]));
        if (sum != packet[^2]) throw new InvalidDataException("STC89 响应帧校验和错误。");
        return packet[5..^2].ToArray();
    }
    private Task SendPacketAsync(byte[] payload, CancellationToken token) => _channel.SendAsync(EncodePacket(payload), token);
    private async Task<byte[]> ExpectAsync(byte command, CancellationToken token, TimeSpan? timeout = null)
    {
        var payload = await ReadPacketAsync(timeout ?? TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        if (payload[0] != command) { _ready = false; throw new InvalidDataException($"STC89 命令响应错误：期望 0x{command:X2}，收到 0x{payload[0]:X2}。"); }
        return payload;
    }
    private async Task<byte[]> ReadPacketAsync(TimeSpan timeout, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token); deadline.CancelAfter(timeout);
        try
        {
            byte first;
            do { first = await _receive.Reader.ReadAsync(deadline.Token).ConfigureAwait(false); } while (first is not (0x46 or 0x68));
            if (first == 0x46 && (await _receive.Reader.ReadAsync(deadline.Token).ConfigureAwait(false) != 0xB9 || await _receive.Reader.ReadAsync(deadline.Token).ConfigureAwait(false) != 0x68)) throw new InvalidDataException("STC89 帧头无效。");
            var high = await _receive.Reader.ReadAsync(deadline.Token).ConfigureAwait(false);
            var low = await _receive.Reader.ReadAsync(deadline.Token).ConfigureAwait(false);
            var length = (high << 8) | low;
            if (length is < 6 or > 2053) throw new InvalidDataException("STC89 响应长度无效。");
            var packet = new byte[length + 2]; packet[0] = 0x46; packet[1] = 0xB9; packet[2] = 0x68; packet[3] = high; packet[4] = low;
            for (var i = 5; i < packet.Length; i++) packet[i] = await _receive.Reader.ReadAsync(deadline.Token).ConfigureAwait(false);
            return DecodePacket(packet);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        { throw new TimeoutException("STC89 响应超时，请检查供电、RX/TX 和晶振。"); }
    }
    private void OnData(object? sender, ReadOnlyMemory<byte> bytes)
    {
        if (_disposed) return;
        foreach (var value in bytes.Span)
            if (!_receive.Writer.TryWrite(value)) { _receive.Writer.TryComplete(new IOException("STC89 接收缓存溢出。")); break; }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true; _ready = false;
        _lifetime.Cancel(); _channel.DataReceived -= OnData; _receive.Writer.TryComplete();
        await _channel.DisposeAsync().ConfigureAwait(false);
    }
}
