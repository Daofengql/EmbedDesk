using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using EmbeddedDebugger.Core.Abstractions;
using EmbeddedDebugger.Infrastructure.Networking;

namespace EmbeddedDebugger.App;

public enum NetworkProbeToolMode { Icmp, Tcping }

public sealed record NetworkProbeSample(string Time, string Outcome, string Latency, string Detail);

public partial class NetworkProbeToolWindow : Window, IDisposable
{
    private readonly NetworkProbe _probe = new();
    private CancellationTokenSource? _lifetime;
    private Task? _worker;
    private int _total;
    private int _failed;

    public NetworkProbeToolWindow(NetworkProbeToolMode mode = NetworkProbeToolMode.Icmp)
    {
        InitializeComponent();
        DataContext = this;
        ModeBox.SelectedIndex = mode == NetworkProbeToolMode.Tcping ? 1 : 0;
        FamilyBox.SelectedIndex = 0;
        PopulateInterfaces();
        WindowTheme.ApplyDarkTitleBar(this);
        Closed += (_, _) => Dispose();
    }

    public ObservableCollection<NetworkProbeSample> Samples { get; } = [];

    private void PopulateInterfaces()
    {
        var choices = new List<NetworkInterfaceChoice> { new("自动选择", null) };
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces().Where(item => item.OperationalStatus == OperationalStatus.Up))
            {
                foreach (var address in network.GetIPProperties().UnicastAddresses.Select(item => item.Address)
                             .Where(item => item.AddressFamily is System.Net.Sockets.AddressFamily.InterNetwork or System.Net.Sockets.AddressFamily.InterNetworkV6)
                             .Distinct())
                    choices.Add(new($"{network.Name} · {address}", address.ToString()));
            }
        }
        catch (NetworkInformationException) { }
        InterfaceBox.ItemsSource = choices;
        InterfaceBox.SelectedIndex = 0;
    }

    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (PortBox is null) return;
        var tcp = ModeBox.SelectedIndex == 1;
        PortBox.IsEnabled = tcp;
        PacketSizeBox.IsEnabled = !tcp;
        FamilyBox.IsEnabled = true;
        InterfaceBox.IsEnabled = true;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        await StopAsync();
        var host = HostBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(host)) { StatusText.Text = "请输入主机地址。"; return; }
        if (!int.TryParse(IntervalBox.Text, out var interval) || interval is < 100 or > 60000) { StatusText.Text = "间隔应为 100–60000 毫秒。"; return; }
        if (!int.TryParse(CountBox.Text, out var count) || count is < 0 or > 1_000_000) { StatusText.Text = "循环次数应为 0–1000000，0 表示持续运行。"; return; }
        if (!int.TryParse(PacketSizeBox.Text, out var packetSize) || packetSize is < 0 or > 65500) { StatusText.Text = "包大小应为 0–65500 字节。"; return; }
        int? port = null;
        if (ModeBox.SelectedIndex == 1)
        {
            if (!int.TryParse(PortBox.Text, out var parsedPort) || parsedPort is < 1 or > 65535) { StatusText.Text = "TCP 端口应为 1–65535。"; return; }
            port = parsedPort;
        }
        var family = FamilyBox.SelectedIndex switch
        {
            1 => NetworkProbeAddressFamily.IPv4,
            2 => NetworkProbeAddressFamily.IPv6,
            _ => NetworkProbeAddressFamily.Any
        };
        var localAddress = (InterfaceBox.SelectedItem as NetworkInterfaceChoice)?.Address;
        Samples.Clear(); _total = 0; _failed = 0;
        var lifetime = new CancellationTokenSource();
        _lifetime = lifetime;
        StartButton.IsEnabled = false; StopButton.IsEnabled = true; HostBox.IsEnabled = false; ModeBox.IsEnabled = false;
        FamilyBox.IsEnabled = false; InterfaceBox.IsEnabled = false; CountBox.IsEnabled = false; IntervalBox.IsEnabled = false; PortBox.IsEnabled = false; PacketSizeBox.IsEnabled = false;
        StatusText.Text = $"正在探测 {host}{(port.HasValue ? $":{port}" : string.Empty)}";
        var target = new NetworkProbeTarget(host, port, family, localAddress, packetSize);
        _worker = MonitorAsync(target, interval, count, lifetime.Token);
        try { await _worker; }
        finally
        {
            var completed = ReferenceEquals(_lifetime, lifetime);
            if (completed)
            {
                _lifetime = null;
                var canceled = lifetime.IsCancellationRequested;
                lifetime.Dispose();
                StatusText.Text = canceled ? "已停止" : count == 0 ? "已停止" : "已完成";
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
        StartButton.IsEnabled = true; StopButton.IsEnabled = false; HostBox.IsEnabled = true; ModeBox.IsEnabled = true;
        FamilyBox.IsEnabled = true; InterfaceBox.IsEnabled = true; CountBox.IsEnabled = true; IntervalBox.IsEnabled = true;
        Mode_Changed(this, null!);
    }

    private async Task MonitorAsync(NetworkProbeTarget target, int interval, int count, CancellationToken cancellationToken)
    {
        try
        {
            while (count == 0 || _total < count)
            {
                var result = await _probe.ProbeAsync(target, cancellationToken);
                _total++;
                if (result.Outcome != NetworkProbeOutcome.Success) _failed++;
                await Dispatcher.InvokeAsync(() =>
                {
                    var latency = result.Milliseconds is double ms ? $"{ms:0.0} ms" : "—";
                    Samples.Insert(0, new NetworkProbeSample(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), result.Outcome == NetworkProbeOutcome.Success ? "成功" : result.Outcome == NetworkProbeOutcome.NoReply ? "无响应" : "不可用", latency, result.Detail));
                    while (Samples.Count > 200) Samples.RemoveAt(Samples.Count - 1);
                    SummaryText.Text = $"共 {_total} 次 · 失败 {_failed} 次";
                });
                if (count == 0 || _total < count) await Task.Delay(interval, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        var lifetime = _lifetime;
        _lifetime = null;
        lifetime?.Cancel();
        lifetime?.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed record NetworkInterfaceChoice(string Name, string? Address);
}
