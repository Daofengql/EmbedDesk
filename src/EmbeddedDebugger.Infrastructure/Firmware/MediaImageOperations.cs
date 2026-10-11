using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace EmbeddedDebugger.Infrastructure.Firmware;

public sealed record MediaProgress(string Phase, long Completed, long Total)
{
    public double Percent => Total > 0 ? Completed * 100d / Total : 0;
}

/// <summary>Stream algorithms shared by physical media and file-backed regression tests.</summary>
public static class MediaImageOperations
{
    public static async Task<string> WriteAndVerifyAsync(Stream source, Stream target, long capacity, int sectorSize,
        IProgress<MediaProgress>? progress = null, CancellationToken token = default)
    {
        if (sectorSize is not (512 or 4096) || !source.CanSeek || !target.CanSeek) throw new ArgumentException("不支持的介质扇区或数据流。");
        var size = source.Length;
        var paddedLength = checked((size + sectorSize - 1) / sectorSize * sectorSize);
        if (size <= 0 || paddedLength > capacity) throw new IOException("镜像为空或超过目标容量。");
        var bytes = new byte[1024 * 1024];
        source.Position = 0; target.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long done = 0;
        while (done < size)
        {
            var count = (int)Math.Min(bytes.Length, size - done);
            await source.ReadExactlyAsync(bytes.AsMemory(0, count), token).ConfigureAwait(false);
            hash.AppendData(bytes, 0, count);
            var aligned = (count + sectorSize - 1) / sectorSize * sectorSize;
            bytes.AsSpan(count, aligned - count).Clear();
            await target.WriteAsync(bytes.AsMemory(0, aligned), token).ConfigureAwait(false);
            done += count; progress?.Report(new("写入", done, size));
        }
        await target.FlushAsync(token).ConfigureAwait(false);
        if (target is FileStream file) file.Flush(true);
        var expected = hash.GetHashAndReset();
        target.Position = 0; done = 0;
        while (done < size)
        {
            var count = (int)Math.Min(bytes.Length, size - done);
            var aligned = (count + sectorSize - 1) / sectorSize * sectorSize;
            await target.ReadExactlyAsync(bytes.AsMemory(0, aligned), token).ConfigureAwait(false);
            hash.AppendData(bytes, 0, count);
            done += count; progress?.Report(new("回读校验", done, size));
        }
        if (!CryptographicOperations.FixedTimeEquals(expected, hash.GetHashAndReset())) throw new IOException("镜像回读 SHA-256 不匹配，请检查介质。");
        return Convert.ToHexString(expected);
    }

    public static async Task FormatFat32Async(Stream target, long capacity, string label, IProgress<MediaProgress>? progress = null, CancellationToken token = default)
    {
        if (capacity < 256L * 1024 * 1024 || capacity > 2L * 1024 * 1024 * 1024 * 1024) throw new ArgumentException("FAT32 支持 256 MiB–2 TiB 介质。");
        if (label.Length > 11 || label.Any(c => c < 32 || c > 126 || "\"*+,./:;<=>?[\\]|".Contains(c))) throw new ArgumentException("卷标应为最多 11 位 ASCII 字母、数字或空格。");
        const uint start = 2048, reserved = 32;
        uint total = checked((uint)(capacity / 512 - start));
        byte spc = capacity < 8L * 1024 * 1024 * 1024 ? (byte)8 : (byte)64;
        uint fatSectors = checked((uint)(((ulong)(total - reserved) / spc + 2) * 4 + 511) / 512);
        var clusters = (total - reserved - 2 * fatSectors) / spc;
        if (clusters < 65525 || clusters >= 0x0FFFFFF5) throw new ArgumentException("容量不适合 FAT32。");
        var zero = new byte[1024 * 1024];
        var metadataLength = ((long)start + reserved + 2 * fatSectors + spc) * 512;
        target.Position = 0;
        for (long done = 0; done < metadataLength;)
        {
            var count = (int)Math.Min(zero.Length, metadataLength - done);
            await target.WriteAsync(zero.AsMemory(0, count), token).ConfigureAwait(false);
            done += count; progress?.Report(new("创建 FAT32", done, metadataLength));
        }
        // Remove a former GPT backup table; the new partition contains no user data yet.
        target.Position = capacity / 512 * 512 - 34 * 512;
        await target.WriteAsync(new byte[34 * 512], token).ConfigureAwait(false);
        var mbr = new byte[512];
        Guid.NewGuid().ToByteArray().AsSpan(0, 4).CopyTo(mbr.AsSpan(440));
        mbr[450] = 0x0C; mbr[447] = mbr[451] = 0xFE; mbr[448] = mbr[449] = mbr[452] = mbr[453] = 0xFF;
        U32(mbr, 454, start); U32(mbr, 458, total); Signature(mbr);
        var boot = new byte[512];
        boot[0] = 0xEB; boot[1] = 0x58; boot[2] = 0x90; Encoding.ASCII.GetBytes("EDBG    ").CopyTo(boot, 3);
        U16(boot, 11, 512); boot[13] = spc; U16(boot, 14, (ushort)reserved); boot[16] = 2;
        boot[21] = 0xF8; U16(boot, 24, 63); U16(boot, 26, 255); U32(boot, 28, start); U32(boot, 32, total);
        U32(boot, 36, fatSectors); U32(boot, 44, 2); U16(boot, 48, 1); U16(boot, 50, 6);
        boot[64] = 0x80; boot[66] = 0x29; mbr.AsSpan(440, 4).CopyTo(boot.AsSpan(67));
        Encoding.ASCII.GetBytes(label.ToUpperInvariant().PadRight(11)).CopyTo(boot, 71);
        Encoding.ASCII.GetBytes("FAT32   ").CopyTo(boot, 82); Signature(boot);
        var info = new byte[512];
        U32(info, 0, 0x41615252); U32(info, 484, 0x61417272); U32(info, 488, clusters - 1); U32(info, 492, 3); Signature(info);
        var fat = new byte[512]; U32(fat, 0, 0x0FFFFFF8); U32(fat, 4, 0xFFFFFFFF); U32(fat, 8, 0x0FFFFFFF);
        await Sector(0, mbr); await Sector(start, boot); await Sector(start + 6, boot);
        await Sector(start + 1, info); await Sector(start + 7, info);
        await Sector(start + reserved, fat); await Sector(start + reserved + fatSectors, fat);
        if (!string.IsNullOrWhiteSpace(label))
        {
            var root = new byte[512]; Encoding.ASCII.GetBytes(label.ToUpperInvariant().PadRight(11)).CopyTo(root, 0); root[11] = 8;
            await Sector(start + reserved + 2 * fatSectors, root);
        }
        await target.FlushAsync(token).ConfigureAwait(false);
        if (target is FileStream file) file.Flush(true);
        // Read back both boot copies, FSInfo and the FAT headers.
        foreach (var (sector, expected) in new (long, byte[])[] { (0, mbr), (start, boot), (start + 6, boot), (start + 1, info), (start + reserved, fat), (start + reserved + fatSectors, fat) })
        {
            target.Position = sector * 512; var actual = new byte[512];
            await target.ReadExactlyAsync(actual, token).ConfigureAwait(false);
            if (!actual.AsSpan().SequenceEqual(expected)) throw new IOException("FAT32 元数据回读校验失败。");
        }
        progress?.Report(new("FAT32 完成", 1, 1));

        async Task Sector(long sector, byte[] bytes) { target.Position = sector * 512; await target.WriteAsync(bytes, token).ConfigureAwait(false); }
    }

    private static void U16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    private static void U32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void Signature(byte[] bytes) { bytes[510] = 0x55; bytes[511] = 0xAA; }
}

