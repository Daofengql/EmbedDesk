using EmbedDesk.Core.Models;

namespace EmbedDesk.App.ViewModels;

public sealed class RemoteDirectoryEntryViewModel
{
    public RemoteDirectoryEntryViewModel(RemoteDirectoryEntry entry)
    {
        Name = entry.Name;
        FullPath = entry.FullPath;
        IsDirectory = entry.IsDirectory;
        Size = entry.Size;
        Modified = entry.Modified;
        Permissions = entry.Permissions;
    }

    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }
    public long Size { get; }
    public DateTimeOffset? Modified { get; }
    public string Permissions { get; }
    public string TypeLabel => IsDirectory ? "目录" : "文件";
    public string SizeLabel => IsDirectory ? "-" : FormatSize(Size);
    public string ModifiedLabel => Modified?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-";
    public string IconGlyph => IsDirectory ? "\uE8B7" : "\uE8A5";

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:0.#} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024d * 1024):0.#} MB";
        return $"{bytes / (1024d * 1024 * 1024):0.#} GB";
    }
}
