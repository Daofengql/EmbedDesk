using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace EmbedDesk.App;

public partial class LocalInfoWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private long _previousIdle;
    private long _previousTotal;
    private bool _hasCpuSample;

    public LocalInfoWindow()
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        _timer.Tick += (_, _) => RefreshInformation();
        Loaded += (_, _) => { RefreshInformation(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
    }

    private void RefreshInformation()
    {
        try
        {
            var cpuName = ReadCpuName();
            CpuText.Text = $"{cpuName}\n{Environment.ProcessorCount} 个逻辑处理器";
            CpuUsageText.Text = TryReadCpuUsage() is { } cpu ? $"使用率 {cpu:F0}%" : "使用率采集中…";

            if (TryReadMemory(out var totalMemory, out var availableMemory))
                MemoryText.Text = $"总计 {FormatBytes(totalMemory)}\n可用 {FormatBytes(availableMemory)} · 已用 {FormatBytes(totalMemory - availableMemory)}";
            else MemoryText.Text = "内存状态不可用";

            OsText.Text = $"{ReadWindowsName()}\n{RuntimeInformation.OSArchitecture} · {Environment.MachineName}";
            RuntimeText.Text = $".NET {Environment.Version}";
            var ports = System.IO.Ports.SerialPort.GetPortNames().Order(StringComparer.OrdinalIgnoreCase).ToArray();
            DeviceText.Text = ports.Length == 0 ? "未发现串口" : $"串口：{string.Join(", ", ports)}";
            UptimeText.Text = $"运行时间 {FormatDuration(TimeSpan.FromMilliseconds(Environment.TickCount64))}";
            AdaptersList.ItemsSource = ReadAdapters();
            DisksGrid.ItemsSource = ReadDisks();
            UpdatedAtText.Text = $"每 2 秒自动刷新 · {DateTime.Now:HH:mm:ss} · 信息仅在本机读取";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            UpdatedAtText.Text = $"读取部分本机信息失败：{ex.Message}";
        }
    }

    private static IReadOnlyList<AdapterInfo> ReadAdapters()
    {
        var result = new List<AdapterInfo>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var properties = adapter.GetIPProperties();
                var addresses = properties.UnicastAddresses
                    .Where(item => item.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    .Select(item => $"{item.Address}/{item.PrefixLength}")
                    .ToArray();
                var gateways = properties.GatewayAddresses.Select(item => item.Address.ToString())
                    .Where(item => !string.IsNullOrWhiteSpace(item)).Distinct().ToArray();
                var dns = properties.DnsAddresses.Select(item => item.ToString()).Distinct().ToArray();
                var mac = adapter.GetPhysicalAddress().GetAddressBytes();
                result.Add(new AdapterInfo(
                    adapter.Name,
                    adapter.Description,
                    adapter.OperationalStatus == OperationalStatus.Up ? "已连接" : adapter.OperationalStatus.ToString(),
                    mac.Length == 0 ? "—" : string.Join("-", mac.Select(value => value.ToString("X2"))),
                    addresses.Length == 0 ? "无 IP 地址" : string.Join(" · ", addresses),
                    gateways.Length == 0 ? "—" : string.Join(" · ", gateways),
                    dns.Length == 0 ? "—" : string.Join(" · ", dns)));
            }
            catch (NetworkInformationException)
            {
                result.Add(new AdapterInfo(adapter.Name, adapter.Description, "信息不可用", "—", "—", "—", "—"));
            }
        }
        return result;
    }

    private static IReadOnlyList<DiskInfo> ReadDisks()
    {
        var result = new List<DiskInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                result.Add(new DiskInfo(drive.Name, drive.DriveType.ToString(), drive.DriveFormat,
                    FormatBytes((ulong)drive.TotalSize), FormatBytes((ulong)drive.AvailableFreeSpace), drive.VolumeLabel));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result;
    }

    private static string ReadCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("ProcessorNameString") as string is { Length: > 0 } name ? name.Trim() : "处理器名称不可用";
        }
        catch (UnauthorizedAccessException) { return "处理器名称不可用"; }
    }

    private static string ReadWindowsName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = key?.GetValue("ProductName") as string ?? "Windows";
            var displayVersion = key?.GetValue("DisplayVersion") as string;
            var build = key?.GetValue("CurrentBuildNumber") as string;
            return string.Join(" · ", new[] { product, displayVersion, build is null ? null : $"Build {build}" }.Where(value => !string.IsNullOrWhiteSpace(value)));
        }
        catch (UnauthorizedAccessException) { return $"Windows {Environment.OSVersion.Version}"; }
    }

    private double? TryReadCpuUsage()
    {
        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime)) return null;
        var idle = FileTimeToInt64(idleTime);
        var total = FileTimeToInt64(kernelTime) + FileTimeToInt64(userTime);
        if (!_hasCpuSample)
        {
            _previousIdle = idle; _previousTotal = total; _hasCpuSample = true; return null;
        }
        var idleDelta = idle - _previousIdle;
        var totalDelta = total - _previousTotal;
        _previousIdle = idle; _previousTotal = total;
        return totalDelta <= 0 ? 0 : Math.Clamp(100d * (totalDelta - idleDelta) / totalDelta, 0, 100);
    }

    private static bool TryReadMemory(out ulong total, out ulong available)
    {
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        var success = GlobalMemoryStatusEx(ref memory);
        total = memory.TotalPhysical; available = memory.AvailablePhysical;
        return success;
    }

    private void CopyAddresses_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string addresses && addresses != "无 IP 地址")
        {
            try { Clipboard.SetText(addresses.Replace(" · ", Environment.NewLine, StringComparison.Ordinal)); }
            catch (System.Runtime.InteropServices.ExternalException) { }
        }
    }

    private static string FormatBytes(ulong bytes) => bytes switch
    {
        >= 1UL << 40 => $"{bytes / (double)(1UL << 40):N1} TiB",
        >= 1UL << 30 => $"{bytes / (double)(1UL << 30):N1} GiB",
        >= 1UL << 20 => $"{bytes / (double)(1UL << 20):N0} MiB",
        _ => $"{bytes:N0} B"
    };

    private static string FormatDuration(TimeSpan duration) => duration.TotalDays >= 1
        ? $"{(int)duration.TotalDays} 天 {duration.Hours} 小时"
        : duration.TotalHours >= 1 ? $"{(int)duration.TotalHours} 小时 {duration.Minutes} 分钟" : $"{duration.Hours} 小时 {duration.Minutes} 分钟";

    private static long FileTimeToInt64(System.Runtime.InteropServices.ComTypes.FILETIME time) =>
        ((long)(uint)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out System.Runtime.InteropServices.ComTypes.FILETIME idleTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME kernelTime, out System.Runtime.InteropServices.ComTypes.FILETIME userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    private sealed record AdapterInfo(string Name, string Description, string Status, string MacAddress, string Addresses, string Gateways, string DnsServers);
    private sealed record DiskInfo(string Name, string DriveType, string Format, string Total, string Free, string Label);
}
