using System.IO;

namespace EmbedDesk.App;

public sealed class LocalShellStartupOptions
{
    public const int MaximumDirectories = 16;
    public IReadOnlyList<string> Directories { get; }

    public LocalShellStartupOptions(IEnumerable<string> paths)
    {
        var directories = new List<string>();
        var count = 0;
        foreach (var path in paths)
        {
            if (++count > MaximumDirectories) throw new ArgumentException("一次最多打开 16 个目录。");
            if (string.IsNullOrWhiteSpace(path) || path.Length > 32767 || path.Any(char.IsControl))
                throw new ArgumentException("请拖入有效目录。");
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (!Directory.Exists(fullPath)) throw new ArgumentException("仅支持已存在的目录，不支持文件或命令参数。");
            if (!directories.Contains(fullPath, StringComparer.OrdinalIgnoreCase)) directories.Add(fullPath);
        }
        Directories = directories.AsReadOnly();
    }
}
