using System.Text;

namespace EmbeddedDebugger.Infrastructure.Firmware;

public sealed record FirmwareMemorySegment(uint Address, byte[] Data);

public static class FirmwareImageLoader
{
    public const int MaximumFileBytes = 64 * 1024 * 1024;

    public static async Task<IReadOnlyList<FirmwareMemorySegment>> LoadAsync(string path, uint binaryAddress = 0x08000000, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (stream.Length is <= 0 or > MaximumFileBytes) throw new FormatException("固件大小必须在 1 字节到 64 MiB 之间。");
        var format = DetectFormat(path);
        if (format == FirmwareImageFormat.Binary)
        {
            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            CheckRange(binaryAddress, bytes.Length);
            return [new(binaryAddress, bytes)];
        }
        using var reader = new StreamReader(stream, Encoding.ASCII, true);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        return ParseRecords(text, format, cancellationToken);
    }

    public static FirmwareImageFormat DetectFormat(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".hex" or ".ihx" => FirmwareImageFormat.IntelHex,
        ".s19" or ".s28" or ".s37" or ".srec" or ".mot" => FirmwareImageFormat.MotorolaSRecord,
        _ => FirmwareImageFormat.Binary
    };

    public static IReadOnlyList<FirmwareMemorySegment> ParseRecords(string text, FirmwareImageFormat format, CancellationToken token = default)
    {
        if (format == FirmwareImageFormat.Binary || text.Length > MaximumFileBytes) throw new FormatException("记录格式或文件大小无效。");
        var segments = new List<FirmwareMemorySegment>();
        using var reader = new StringReader(text);
        uint baseAddress = 0;
        var ended = false; var records = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            token.ThrowIfCancellationRequested();
            line = line.Trim(); if (line.Length == 0) continue;
            records++;
            if (ended) throw new FormatException($"终止记录之后还有内容（第 {records} 行）。");
            if (line.Length > 520) throw new FormatException($"记录过长（第 {records} 行）。");
            byte[] bytes;
            if (format == FirmwareImageFormat.IntelHex)
            {
                if (!line.StartsWith(':')) throw new FormatException("Intel HEX 前缀错误。");
                bytes = Convert.FromHexString(line[1..]);
                if (bytes.Length < 5 || bytes[0] != bytes.Length - 5 || (bytes.Sum(b => b) & 255) != 0) throw new FormatException($"HEX 长度或校验和错误（第 {records} 行）。");
                var offset = (uint)((bytes[1] << 8) | bytes[2]);
                var count = bytes[0]; var type = bytes[3];
                if (type != 0 && offset != 0) throw new FormatException("控制记录地址必须为 0。");
                switch (type)
                {
                    case 0:
                        if (offset + count > 65536) throw new FormatException("HEX 数据跨越 64 KiB 边界。");
                        if (count > 0) Add(checked(baseAddress + offset), bytes[4..(4 + count)]);
                        break;
                    case 1: RequireCount(count, 0); ended = true; break;
                    case 2: RequireCount(count, 2); baseAddress = (uint)((bytes[4] << 8) | bytes[5]) << 4; break;
                    case 4: RequireCount(count, 2); baseAddress = (uint)((bytes[4] << 8) | bytes[5]) << 16; break;
                    case 3 or 5: RequireCount(count, 4); break;
                    default: throw new FormatException($"不支持的 HEX 记录类型 {type}。");
                }
            }
            else
            {
                if (line.Length < 4 || line[0] != 'S') throw new FormatException("S-Record 前缀错误。");
                var type = line[1];
                var addressBytes = type switch { '0' or '1' or '5' or '9' => 2, '2' or '6' or '8' => 3, '3' or '7' => 4, _ => throw new FormatException("不支持的 S-Record 类型。") };
                bytes = Convert.FromHexString(line[2..]);
                if (bytes.Length < addressBytes + 2 || bytes[0] != bytes.Length - 1 || (bytes.Sum(b => b) & 255) != 255) throw new FormatException($"S-Record 长度或校验和错误（第 {records} 行）。");
                uint address = 0;
                for (var i = 1; i <= addressBytes; i++) address = (address << 8) | bytes[i];
                if (type is '1' or '2' or '3') { if (bytes.Length > addressBytes + 2) Add(address, bytes[(addressBytes + 1)..^1]); }
                if (type is '5' or '6') { RequireCount(bytes.Length, addressBytes + 2); if (address != segments.Count) throw new FormatException("S-Record 数据记录计数不匹配。"); }
                if (type is '7' or '8' or '9') { RequireCount(bytes.Length, addressBytes + 2); ended = true; }
            }
        }
        if (!ended || segments.Count == 0) throw new FormatException("缺少终止记录或有效固件数据。");
        segments.Sort((a, b) => a.Address.CompareTo(b.Address));
        for (var i = 1; i < segments.Count; i++)
            if ((ulong)segments[i - 1].Address + (uint)segments[i - 1].Data.Length > segments[i].Address) throw new FormatException("固件记录地址重叠，拒绝烧录。");
        var merged = new List<FirmwareMemorySegment>();
        for (var i = 0; i < segments.Count;)
        {
            var start = segments[i].Address;
            using var buffer = new MemoryStream();
            do { buffer.Write(segments[i].Data); i++; }
            while (i < segments.Count && (ulong)start + (ulong)buffer.Length == segments[i].Address);
            merged.Add(new(start, buffer.ToArray()));
        }
        return merged;

        void Add(uint address, byte[] bytes) { CheckRange(address, bytes.Length); segments.Add(new(address, bytes)); }
    }

    private static void RequireCount(int actual, int expected) { if (actual != expected) throw new FormatException("控制记录长度无效。"); }
    private static void CheckRange(uint address, int length) { if ((ulong)address + (uint)length > 0x100000000UL) throw new FormatException("固件地址溢出。"); }
}

