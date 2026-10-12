using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace EmbedDesk.Infrastructure.Firmware;

public sealed record EspPartition(byte Type, byte Subtype, uint Offset, uint Size, string Label, uint Flags)
{
    public string AddressText => $"0x{Offset:X8}";
    public string SizeText => $"{Size:N0} B ({Size / 1024d:N1} KiB)";
    public string TypeText => Type switch
    {
        0 => Subtype switch { 0 => "应用 / factory", >= 0x10 and <= 0x1F => $"应用 / ota_{Subtype - 0x10}", 0x20 => "应用 / test", _ => $"应用 / 0x{Subtype:X2}" },
        1 => Subtype switch { 0 => "数据 / otadata", 1 => "数据 / phy", 2 => "数据 / NVS", 3 => "数据 / coredump", 4 => "数据 / nvs_keys", 0x81 => "数据 / FAT", 0x82 => "数据 / SPIFFS", 0x83 => "数据 / LittleFS", _ => $"数据 / 0x{Subtype:X2}" },
        _ => $"0x{Type:X2} / 0x{Subtype:X2}"
    };
    public string FlagsText => (Flags & 1) != 0 ? "加密" : "—";
}

/// <summary>ESP-IDF 32-byte binary partition entries, including the optional MD5 trailer.</summary>
public static class EspPartitionTable
{
    public static IReadOnlyList<EspPartition> Parse(ReadOnlySpan<byte> data, uint flashSize = 16 * 1024 * 1024)
    {
        if (data.Length < 32) throw new InvalidDataException("分区表至少需要 32 字节。");
        var partitions = new List<EspPartition>();
        var hasDigest = false;
        for (var offset = 0; offset + 32 <= data.Length; offset += 32)
        {
            var entry = data.Slice(offset, 32);
            var magic = BinaryPrimitives.ReadUInt16LittleEndian(entry);
            if (magic == 0xFFFF)
            {
                if (entry.IndexOfAnyExcept((byte)0xFF) >= 0) throw new InvalidDataException("分区表结束项被破坏。");
                break;
            }
            if (magic == 0xEBEB)
            {
                if (hasDigest || !CryptographicOperations.FixedTimeEquals(MD5.HashData(data[..offset]), entry[16..]))
                    throw new InvalidDataException("分区表 MD5 校验失败。");
                hasDigest = true;
                continue;
            }
            if (hasDigest || magic != 0x50AA) throw new InvalidDataException($"0x{offset:X} 不是有效的 ESP-IDF 分区项。");
            var labelBytes = entry.Slice(12, 16);
            var end = labelBytes.IndexOf((byte)0);
            var label = Encoding.UTF8.GetString(end >= 0 ? labelBytes[..end] : labelBytes);
            var partition = new EspPartition(entry[2], entry[3], BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]),
                BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]), label, BinaryPrimitives.ReadUInt32LittleEndian(entry[28..]));
            if (partition.Size == 0 || (ulong)partition.Offset + partition.Size > flashSize)
                throw new InvalidDataException($"分区 {label} 的地址或大小超出 Flash 范围。");
            if (partition.Offset % 4096 != 0 || (partition.Type == 0 && partition.Offset % 65536 != 0))
                throw new InvalidDataException($"分区 {label} 的起始地址未按 ESP-IDF 规则对齐。");
            if (partitions.Any(other => partition.Offset < (ulong)other.Offset + other.Size && other.Offset < (ulong)partition.Offset + partition.Size))
                throw new InvalidDataException($"分区 {label} 与其他分区重叠。");
            partitions.Add(partition);
        }
        if (partitions.Count == 0) throw new InvalidDataException("没有找到有效分区。请确认分区表地址（通常为 0x8000）。");
        return partitions;
    }

    public static string ToCsv(IEnumerable<EspPartition> partitions)
    {
        var output = new StringBuilder("# Name, Type, SubType, Offset, Size, Flags\r\n");
        foreach (var partition in partitions)
            output.AppendLine($"\"{partition.Label.Replace("\"", "\"\"", StringComparison.Ordinal)}\",0x{partition.Type:X2},0x{partition.Subtype:X2},0x{partition.Offset:X},0x{partition.Size:X},{((partition.Flags & 1) != 0 ? "encrypted" : "")}");
        return output.ToString();
    }
}
