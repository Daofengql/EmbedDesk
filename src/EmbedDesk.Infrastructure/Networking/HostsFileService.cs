using System.Runtime.InteropServices;
using System.Text;
using EmbedDesk.Core.Models;

namespace EmbedDesk.Infrastructure.Networking;

public sealed record HostsFileSnapshot(byte[] Bytes, string Text);

/// <summary>Edits only our marked block, preserving all other content and its encoding.</summary>
public sealed class HostsFileService(string? hostsPath = null)
{
    public const string BeginMarker = "# >>> EmbedDesk Hosts";
    public const string EndMarker = "# <<< EmbedDesk Hosts";
    public const int MaximumBytes = 1024 * 1024;
    public string HostsPath { get; } = hostsPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    public HostsFileSnapshot Read()
    {
        using var stream = new FileStream(HostsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length > MaximumBytes) throw new InvalidDataException("Hosts 文件超过 1 MiB，拒绝自动编辑。");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return Decode(bytes);
    }

    public static HostsFileSnapshot Decode(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Hosts 文件超过 1 MiB。");
        var (encoding, preamble) = GetEncoding(bytes);
        string text;
        try { text = encoding.GetString(bytes, preamble, bytes.Length - preamble); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("文件不是有效的 UTF-8 / UTF-16，拒绝覆盖原始编码。", ex); }
        if (text.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t'))
            throw new InvalidDataException("Hosts 文件包含异常控制字符，拒绝覆盖。");
        return new(bytes, text);
    }

    public static byte[] CreateManagedContent(HostsFileSnapshot snapshot, IEnumerable<HostsMapping> mappings)
    {
        var enabled = HostsMapping.Validate(mappings).Where(m => m.Enabled).ToArray();
        var (start, end) = FindBlock(snapshot.Text);
        var outside = start < 0 ? snapshot.Text : snapshot.Text.Remove(start, end - start);
        var domains = enabled.Select(m => m.Domain).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var line in outside.Split('\n'))
        {
            var fields = line.Split('#')[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length > 1 && System.Net.IPAddress.TryParse(fields[0], out _) && fields.Skip(1).Any(d => domains.Contains(d.TrimEnd('.'))))
                throw new InvalidOperationException("启用的域名与管理区块外的 Hosts 规则冲突，请先检查系统 Hosts。");
        }
        var newline = snapshot.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var block = enabled.Length == 0 ? "" : BeginMarker + newline
            + string.Join(newline, enabled.Select(m => $"{m.Address}\t{m.Domain}" + (m.Comment.Length == 0 ? "" : " # " + m.Comment)))
            + newline + EndMarker + newline;
        var result = start >= 0 ? snapshot.Text[..start] + block + snapshot.Text[end..]
            : snapshot.Text + (block.Length > 0 && snapshot.Text.Length > 0 && !snapshot.Text.EndsWith('\n') ? newline : "") + block;
        var (encoding, _) = GetEncoding(snapshot.Bytes);
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(result)).ToArray();
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("生成的 Hosts 文件超过 1 MiB。");
        return bytes;
    }

    public string Apply(HostsFileSnapshot expected, byte[] replacement, string backupDirectory)
    {
        _ = Decode(replacement);
        // Deny competing writes and replacement while checking, backing up and writing.
        using var stream = new FileStream(HostsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        if (stream.Length != expected.Bytes.Length) throw Changed();
        var current = new byte[checked((int)stream.Length)]; stream.ReadExactly(current);
        if (!current.AsSpan().SequenceEqual(expected.Bytes)) throw Changed();
        Directory.CreateDirectory(backupDirectory);
        var backup = Path.Combine(backupDirectory, $"hosts-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.bak");
        using (var saved = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { saved.Write(current); saved.Flush(true); }
        try
        {
            stream.Position = 0; stream.Write(replacement); stream.SetLength(replacement.Length); stream.Flush(true);
        }
        catch (IOException failure)
        {
            try { stream.Position = 0; stream.Write(current); stream.SetLength(current.Length); stream.Flush(true); }
            catch (IOException rollback) { throw new IOException("Hosts 写入及恢复均失败，请使用备份恢复。", new AggregateException(failure, rollback)); }
            throw;
        }
        return backup;
    }

    private static (int Start, int End) FindBlock(string text)
    {
        var start = -1; var end = -1; var offset = 0;
        foreach (var line in text.Split('\n'))
        {
            var marker = line.Trim();
            if (marker.Contains(BeginMarker, StringComparison.Ordinal) || marker.Contains(EndMarker, StringComparison.Ordinal))
            {
                if (marker == BeginMarker && start < 0 && end < 0) start = offset;
                else if (marker == EndMarker && start >= 0 && end < 0) end = Math.Min(text.Length, offset + line.Length + 1);
                else throw new InvalidDataException("EmbedDesk 管理区块标记损坏或重复，拒绝覆盖。");
            }
            offset += line.Length + 1;
        }
        if (start >= 0 && end < 0) throw new InvalidDataException("EmbedDesk 管理区块缺少结束标记，拒绝覆盖。");
        return (start, end);
    }

    private static (Encoding Encoding, int Preamble) GetEncoding(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 }) || bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF }))
            throw new InvalidDataException("不支持 UTF-32 Hosts 文件。");
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) return (new UnicodeEncoding(false, true, true), 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) return (new UnicodeEncoding(true, true, true), 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return (new UTF8Encoding(true, true), 3);
        return (new UTF8Encoding(false, true), 0);
    }

    private static IOException Changed() => new("Hosts 已被其他程序修改，请重新读取后再应用。");

    public static bool FlushDnsCache() => OperatingSystem.IsWindows() && DnsFlushResolverCache();
    [DllImport("dnsapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DnsFlushResolverCache();
}
