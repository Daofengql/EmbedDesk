using System.Security.Cryptography;

namespace EmbeddedDebugger.Infrastructure.Firmware;

public enum FirmwareImageFormat { Binary, IntelHex, MotorolaSRecord }

public sealed record FirmwareImageSummary(string FilePath, FirmwareImageFormat Format, long Size, string Sha256,
    long DataBytes, uint? StartAddress, uint? EndAddress, int RecordCount, IReadOnlyList<string> Warnings)
{
    public string AddressRange => StartAddress is { } start && EndAddress is { } end
        ? $"0x{start:X8} - 0x{end:X8} ({(ulong)end - start + 1:N0} 字节地址空间)" : "由烧录起始地址决定";
}

public static class FirmwareImageAnalyzer
{
    public static async Task<FirmwareImageSummary> AnalyzeAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        var format = FirmwareImageLoader.DetectFormat(path);
        if (format == FirmwareImageFormat.Binary) return new(path, format, stream.Length, hash, stream.Length, null, null, 0, []);
        var segments = await FirmwareImageLoader.LoadAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new(path, format, stream.Length, hash, segments.Sum(s => (long)s.Data.Length), segments[0].Address,
            checked((uint)((ulong)segments[^1].Address + (uint)segments[^1].Data.Length - 1)), segments.Count, []);
    }
}

