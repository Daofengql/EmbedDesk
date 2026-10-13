using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace EmbedDesk.App;

public sealed record NetworkDiscoveryDevice(string Status, string Address, string HostName, string MacAddress, string Latency);

public partial class NetworkDiscoveryWindow : Window, IDisposable
{
    private readonly List<NetworkRangeChoice> _ranges = [];
    private CancellationTokenSource? _lifetime;
    private Task? _worker;
    private NetworkRangeChoice? _selectedRange;
    private int _scanned;
    private int _online;
    private readonly Action<NetworkDiscoveryPreferences>? _savePreferences;

    public NetworkDiscoveryWindow(NetworkDiscoveryPreferences? preferences = null, Action<NetworkDiscoveryPreferences>? savePreferences = null)
    {
        _savePreferences = savePreferences;
        InitializeComponent();
        DataContext = this;
        PopulateInterfaces();
        if (preferences is not null) ApplyPreferences(preferences);
        WindowTheme.ApplyDarkTitleBar(this);
        Closed += (_, _) => Dispose();
        Closed += (_, _) => _savePreferences?.Invoke(CapturePreferences());
    }

    private void ApplyPreferences(NetworkDiscoveryPreferences preferences)
    {
        ConcurrencyBox.Text = preferences.Concurrency.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(preferences.LocalAddress)) return;
        InterfaceBox.SelectedItem = _ranges.FirstOrDefault(range => string.Equals(range.LocalAddress, preferences.LocalAddress, StringComparison.OrdinalIgnoreCase))
            ?? InterfaceBox.SelectedItem;
    }

    private NetworkDiscoveryPreferences CapturePreferences() => new()
    {
        LocalAddress = _selectedRange?.LocalAddress ?? string.Empty,
        Concurrency = int.TryParse(ConcurrencyBox.Text, out var concurrency) ? concurrency : 64
    };

    public ObservableCollection<NetworkDiscoveryDevice> Devices { get; } = [];

    private void PopulateInterfaces()
    {
        _ranges.Clear();
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces().Where(item => item.OperationalStatus == OperationalStatus.Up))
            {
                foreach (var unicast in network.GetIPProperties().UnicastAddresses.Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork && item.IPv4Mask is not null))
                {
                    var ip = ToUInt32(unicast.Address);
                    var mask = ToUInt32(unicast.IPv4Mask);
                    var networkAddress = ip & mask;
                    var broadcast = networkAddress | ~mask;
                    var first = mask == uint.MaxValue ? networkAddress : networkAddress + 1;
                    var last = mask == uint.MaxValue ? broadcast : broadcast - 1;
                    var count = last >= first ? (ulong)last - first + 1 : 0;
                    _ranges.Add(new NetworkRangeChoice($"{network.Name} · {unicast.Address}/{PrefixLength(mask)}", unicast.Address.ToString(), networkAddress, broadcast, first, last, count));
                }
            }
        }
        catch (NetworkInformationException) { }
        InterfaceBox.ItemsSource = _ranges;
        InterfaceBox.SelectedIndex = _ranges.Count == 0 ? -1 : 0;
        UpdateRangeLabel();
    }

    private void Interface_Changed(object sender, SelectionChangedEventArgs e)
    {
        _selectedRange = InterfaceBox.SelectedItem as NetworkRangeChoice;
        UpdateRangeLabel();
    }

    private void UpdateRangeLabel()
    {
        if (_selectedRange is null)
        {
            RangeText.Text = "没有发现可扫描的 IPv4 网卡。";
            return;
        }
        RangeText.Text = $"扫描范围：{ToAddress(_selectedRange.First)} – {ToAddress(_selectedRange.Last)} · 共 {_selectedRange.Count:N0} 个地址";
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        await StopAsync();
        if (_selectedRange is null) { StatusText.Text = "请选择一个 IPv4 网卡。"; return; }
        if (_selectedRange.Count > 4096) { StatusText.Text = "网段超过 4096 个地址，请在系统网卡或路由上缩小扫描范围。"; return; }
        if (!int.TryParse(ConcurrencyBox.Text, out var concurrency) || concurrency is < 1 or > 256) { StatusText.Text = "并发数应为 1–256。"; return; }
        Devices.Clear(); _scanned = 0; _online = 0;
        var lifetime = new CancellationTokenSource();
        _lifetime = lifetime;
        StartButton.IsEnabled = false; StopButton.IsEnabled = true; InterfaceBox.IsEnabled = false; ConcurrencyBox.IsEnabled = false;
        StatusText.Text = $"正在扫描 {_selectedRange.Count:N0} 个地址…";
        _worker = ScanAsync(_selectedRange, concurrency, lifetime.Token);
        try { await _worker; }
        finally
        {
            var ownsLifetime = ReferenceEquals(_lifetime, lifetime);
            if (ownsLifetime)
            {
                _lifetime = null;
                var canceled = lifetime.IsCancellationRequested;
                lifetime.Dispose();
                StatusText.Text = canceled ? "已停止" : $"扫描完成，共发现 {_online} 台设备";
            }
            SetIdle();
            _worker = null;
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopAsync();

    private async Task StopAsync()
    {
        var lifetime = _lifetime;
        if (lifetime is null) return;
        _lifetime = null;
        lifetime.Cancel();
        if (_worker is not null)
        {
            try { await _worker; } catch (OperationCanceledException) { }
        }
        lifetime.Dispose();
        _worker = null;
        SetIdle();
        StatusText.Text = "已停止";
    }

    private void SetIdle()
    {
        StartButton.IsEnabled = true; StopButton.IsEnabled = false; InterfaceBox.IsEnabled = true; ConcurrencyBox.IsEnabled = true;
    }

    private async Task ScanAsync(NetworkRangeChoice range, int concurrency, CancellationToken cancellationToken)
    {
        var arp = await LoadArpTableAsync(cancellationToken);
        using var gate = new SemaphoreSlim(concurrency, concurrency);
        var tasks = new List<Task>();
        for (var value = range.First; value <= range.Last; value++)
        {
            var address = ToAddress(value);
            tasks.Add(ProbeOneAsync(address, arp, gate, cancellationToken));
            if (tasks.Count >= concurrency * 4)
            {
                await Task.WhenAll(tasks);
                tasks.Clear();
            }
            if (value == uint.MaxValue) break;
        }
        await Task.WhenAll(tasks);
        await Dispatcher.InvokeAsync(() =>
        {
            var known = Devices.Select(device => device.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in arp)
            {
                if (!IPAddress.TryParse(pair.Key, out var address) || address.AddressFamily != AddressFamily.InterNetwork) continue;
                var value = ToUInt32(address);
                if (value < range.First || value > range.Last || !known.Add(pair.Key)) continue;
                Devices.Add(new NetworkDiscoveryDevice("ARP", pair.Key, "—", pair.Value, "—"));
                _online++;
            }
            SummaryText.Text = $"已扫描 {_scanned:N0} · 在线 {_online:N0}";
        });
    }

    private async Task ProbeOneAsync(IPAddress address, IReadOnlyDictionary<string, string> arp, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            Interlocked.Increment(ref _scanned);
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(600), new byte[16], null, cancellationToken);
            if (reply.Status != IPStatus.Success) return;
            var hostName = string.Empty;
            try { hostName = (await Dns.GetHostEntryAsync(address).WaitAsync(TimeSpan.FromMilliseconds(900), cancellationToken)).HostName; }
            catch (Exception ex) when (ex is SocketException or TimeoutException) { }
            var key = address.ToString();
            var mac = arp.TryGetValue(key, out var value) ? value : "—";
            Interlocked.Increment(ref _online);
            await Dispatcher.InvokeAsync(() =>
            {
                Devices.Add(new NetworkDiscoveryDevice("在线", key, string.IsNullOrWhiteSpace(hostName) ? "—" : hostName, mac, $"{reply.RoundtripTime} ms"));
                SummaryText.Text = $"已扫描 {_scanned:N0} · 在线 {_online:N0}";
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (PingException) { }
        finally { gate.Release(); }
    }

    private static async Task<IReadOnlyDictionary<string, string>> LoadArpTableAsync(CancellationToken cancellationToken)
    {
        var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var info = new ProcessStartInfo { FileName = "arp.exe", Arguments = "-a", UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using var process = Process.Start(info);
            if (process is null) return table;
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            foreach (Match match in Regex.Matches(output, @"(?<ip>\d{1,3}(?:\.\d{1,3}){3})\s+(?<mac>[0-9a-f]{2}(?:[-:][0-9a-f]{2}){5})", RegexOptions.IgnoreCase))
                table[match.Groups["ip"].Value] = match.Groups["mac"].Value;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or OperationCanceledException) { }
        return table;
    }

    private static uint ToUInt32(IPAddress address) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
    private static IPAddress ToAddress(uint value) => new(new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });
    private static int PrefixLength(uint mask) => System.Numerics.BitOperations.PopCount(mask);

    public void Dispose()
    {
        var lifetime = _lifetime;
        _lifetime = null;
        lifetime?.Cancel();
        lifetime?.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed record NetworkRangeChoice(string Name, string LocalAddress, uint Network, uint Broadcast, uint First, uint Last, ulong Count);
}
