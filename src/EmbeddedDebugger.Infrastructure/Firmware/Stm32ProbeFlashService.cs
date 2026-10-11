namespace EmbeddedDebugger.Infrastructure.Firmware;

/// <summary>A removable ST-LINK/DAPLink mass-storage target discovered by its standard marker files.</summary>
public sealed record Stm32ProbeDrive(string Root, string DisplayName, string? VolumeLabel, long FreeBytes);

/// <summary>
/// Implements the drag-and-drop firmware mode exposed by ST-LINK and DAPLink probes.
/// This intentionally does not pretend to be a SWD debugger: the probe firmware owns
/// programming, verification and target reset after the file is copied.
/// </summary>
public static class Stm32ProbeFlashService
{
    private static readonly string[] MarkerFiles = ["DETAILS.TXT", "MBED.HTM", "ST-LINK_CLI.exe", "ST-LINK.TXT", "FAIL.TXT"];
    private static readonly string[] FirmwareExtensions = [".bin", ".hex", ".ihx", ".s19", ".s28", ".s37", ".srec", ".mot"];

    public static Task<IReadOnlyList<Stm32ProbeDrive>> ScanAsync(CancellationToken token = default) => Task.Run<IReadOnlyList<Stm32ProbeDrive>>(() =>
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return Array.Empty<Stm32ProbeDrive>();
        var drives = new List<Stm32ProbeDrive>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (drive.DriveType != DriveType.Removable || !drive.IsReady || !IsProbeDrive(drive)) continue;
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? null : drive.VolumeLabel;
                var kind = label?.Contains("DAP", StringComparison.OrdinalIgnoreCase) == true ||
                    label?.Contains("MBED", StringComparison.OrdinalIgnoreCase) == true ? "DAPLink" : "ST-LINK / DAPLink";
                drives.Add(new Stm32ProbeDrive(drive.RootDirectory.FullName, $"{kind} · {label ?? drive.Name}", label, drive.AvailableFreeSpace));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return (IReadOnlyList<Stm32ProbeDrive>)drives.AsReadOnly();
    }, token);

    public static async Task CopyFirmwareAsync(string root, string firmwarePath, IProgress<long>? progress = null, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("请选择 ST-LINK / DAPLink 目标盘。", nameof(root));
        if (!File.Exists(firmwarePath)) throw new FileNotFoundException("固件文件不存在。", firmwarePath);
        if (!FirmwareExtensions.Contains(Path.GetExtension(firmwarePath), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("探针拖拽烧录仅接受 BIN、HEX 或 S-Record 文件。");
        var source = new FileInfo(firmwarePath);
        if (source.Length is <= 0 or > 32 * 1024 * 1024) throw new InvalidDataException("探针固件大小必须为 1 字节～32 MiB。");
        var destination = Path.Combine(root, Path.GetFileName(firmwarePath));
        var temporary = Path.Combine(root, "." + Path.GetFileName(firmwarePath) + ".edbg-partial");
        try
        {
            await using (var input = new FileStream(firmwarePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[65536]; long copied = 0;
                while (true)
                {
                    var count = await input.ReadAsync(buffer, token);
                    if (count == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    copied += count; progress?.Report(copied);
                }
                await output.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
            await Task.Delay(350, token); // Give the probe firmware time to create FAIL.TXT.
            var failure = MarkerFiles.Select(name => Path.Combine(root, name)).FirstOrDefault(File.Exists);
            if (failure is not null && Path.GetFileName(failure).Equals("FAIL.TXT", StringComparison.OrdinalIgnoreCase))
            {
                var detail = await File.ReadAllTextAsync(failure, token);
                throw new IOException($"探针报告烧录失败：{detail.Trim()}");
            }
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { /* device may have disappeared */ }
        }
    }

    private static bool IsProbeDrive(DriveInfo drive)
    {
        var label = drive.VolumeLabel ?? string.Empty;
        if (label.Contains("DAP", StringComparison.OrdinalIgnoreCase) || label.Contains("MBED", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("ST-LINK", StringComparison.OrdinalIgnoreCase) || label.Contains("NODE_", StringComparison.OrdinalIgnoreCase)) return true;
        return MarkerFiles.Any(name => File.Exists(Path.Combine(drive.RootDirectory.FullName, name)));
    }
}
