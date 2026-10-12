using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using EmbedDesk.Core.Abstractions;

namespace EmbedDesk.Infrastructure.Firmware;

public sealed record EspFlashImage(uint Address, byte[] Data, string Name);
public sealed record EspOperationProgress(string Stage, long Completed, long Total);

/// <summary>
/// ROM-only UART loader. Protocol/register definitions are based on Espressif esptool v4.8.1
/// (GPL-2.0-or-later), loader.py and targets/*.py. No external executable or RAM stub.
/// </summary>
public sealed class EspRomBootloaderClient : IAsyncDisposable
{
    private sealed record Chip(string Name, uint[] Magic, int ImageId, uint SpiBase, uint UserOffset,
        uint DataOffset, bool ExtendedBegin, bool Esp8266 = false);

    private static readonly Chip[] Chips =
    [
        new("ESP8266", [0xFFF0C101], -1, 0x60000200, 0x1C, 0x40, false, true),
        new("ESP32", [0x00F01D83], 0, 0x3FF42000, 0x1C, 0x80, false),
        new("ESP32-S2", [0x000007C6], 2, 0x3F402000, 0x18, 0x58, true),
        new("ESP32-S3", [0x00000009], 9, 0x60002000, 0x18, 0x58, true),
        new("ESP32-C3", [0x6921506F, 0x1B31506F, 0x4881606F, 0x4361606F], 5, 0x60002000, 0x18, 0x58, true),
        new("ESP32-C2", [0x6F51306F, 0x7C41A06F, 0x0C21E06F], 12, 0x60002000, 0x18, 0x58, true),
        new("ESP32-C6", [0x2CE0806F], 13, 0x60003000, 0x18, 0x58, true),
        new("ESP32-H2", [0xD7B73E80], 16, 0x60003000, 0x18, 0x58, true)
    ];
    private const int BlockSize = 1024;
    private readonly ISerialPortChannel _channel;
    private readonly Channel<byte> _received = Channel.CreateBounded<byte>(128 * 1024);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _operation = new(1, 1);
    private EventHandler<ReadOnlyMemory<byte>>? _monitorHandler;
    private Chip? _chip;
    private bool _disposed;
    private bool _receiveOverflow;

    public EspRomBootloaderClient(ISerialPortChannel channel)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _channel.DataReceived += OnDataReceived;
    }

    public bool IsConnected { get; private set; }
    public bool IsMonitoring => _monitorHandler is not null;
    public bool CanAutoReset => _channel is ISerialPortControl;
    public string ChipFamily => _chip?.Name ?? "未识别";
    /// <summary>ROM family identification value, not a unique device serial number.</summary>
    public uint? ChipId { get; private set; }
    public uint FlashId { get; private set; }
    public uint FlashSize { get; private set; }
    public bool WriteProtected { get; private set; }
    public string SecurityDescription { get; private set; } = "未读取";
    public static string SupportedChips => string.Join(" / ", Chips.Select(chip => chip.Name));

    public Task ConnectAsync(CancellationToken cancellationToken = default) => ConnectAsync(true, cancellationToken);

    public Task ConnectAsync(bool autoReset, CancellationToken cancellationToken = default) => ExecuteAsync(async token =>
    {
        _monitorHandler = null;
        IsConnected = false;
        _chip = null;
        FlashSize = 0;
        FlashId = 0;
        ChipId = null;
        WriteProtected = true;
        SecurityDescription = "未读取";
        if (autoReset && _channel is ISerialPortControl control)
        {
            // Classic two-transistor dev-board reset sequence: hold EN, strap GPIO0, release EN.
            await control.SetControlLinesAsync(false, true, token).ConfigureAwait(false);
            await Task.Delay(100, token).ConfigureAwait(false);
            await control.SetControlLinesAsync(true, false, token).ConfigureAwait(false);
            await Task.Delay(100, token).ConfigureAwait(false);
            await control.SetControlLinesAsync(false, false, token).ConfigureAwait(false);
        }
        DrainReceived();
        var sync = new byte[36];
        new byte[] { 7, 7, 0x12, 0x20 }.CopyTo(sync, 0);
        sync.AsSpan(4).Fill(0x55);
        Exception? last = null;
        var synced = false;
        for (var attempt = 0; attempt < 7; attempt++)
        {
            try
            {
                await CommandAsync(0x08, sync, 0, TimeSpan.FromMilliseconds(600), token).ConfigureAwait(false);
                synced = true;
                break;
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidDataException)
            {
                last = ex;
                DrainReceived();
            }
        }
        if (!synced) throw new IOException("ESP ROM 同步失败。请按住 BOOT 后点 RESET，再尝试手动连接。", last);
        // Extra SYNC responses are ignored by the opcode matcher below.
        ChipId = await ReadRegisterAsync(0x40001000, token).ConfigureAwait(false);
        _chip = Chips.FirstOrDefault(chip => chip.Magic.Contains(ChipId.Value));
        if (_chip is null) throw new NotSupportedException($"不支持的 ESP ROM 标识 0x{ChipId:X8}；未执行擦除或写入。");

        if (_chip.Esp8266)
        {
            await BeginFlashAsync(0, 0, token).ConfigureAwait(false); // Zero-size attach, no erase.
            WriteProtected = false;
            SecurityDescription = "ESP8266 ROM（无 Flash 加密 / Secure Boot）";
        }
        else
        {
            await CheckedAsync(0x0D, Words(0, 0), token).ConfigureAwait(false);
            if (_chip.ImageId == 0)
            {
                var crypt = await ReadRegisterAsync(0x3FF5A000, token).ConfigureAwait(false);
                var secure = await ReadRegisterAsync(0x3FF5A018, token).ConfigureAwait(false);
                SetSecurity((secure & 0x30) != 0, BitOperations.PopCount((crypt >> 20) & 0x7F) % 2 != 0);
            }
            else
            {
                var security = await CheckedAsync(0x14, [], token).ConfigureAwait(false);
                if (security.Length < 12) throw new InvalidDataException("ESP 安全状态响应不完整，已拒绝写入。");
                SetSecurity((BinaryPrimitives.ReadUInt32LittleEndian(security) & 1) != 0,
                    BitOperations.PopCount((uint)security[4]) % 2 != 0);
            }
        }
        FlashId = BinaryPrimitives.ReadUInt32LittleEndian(await SpiReadAsync(0x9F, null, 4, token).ConfigureAwait(false)) & 0xFFFFFF;
        var exponent = (int)(FlashId >> 16);
        if (FlashId is 0 or 0xFFFFFF || exponent is < 18 or > 24)
            throw new NotSupportedException($"无法确认 Flash 容量（JEDEC 0x{FlashId:X6}）。本工具支持 256 KiB～16 MiB 的标准 SPI Flash。");
        FlashSize = 1u << exponent;
        if (!_chip.Esp8266)
            await CheckedAsync(0x0B, Words(0, FlashSize, 65536, 4096, 256, 0xFFFF), token).ConfigureAwait(false);
        IsConnected = true;
    }, cancellationToken);

    private void SetSecurity(bool secureBoot, bool encrypted)
    {
        WriteProtected = secureBoot || encrypted;
        SecurityDescription = $"Secure Boot：{(secureBoot ? "启用" : "关闭")}；Flash 加密：{(encrypted ? "启用" : "关闭")}";
        if (WriteProtected) SecurityDescription += "（仅允许读取，普通固件写入已禁用）";
    }

    public Task<uint?> TryReadChipIdAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnected();
        return Task.FromResult(ChipId);
    }

    public static void ValidateImages(IReadOnlyList<EspFlashImage> images, uint flashSize)
    {
        if (images.Count == 0) throw new ArgumentException("请至少添加一个固件。");
        ulong previousEnd = 0;
        foreach (var image in images.OrderBy(image => image.Address))
        {
            if (image.Data.Length == 0) throw new ArgumentException($"{image.Name} 为空。");
            if (image.Address % 4096 != 0) throw new ArgumentException($"{image.Name} 的地址必须按 0x1000 扇区对齐。");
            var end = (ulong)image.Address + (ulong)image.Data.Length;
            var erasedEnd = (end + 4095) & ~4095UL;
            if (end > flashSize || erasedEnd > flashSize) throw new ArgumentException($"{image.Name} 超出 Flash 容量。");
            if (image.Address < previousEnd) throw new ArgumentException($"{image.Name} 与其他文件的擦除扇区重叠。");
            previousEnd = erasedEnd;
        }
    }

    public Task FlashImagesAsync(IReadOnlyList<EspFlashImage> images, IProgress<EspOperationProgress>? progress = null,
        CancellationToken cancellationToken = default) => ExecuteAsync(async token =>
    {
        EnsureWritable();
        ValidateImages(images, FlashSize);
        foreach (var image in images)
        {
            // ESP image headers embed their target ID (data/partition images have no E9 header).
            if (!_chip!.Esp8266 && image.Data.Length >= 24 && image.Data[0] == 0xE9 && image.Data[1] is > 0 and <= 16
                && BinaryPrimitives.ReadUInt16LittleEndian(image.Data.AsSpan(12, 2)) != _chip.ImageId)
                throw new InvalidDataException($"{image.Name} 的 ESP 镜像芯片 ID 与 {_chip.Name} 不一致。");
        }
        var total = images.Sum(image => (long)image.Data.Length);
        long done = 0;
        foreach (var image in images.OrderBy(image => image.Address))
        {
            progress?.Report(new($"擦除 {image.Name}", done, total));
            await BeginFlashAsync(image.Address, image.Data.Length, token).ConfigureAwait(false);
            for (var offset = 0; offset < image.Data.Length; offset += BlockSize)
            {
                var payload = new byte[16 + BlockSize];
                Words(BlockSize, (uint)(offset / BlockSize), 0, 0).CopyTo(payload, 0);
                payload.AsSpan(16).Fill(0xFF);
                var count = Math.Min(BlockSize, image.Data.Length - offset);
                image.Data.AsSpan(offset, count).CopyTo(payload.AsSpan(16));
                await CheckedAsync(0x03, payload, token, Checksum(payload.AsSpan(16)), TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                progress?.Report(new($"写入 {image.Name}", done + offset + count, total));
            }
            progress?.Report(new($"校验 {image.Name}", done, total));
            await VerifyCoreAsync(image.Address, image.Data, token).ConfigureAwait(false);
            done += image.Data.Length;
            progress?.Report(new($"已校验 {image.Name}", done, total));
        }
        // ROM FLASH_END leaves the command loop, even with reboot=0 on some targets.
        // Stay in ROM for backup/next image; only send it on explicit reboot.
    }, cancellationToken, invalidateOnFailure: true);

    public async Task FlashAsync(uint address, ReadOnlyMemory<byte> image, IProgress<long>? progress = null,
        bool reboot = true, CancellationToken cancellationToken = default)
    {
        await FlashImagesAsync([new(address, image.ToArray(), "固件")],
            progress is null ? null : new CallbackProgress(value => progress.Report(value.Completed)), cancellationToken).ConfigureAwait(false);
        if (reboot) await RebootAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task VerifyAsync(uint address, ReadOnlyMemory<byte> expected, CancellationToken cancellationToken = default) => ExecuteAsync(async token =>
    {
        EnsureConnected();
        ValidateRange(address, expected.Length);
        await VerifyCoreAsync(address, expected, token).ConfigureAwait(false);
    }, cancellationToken, invalidateOnFailure: true);

    private async Task VerifyCoreAsync(uint address, ReadOnlyMemory<byte> expected, CancellationToken token)
    {
        if (!_chip!.Esp8266)
        {
            var remote = await CheckedAsync(0x13, Words(address, (uint)expected.Length, 0, 0), token,
                timeout: TimeSpan.FromSeconds(Math.Max(10, expected.Length / 100_000d))).ConfigureAwait(false);
            var local = MD5.HashData(expected.Span); // ESP ROM's wire integrity algorithm, not authentication.
            var digest = remote.Length == 32 ? Convert.FromHexString(Encoding.ASCII.GetString(remote)) : remote;
            if (digest.Length != 16 || !CryptographicOperations.FixedTimeEquals(local, digest))
                throw new InvalidDataException($"Flash 校验失败：0x{address:X8} 的 MD5 与本地文件不一致。");
            return;
        }
        for (var offset = 0; offset < expected.Length; offset += 64)
        {
            var count = Math.Min(64, expected.Length - offset);
            var actual = await ReadBlockAsync(address + (uint)offset, count, token).ConfigureAwait(false);
            if (!actual.AsSpan().SequenceEqual(expected.Span.Slice(offset, count)))
                throw new InvalidDataException($"Flash 回读校验失败：0x{address + offset:X8}。");
        }
    }

    public Task ReadFlashAsync(uint address, int length, Stream destination, IProgress<EspOperationProgress>? progress = null,
        CancellationToken cancellationToken = default) => ExecuteAsync(async token =>
    {
        EnsureConnected();
        ValidateRange(address, length);
        ArgumentNullException.ThrowIfNull(destination);
        for (var offset = 0; offset < length; offset += 64)
        {
            var data = await ReadBlockAsync(address + (uint)offset, Math.Min(64, length - offset), token).ConfigureAwait(false);
            await destination.WriteAsync(data, token).ConfigureAwait(false);
            if ((offset + data.Length) % 4096 == 0 || offset + data.Length == length)
                progress?.Report(new("读取 Flash", offset + data.Length, length));
        }
    }, cancellationToken, invalidateOnFailure: true);

    public async Task<byte[]> ReadFlashAsync(uint address, int length, CancellationToken cancellationToken = default)
    {
        using var output = new MemoryStream();
        await ReadFlashAsync(address, length, output, cancellationToken: cancellationToken).ConfigureAwait(false);
        return output.ToArray();
    }

    // Erasing through the ordinary flash path also works on ROMs without ERASE_REGION.
    // Writing FF verifies the entire requested range and does not rely on stub-only commands.
    public Task EraseAsync(uint address, int length, IProgress<EspOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (length <= 0 || length > 16 * 1024 * 1024 || length % 4096 != 0)
            throw new ArgumentException("擦除长度必须为 0x1000 的整数倍，最大 16 MiB。");
        var blank = new byte[length];
        blank.AsSpan().Fill(0xFF);
        return FlashImagesAsync([new(address, blank, "擦除区域（0xFF）")], progress, cancellationToken);
    }

    public Task RebootAsync(CancellationToken cancellationToken = default) => ExecuteAsync(async token =>
    {
        EnsureConnected();
        IsConnected = false;
        if (_channel is ISerialPortControl control)
        {
            await control.SetControlLinesAsync(false, true, token).ConfigureAwait(false);
            await Task.Delay(100, token).ConfigureAwait(false);
            await control.SetControlLinesAsync(false, false, token).ConfigureAwait(false);
        }
        else
        {
            await BeginFlashAsync(0, 0, token).ConfigureAwait(false);
            await CheckedAsync(0x04, Words(0), token).ConfigureAwait(false);
        }
        DrainReceived();
    }, cancellationToken, invalidateOnFailure: true);

    public void StartMonitor(EventHandler<ReadOnlyMemory<byte>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_operation.Wait(0)) throw new InvalidOperationException("ROM 操作进行中，不能启动监视器。");
        try
        {
            IsConnected = false;
            _monitorHandler = handler;
            DrainReceived();
        }
        finally { _operation.Release(); }
    }

    public void StopMonitor()
    {
        _monitorHandler = null;
        DrainReceived();
        // A running application is not a bootloader. Reconnect explicitly before any command.
    }

    public async Task SendMonitorAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (!IsMonitoring) throw new InvalidOperationException("请先启动串口监视器。");
        await _channel.SendAsync(data, cancellationToken).ConfigureAwait(false);
    }

    private async Task BeginFlashAsync(uint address, int size, CancellationToken token)
    {
        var eraseSize = (uint)((size + 4095L) & ~4095L);
        if (_chip!.Esp8266)
        {
            // ESP8266 ROM erase-size bug workaround from esptool's ESP8266ROM.
            var sectors = (uint)((size + 4095L) / 4096);
            var head = Math.Min(sectors, 16 - (address / 4096 % 16));
            eraseSize = (sectors < 2 * head ? (sectors + 1) / 2 : sectors - head) * 4096;
        }
        var payload = Words(eraseSize, (uint)((size + BlockSize - 1L) / BlockSize), BlockSize, address);
        if (_chip.ExtendedBegin) payload = [.. payload, 0, 0, 0, 0];
        await CheckedAsync(0x02, payload, token, timeout: TimeSpan.FromSeconds(Math.Max(10, size / 1_048_576d * 30))).ConfigureAwait(false);
    }

    private async Task<byte[]> ReadBlockAsync(uint address, int count, CancellationToken token)
    {
        if (_chip!.Esp8266) return await SpiReadAsync(0x03, address, count, token).ConfigureAwait(false);
        var data = await CheckedAsync(0x0E, Words(address, (uint)count), token).ConfigureAwait(false);
        if (data.Length < count) throw new InvalidDataException("Flash 读取响应被截断。");
        return data[..count];
    }

    private async Task<byte[]> SpiReadAsync(byte command, uint? address, int count, CancellationToken token)
    {
        var chip = _chip!;
        var user = chip.SpiBase + chip.UserOffset;
        var oldUser = await ReadRegisterAsync(user, token).ConfigureAwait(false);
        var oldUser1 = await ReadRegisterAsync(user + 4, token).ConfigureAwait(false);
        var oldUser2 = await ReadRegisterAsync(user + 8, token).ConfigureAwait(false);
        try
        {
            if (chip.Esp8266)
                await WriteRegisterAsync(user + 4, ((uint)count * 8 - 1) << 8 | (address.HasValue ? 23u << 26 : 0), token).ConfigureAwait(false);
            else
            {
                await WriteRegisterAsync(user + 16, (uint)count * 8 - 1, token).ConfigureAwait(false);
                await WriteRegisterAsync(user + 4, address.HasValue ? 23u << 26 : 0, token).ConfigureAwait(false);
            }
            await WriteRegisterAsync(user, (1u << 31) | (1u << 28) | (address.HasValue ? 1u << 30 : 0), token).ConfigureAwait(false);
            await WriteRegisterAsync(user + 8, (7u << 28) | command, token).ConfigureAwait(false);
            if (address.HasValue)
                await WriteRegisterAsync(chip.SpiBase + 4, chip.Esp8266 || chip.ImageId == 0 ? address.Value << 8 : address.Value, token).ConfigureAwait(false);
            await WriteRegisterAsync(chip.SpiBase + chip.DataOffset, 0, token).ConfigureAwait(false);
            await WriteRegisterAsync(chip.SpiBase, 1u << 18, token).ConfigureAwait(false);
            var complete = false;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                if ((await ReadRegisterAsync(chip.SpiBase, token).ConfigureAwait(false) & (1u << 18)) == 0) { complete = true; break; }
            }
            if (!complete) throw new TimeoutException("ESP SPI 控制器未完成读取。");
            var data = new byte[(count + 3) / 4 * 4];
            for (var offset = 0; offset < data.Length; offset += 4)
            {
                var value = await ReadRegisterAsync(chip.SpiBase + chip.DataOffset + (uint)offset, token).ConfigureAwait(false);
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);
            }
            return data[..count];
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                await WriteRegisterAsync(user, oldUser, token).ConfigureAwait(false);
                await WriteRegisterAsync(user + 4, oldUser1, token).ConfigureAwait(false);
                await WriteRegisterAsync(user + 8, oldUser2, token).ConfigureAwait(false);
            }
        }
    }

    private async Task<uint> ReadRegisterAsync(uint address, CancellationToken token)
    {
        var result = await CommandAsync(0x0A, Words(address), 0, TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
        if (result.Data.Length < 2 || result.Data[0] != 0)
            throw new IOException($"读取 ESP 寄存器 0x{address:X8} 失败。");
        return result.Value; // READ_REG value is in the response header, not status payload.
    }

    private Task<byte[]> WriteRegisterAsync(uint address, uint value, CancellationToken token) =>
        CheckedAsync(0x09, Words(address, value, uint.MaxValue, 0), token);

    private async Task<byte[]> CheckedAsync(byte command, byte[] payload, CancellationToken token,
        uint checksum = 0, TimeSpan? timeout = null)
    {
        var response = await CommandAsync(command, payload, checksum, timeout ?? TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
        var statusSize = _chip?.Esp8266 == true ? 2 : 4;
        if (response.Data.Length < statusSize) throw new InvalidDataException($"ESP 0x{command:X2} 状态响应不完整。");
        var status = response.Data.Length - statusSize;
        if (response.Data[status] != 0)
            throw new IOException($"ESP ROM 命令 0x{command:X2} 失败：0x{response.Data[status + 1]:X2}。");
        return response.Data[..status];
    }

    private async Task<(uint Value, byte[] Data)> CommandAsync(byte command, ReadOnlyMemory<byte> payload,
        uint checksum, TimeSpan timeout, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        try
        {
            var packet = new byte[8 + payload.Length];
            packet[1] = command;
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), checked((ushort)payload.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), checksum);
            payload.CopyTo(packet.AsMemory(8));
            await _channel.SendAsync(SlipEncode(packet), deadline.Token).ConfigureAwait(false);
            while (true)
            {
                var frame = await ReadFrameAsync(deadline.Token).ConfigureAwait(false);
                if (frame.Length < 8 || frame[0] != 1 || frame[1] != command) continue;
                var length = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(2, 2));
                if (length != frame.Length - 8) throw new InvalidDataException("ESP ROM 响应长度不一致。");
                return (BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4, 4)), frame[8..]);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException($"ESP ROM 命令 0x{command:X2} 响应超时。");
        }
    }

    private async Task<byte[]> ReadFrameAsync(CancellationToken token)
    {
        var frame = new List<byte>(128);
        var started = false;
        var escaped = false;
        while (true)
        {
            if (_receiveOverflow) throw new IOException("ESP 接收缓冲溢出，请重新连接。");
            var value = await _received.Reader.ReadAsync(token).ConfigureAwait(false);
            if (value == 0xC0)
            {
                if (escaped) throw new InvalidDataException("ESP SLIP 转义未完成。");
                if (started && frame.Count != 0) return frame.ToArray();
                started = true;
                continue;
            }
            if (!started) continue; // Boot messages before the first frame are not SLIP data.
            if (escaped)
            {
                frame.Add(value switch { 0xDC => 0xC0, 0xDD => 0xDB, _ => throw new InvalidDataException("ESP SLIP 转义无效。") });
                escaped = false;
            }
            else if (value == 0xDB) escaped = true;
            else frame.Add(value);
            if (frame.Count > 65543) throw new InvalidDataException("ESP 响应帧过长。");
        }
    }

    private static byte[] SlipEncode(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(data.Length + 2) { 0xC0 };
        foreach (var value in data)
        {
            if (value == 0xC0) output.AddRange([0xDB, 0xDC]);
            else if (value == 0xDB) output.AddRange([0xDB, 0xDD]);
            else output.Add(value);
        }
        output.Add(0xC0);
        return output.ToArray();
    }

    private static uint Checksum(ReadOnlySpan<byte> bytes)
    {
        byte checksum = 0xEF;
        foreach (var value in bytes) checksum ^= value;
        return checksum;
    }

    private static byte[] Words(params uint[] values)
    {
        var output = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(i * 4, 4), values[i]);
        return output;
    }

    private void ValidateRange(uint address, int length)
    {
        if (length <= 0 || (ulong)address + (ulong)length > FlashSize) throw new ArgumentOutOfRangeException(nameof(length), "读取区域超出 Flash 容量。");
    }
    private void EnsureConnected()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsConnected || _chip is null || IsMonitoring) throw new InvalidOperationException("请重新连接 ESP 下载模式后操作。");
    }
    private void EnsureWritable()
    {
        EnsureConnected();
        if (WriteProtected) throw new InvalidOperationException("检测到安全启动 / Flash 加密，禁止普通擦除和烧录。");
    }

    private async Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken token, bool invalidateOnFailure = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _operation.WaitAsync(linked.Token).ConfigureAwait(false);
        try { await action(linked.Token).ConfigureAwait(false); }
        catch
        {
            if (invalidateOnFailure) IsConnected = false;
            throw;
        }
        finally { _operation.Release(); }
    }

    private void OnDataReceived(object? sender, ReadOnlyMemory<byte> data)
    {
        if (_disposed) return;
        var monitor = _monitorHandler;
        if (monitor is not null) { monitor(sender, data); return; }
        foreach (var value in data.Span) if (!_received.Writer.TryWrite(value)) { _receiveOverflow = true; break; }
    }
    private void DrainReceived()
    {
        while (_received.Reader.TryRead(out _)) { }
        _receiveOverflow = false;
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        await _operation.WaitAsync().ConfigureAwait(false);
        try
        {
            _monitorHandler = null;
            _channel.DataReceived -= OnDataReceived;
            _received.Writer.TryComplete();
            await _channel.DisposeAsync().ConfigureAwait(false);
            IsConnected = false;
        }
        finally { _operation.Release(); _lifetime.Dispose(); }
    }
    private sealed class CallbackProgress(Action<EspOperationProgress> callback) : IProgress<EspOperationProgress>
    {
        public void Report(EspOperationProgress value) => callback(value);
    }
}
