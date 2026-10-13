using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using EmbedDesk.Core.Abstractions;
using EmbedDesk.Infrastructure.Networking;

namespace EmbedDesk.App;

public sealed record NetworkProbeSample(string Time, string Outcome, string Latency, string Detail);
public sealed record TracerouteHop(int Hop, string Address, string HostName, string Latency, string Status);

public partial class NetworkProbeToolWindow : Window, IDisposable
{
    private readonly NetworkProbe _probe = new();
    private CancellationTokenSource? _lifetime;
    private bool _closed;
    private int _total;
    private int _failed;
    private int _pingInterfaceIndex;
    private bool _wasTrace;
    private readonly Action<NetworkProbePreferences>? _savePreferences;

    public NetworkProbeToolWindow(NetworkProbeToolMode mode = NetworkProbeToolMode.Icmp,
        NetworkProbePreferences? preferences = null, Action<NetworkProbePreferences>? savePreferences = null)
    {
        _savePreferences = savePreferences;
        InitializeComponent();
        DataContext = this;
        FamilyBox.SelectedIndex = 0;
        PopulateInterfaces();
        if (preferences is not null)
        {
            HostBox.Text = preferences.Host;
            PortBox.Text = preferences.TcpPort.ToString(CultureInfo.InvariantCulture);
            PacketSizeBox.Text = preferences.PacketSize.ToString(CultureInfo.InvariantCulture);
            CountBox.Text = preferences.Count.ToString(CultureInfo.InvariantCulture);
            IntervalBox.Text = preferences.IntervalMilliseconds.ToString(CultureInfo.InvariantCulture);
            MaxHopsBox.Text = preferences.MaxHops.ToString(CultureInfo.InvariantCulture);
            TimeoutBox.Text = preferences.TimeoutMilliseconds.ToString(CultureInfo.InvariantCulture);
            ResolveNamesBox.IsChecked = preferences.ResolveNames;
            FamilyBox.SelectedIndex = (int)preferences.AddressFamily;
            InterfaceBox.SelectedItem = InterfaceBox.Items.Cast<NetworkInterfaceChoice>()
                .FirstOrDefault(item => string.Equals(item.Address, preferences.LocalAddress, StringComparison.Ordinal)) ?? InterfaceBox.Items[0];
            mode = preferences.Mode;
        }
        ModeBox.SelectedIndex = (int)mode;
        WindowTheme.ApplyDarkTitleBar(this);
        Closed += (_, _) =>
        {
            _savePreferences?.Invoke(CapturePreferences());
            Dispose();
        };
    }

    public ObservableCollection<NetworkProbeSample> Samples { get; } = [];
    public ObservableCollection<TracerouteHop> Hops { get; } = [];

    private void PopulateInterfaces()
    {
        var choices = new List<NetworkInterfaceChoice> { new("自动选择", null) };
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces().Where(item => item.OperationalStatus == OperationalStatus.Up))
                foreach (var address in network.GetIPProperties().UnicastAddresses.Select(item => item.Address)
                             .Where(item => item.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6).Distinct())
                    choices.Add(new($"{network.Name} · {address}", address.ToString()));
        }
        catch (NetworkInformationException) { }
        InterfaceBox.ItemsSource = choices;
        InterfaceBox.SelectedIndex = 0;
    }

    private bool IsTrace => ModeBox.SelectedIndex == 2;

    private NetworkProbePreferences CapturePreferences()
    {
        var interfaceIndex = _wasTrace ? _pingInterfaceIndex : InterfaceBox.SelectedIndex;
        var localAddress = interfaceIndex >= 0 && interfaceIndex < InterfaceBox.Items.Count
            ? ((NetworkInterfaceChoice)InterfaceBox.Items[interfaceIndex]).Address ?? string.Empty
            : string.Empty;
        return new NetworkProbePreferences
        {
            Host = HostBox.Text.Trim(),
            Mode = (NetworkProbeToolMode)Math.Clamp(ModeBox.SelectedIndex, 0, 2),
            AddressFamily = (NetworkProbeAddressFamily)Math.Clamp(FamilyBox.SelectedIndex, 0, 2),
            LocalAddress = localAddress,
            TcpPort = int.TryParse(PortBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ? port : 22,
            PacketSize = int.TryParse(PacketSizeBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var packetSize) ? packetSize : 32,
            Count = int.TryParse(CountBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : 0,
            IntervalMilliseconds = int.TryParse(IntervalBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var interval) ? interval : 1000,
            MaxHops = int.TryParse(MaxHopsBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var hops) ? hops : 30,
            TimeoutMilliseconds = int.TryParse(TimeoutBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var timeout) ? timeout : 1000,
            ResolveNames = ResolveNamesBox.IsChecked == true
        };
    }

    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TraceOptions is null) return;
        if (IsTrace && !_wasTrace)
        {
            _pingInterfaceIndex = InterfaceBox.SelectedIndex;
            InterfaceBox.SelectedIndex = 0;
        }
        else if (!IsTrace && _wasTrace) InterfaceBox.SelectedIndex = _pingInterfaceIndex;
        _wasTrace = IsTrace;
        UpdateControls();
        SummaryText.Text = "";
        StatusText.Text = "未开始";
    }

    private void UpdateControls()
    {
        var idle = _lifetime is null;
        StartButton.IsEnabled = idle; StopButton.IsEnabled = !idle;
        HostBox.IsEnabled = ModeBox.IsEnabled = FamilyBox.IsEnabled = idle;
        InterfaceBox.IsEnabled = idle && !IsTrace;
        InterfaceBox.Visibility = IsTrace ? Visibility.Collapsed : Visibility.Visible;
        TraceRouteSource.Visibility = IsTrace ? Visibility.Visible : Visibility.Collapsed;
        PortBox.IsEnabled = idle && ModeBox.SelectedIndex == 1;
        PacketSizeBox.IsEnabled = idle && ModeBox.SelectedIndex != 1;
        CountBox.IsEnabled = IntervalBox.IsEnabled = MaxHopsBox.IsEnabled = TimeoutBox.IsEnabled = ResolveNamesBox.IsEnabled = idle;
        PingOptions.Visibility = PingResults.Visibility = IsTrace ? Visibility.Collapsed : Visibility.Visible;
        TraceOptions.Visibility = TraceResults.Visibility = IsTrace ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_lifetime is not null || _closed) return;
        var host = HostBox.Text.Trim();
        if (host.Length == 0) { StatusText.Text = "请输入主机地址。"; return; }
        var interval = 1000; var count = 0; var packetSize = 32; var maxHops = 30; var timeout = 1000;
        var trace = IsTrace;
        int? port = null;
        if (!trace)
        {
            if (!int.TryParse(IntervalBox.Text, out interval) || interval is < 100 or > 60000) { StatusText.Text = "间隔应为 100–60000 毫秒。"; return; }
            if (!int.TryParse(CountBox.Text, out count) || count is < 0 or > 1_000_000) { StatusText.Text = "循环次数应为 0–1000000。"; return; }
        }
        else
        {
            if (!int.TryParse(MaxHopsBox.Text, out maxHops) || maxHops is < 1 or > 255) { StatusText.Text = "最大跳数应为 1–255。"; return; }
            if (!int.TryParse(TimeoutBox.Text, out timeout) || timeout is < 100 or > 10000) { StatusText.Text = "每跳超时应为 100–10000 毫秒。"; return; }
        }
        if (ModeBox.SelectedIndex == 1)
        {
            if (!int.TryParse(PortBox.Text, out var parsedPort) || parsedPort is < 1 or > 65535) { StatusText.Text = "TCP 端口应为 1–65535。"; return; }
            port = parsedPort;
        }
        else if (!int.TryParse(PacketSizeBox.Text, out packetSize) || packetSize is < 0 or > 65500)
        { StatusText.Text = "包大小应为 0–65500 字节。"; return; }
        var family = FamilyBox.SelectedIndex switch
        {
            1 => NetworkProbeAddressFamily.IPv4,
            2 => NetworkProbeAddressFamily.IPv6,
            _ => NetworkProbeAddressFamily.Any
        };
        var local = (InterfaceBox.SelectedItem as NetworkInterfaceChoice)?.Address;
        using var lifetime = new CancellationTokenSource();
        _lifetime = lifetime;
        UpdateControls();
        SummaryText.Text = "";
        StatusText.Text = $"正在{(trace ? "追踪" : "探测")} {host}…";
        try
        {
            if (trace)
            {
                Hops.Clear();
                await TraceAsync(host, family, packetSize, maxHops, timeout, ResolveNamesBox.IsChecked == true, lifetime.Token);
            }
            else
            {
                Samples.Clear(); _total = _failed = 0;
                await MonitorAsync(new(host, port, family, local, packetSize), interval, count, lifetime.Token);
                StatusText.Text = "已完成";
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { StatusText.Text = "已停止"; }
        catch (Exception ex) when (ex is SocketException or PingException or ArgumentException or TimeoutException)
        { StatusText.Text = $"探测失败：{ex.Message}"; }
        finally { _lifetime = null; UpdateControls(); }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _lifetime?.Cancel();

    private async Task MonitorAsync(NetworkProbeTarget target, int interval, int count, CancellationToken token)
    {
        while (count == 0 || _total < count)
        {
            var result = await _probe.ProbeAsync(target, token);
            token.ThrowIfCancellationRequested();
            _total++;
            if (result.Outcome != NetworkProbeOutcome.Success) _failed++;
            var latency = result.Milliseconds is double ms ? $"{ms:0.0} ms" : "—";
            Samples.Insert(0, new(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                result.Outcome == NetworkProbeOutcome.Success ? "成功" : result.Outcome == NetworkProbeOutcome.NoReply ? "无响应" : "不可用", latency, result.Detail));
            while (Samples.Count > 200) Samples.RemoveAt(Samples.Count - 1);
            SummaryText.Text = $"共 {_total} 次 · 失败 {_failed} 次";
            if (count == 0 || _total < count) await Task.Delay(interval, token);
        }
    }

    private async Task TraceAsync(string host, NetworkProbeAddressFamily family, int packetSize, int maxHops, int timeout, bool resolveNames, CancellationToken token)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, token).WaitAsync(TimeSpan.FromSeconds(5), token);
        var destination = addresses.Where(a => family switch
        {
            NetworkProbeAddressFamily.IPv4 => a.AddressFamily == AddressFamily.InterNetwork,
            NetworkProbeAddressFamily.IPv6 => a.AddressFamily == AddressFamily.InterNetworkV6,
            _ => true
        }).OrderBy(a => a.AddressFamily != AddressFamily.InterNetwork).FirstOrDefault()
            ?? throw new ArgumentException("目标没有符合所选地址族的地址。");
        if (destination.Equals(IPAddress.Any) || destination.Equals(IPAddress.IPv6Any) || destination.IsIPv6Multicast ||
            destination.AddressFamily == AddressFamily.InterNetwork && destination.GetAddressBytes()[0] >= 224)
            throw new ArgumentException("请输入单播目标地址。");
        var names = new Dictionary<IPAddress, string>();
        using var ping = new Ping();
        for (var hop = 1; hop <= maxHops; hop++)
        {
            token.ThrowIfCancellationRequested();
            StatusText.Text = $"正在追踪 {destination} · 第 {hop} 跳";
            var started = Stopwatch.GetTimestamp();
            var reply = await ping.SendPingAsync(destination, TimeSpan.FromMilliseconds(timeout), new byte[packetSize], new PingOptions(hop, false), token);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var address = reply.Status == IPStatus.TimedOut || reply.Address is null ||
                reply.Address.Equals(IPAddress.Any) || reply.Address.Equals(IPAddress.IPv6Any) ? null : reply.Address;
            var name = "—";
            if (resolveNames && address is not null && !names.TryGetValue(address, out name))
            {
                try { name = (await Dns.GetHostEntryAsync(address).WaitAsync(TimeSpan.FromMilliseconds(800), token)).HostName; }
                catch (Exception ex) when (ex is SocketException or TimeoutException) { name = "—"; }
                names[address] = name;
            }
            token.ThrowIfCancellationRequested();
            var state = reply.Status switch
            {
                IPStatus.Success => "到达目标",
                IPStatus.TtlExpired or IPStatus.TimeExceeded => "TTL 到期",
                IPStatus.TimedOut => "超时",
                _ => reply.Status.ToString()
            };
            Hops.Add(new(hop, address?.ToString() ?? "*", name ?? "—", address is null ? "*" : $"{elapsed:0.0} ms", state));
            SummaryText.Text = $"已探测 {hop} 跳";
            if (reply.Status == IPStatus.Success) { StatusText.Text = $"已到达 {destination} · 共 {hop} 跳"; return; }
            if (reply.Status is not (IPStatus.TtlExpired or IPStatus.TimeExceeded or IPStatus.TimedOut))
            { StatusText.Text = $"追踪停止：{state}"; return; }
        }
        StatusText.Text = $"已达最大跳数 {maxHops}，尚未到达目标。";
    }

    public void Dispose()
    {
        _closed = true;
        _lifetime?.Cancel();
        GC.SuppressFinalize(this);
    }

    private sealed record NetworkInterfaceChoice(string Name, string? Address)
    {
        public override string ToString() => Name;
    }
}
