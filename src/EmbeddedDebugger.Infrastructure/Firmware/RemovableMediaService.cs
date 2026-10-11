using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EmbeddedDebugger.Infrastructure.Firmware;

public sealed record RemovableMediaInfo(int Index, string Model, long Size, string UniqueId, string SerialNumber, int SectorSize)
{
    public string DevicePath => $@"\\.\PhysicalDrive{Index}";
    public string DisplayName => $"磁盘 {Index} · {Model} · {Size / 1073741824d:0.0} GiB · {SerialNumber}";
}

/// <summary>Only non-system USB/SD/MMC disks can be selected; fail closed when metadata is unavailable.</summary>
public static class RemovableMediaService
{
    private const string Scope = @"\\.\root\Microsoft\Windows\Storage";

    public static Task<IReadOnlyList<RemovableMediaInfo>> EnumerateAsync(CancellationToken token = default) => Task.Run<IReadOnlyList<RemovableMediaInfo>>(() =>
    {
        var result = new List<RemovableMediaInfo>();
        using var searcher = new ManagementObjectSearcher(Scope, "SELECT * FROM MSFT_Disk");
        using var disks = searcher.Get();
        foreach (ManagementObject disk in disks)
        using (disk)
        {
            token.ThrowIfCancellationRequested();
            if (disk["IsBoot"] is not false || disk["IsSystem"] is not false || disk["IsReadOnly"] is not false || disk["IsOffline"] is not false) continue;
            if (Convert.ToInt32(disk["BusType"]) is not (7 or 12 or 13)) continue;
            var uniqueId = disk["UniqueId"]?.ToString()?.Trim() ?? "";
            var size = Convert.ToInt64(disk["Size"]);
            var sector = Convert.ToInt32(disk["LogicalSectorSize"]);
            if (size <= 0 || uniqueId.Length == 0 || sector is not (512 or 4096)) continue;
            result.Add(new(Convert.ToInt32(disk["Number"]), disk["FriendlyName"]?.ToString() ?? "可移动磁盘",
                size, uniqueId, disk["SerialNumber"]?.ToString()?.Trim() ?? "", sector));
        }
        return result;
    }, token);

    public static Task WriteImageAsync(string imagePath, RemovableMediaInfo target, IProgress<MediaProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            // Hold the source open against replacement through write and verification.
            await using var input = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, true);
            if (input.Length <= 0 || checked((input.Length + target.SectorSize - 1) / target.SectorSize * target.SectorSize) > target.Size)
                throw new IOException("镜像为空或超过磁盘容量。");
            using var lease = await OpenExclusiveAsync(target, cancellationToken).ConfigureAwait(false);
            await MediaImageOperations.WriteAndVerifyAsync(input, lease.Stream, target.Size, target.SectorSize, progress, cancellationToken).ConfigureAwait(false);
            lease.Refresh();
        }, cancellationToken);

    public static Task FormatFat32Async(RemovableMediaInfo target, string label, IProgress<MediaProgress>? progress = null, CancellationToken token = default) =>
        Task.Run(async () =>
        {
            if (target.SectorSize != 512) throw new NotSupportedException("FAT32 格式化目前仅支持 512 字节逻辑扇区。");
            using var lease = await OpenExclusiveAsync(target, token).ConfigureAwait(false);
            await MediaImageOperations.FormatFat32Async(lease.Stream, target.Size, label, progress, token).ConfigureAwait(false);
            lease.Refresh();
        }, token);

    private static async Task<DiskLease> OpenExclusiveAsync(RemovableMediaInfo expected, CancellationToken token)
    {
        await CheckIdentityAsync(expected, token).ConfigureAwait(false);
        var lease = new DiskLease();
        try
        {
            // MSFT_Partition includes GUID volume paths even when no drive letter is assigned.
            using var searcher = new ManagementObjectSearcher(Scope, $"SELECT * FROM MSFT_Partition WHERE DiskNumber = {expected.Index}");
            using var partitions = searcher.Get();
            foreach (ManagementObject partition in partitions)
            using (partition)
            {
                if (partition["IsBoot"] is not false || partition["IsSystem"] is not false) throw new IOException("目标包含系统或引导分区，已拒绝操作。");
                var paths = partition["AccessPaths"] as string[] ?? [];
                var volumePath = paths.FirstOrDefault(p => p.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase));
                // An existing formatted partition without a volume path cannot be locked safely.
                if (volumePath is null)
                {
                    if (paths.Length > 0) throw new IOException("无法确定分区卷路径，已拒绝操作。");
                    continue;
                }
                var handle = Open(volumePath.TrimEnd('\\'));
                lease.Volumes.Add(handle);
                Control(handle, 0x00090018); // FSCTL_LOCK_VOLUME; fails if source image or other files are open on target.
                Control(handle, 0x00090020); // FSCTL_DISMOUNT_VOLUME
            }
            token.ThrowIfCancellationRequested();
            var raw = Open(expected.DevicePath);
            lease.Stream = new FileStream(raw, FileAccess.ReadWrite, 1048576, false);
            var length = new byte[8];
            if (!DeviceIoControl(raw, 0x0007405C, null, 0, length, length.Length, out _, IntPtr.Zero) || BitConverter.ToInt64(length) != expected.Size)
                throw new IOException("磁盘容量校验失败。");
            var number = new byte[12];
            if (!DeviceIoControl(raw, 0x002D1080, null, 0, number, number.Length, out _, IntPtr.Zero) || BitConverter.ToInt32(number, 4) != expected.Index)
                throw new IOException("磁盘句柄身份校验失败。");
            await CheckIdentityAsync(expected, token).ConfigureAwait(false);
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private static async Task CheckIdentityAsync(RemovableMediaInfo expected, CancellationToken token)
    {
        var actual = (await EnumerateAsync(token).ConfigureAwait(false)).SingleOrDefault(d => d.Index == expected.Index);
        if (actual != expected) throw new IOException("磁盘已拔出或身份发生变化，请刷新后重新选择。");
    }

    private sealed class DiskLease : IDisposable
    {
        public List<SafeFileHandle> Volumes { get; } = [];
        public FileStream Stream { get; set; } = null!;
        public void Refresh() { Stream.Flush(true); Control(Stream.SafeFileHandle, 0x00070140); }
        public void Dispose()
        {
            Stream?.Dispose();
            foreach (var volume in Volumes) volume.Dispose(); // Closing releases the volume locks.
        }
    }
    private static SafeFileHandle Open(string path)
    {
        var handle = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x80000000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error(); handle.Dispose();
        throw new IOException("无法独占访问磁盘，请使用管理员权限并关闭目标盘上的文件。", new Win32Exception(error));
    }
    private static void Control(SafeFileHandle handle, uint code)
    {
        if (!DeviceIoControl(handle, code, null, 0, null, 0, out _, IntPtr.Zero))
            throw new IOException("磁盘锁定、卸载或刷新失败，操作已停止。", new Win32Exception(Marshal.GetLastWin32Error()));
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input, int inputSize, byte[]? output, int outputSize, out int returned, IntPtr overlapped);
}

