using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Input;

namespace EmbeddedDebugger.App;

public sealed record TracerouteHop(int Hop, string Address, string HostName, string Latency, string Status);

public partial class TracerouteWindow : Window, IDisposable
{
    private CancellationTokenSource? _lifetime;
    private Task? _worker;

    public TracerouteWindow()
    {
        InitializeComponent();
        DataContext = this;
        WindowTheme.ApplyDarkTitleBar(this);
        Closed += (_, _) => Dispose();
    }

    public ObservableCollection<TracerouteHop> Hops { get; } = [];

    private async void Start_Click(object sender, RoutedEventArgs e) => await StartAsync();
    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopAsync();
    private async void Target_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await StartAsync();
    }

    private async Task StartAsync()
    {
        await StopAsync();
        if (!int.TryParse(MaxHopsBox.Text, out var maxHops) || maxHops is < 1 or > 64)
        {
            StatusText.Text = "最大跳数应为 1–64。";
            return;
        }
        if (!int.TryParse(TimeoutBox.Text, out var timeout) || timeout is < 100 or > 10000)
        {
            StatusText.Text = "每跳超时应为 100–10000 毫秒。";
            return;
        }
        var target = TargetBox.Text.Trim();
        if (target.Length == 0)
        {
            StatusText.Text = "请输入目标主机或 IP 地址。";
            return;
        }
        Hops.Clear();
        var lifetime = new CancellationTokenSource();
        _lifetime = lifetime;
        SetBusy(true);
        StatusText.Text = $"正在追踪 {target}…";
        _worker = TraceAsync(target, maxHops, timeout, lifetime.Token);
        try { await _worker; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { StatusText.Text = "已停止"; }
        catch (Exception ex) when (ex is SocketException or PingException or ArgumentException)
        {
            StatusText.Text = $"追踪失败：{ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_lifetime, lifetime)) _lifetime = null;
            lifetime.Dispose();
            _worker = null;
            SetBusy(false);
        }
    }

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
        SetBusy(false);
        StatusText.Text = "已停止";
    }

    private async Task TraceAsync(string target, int maxHops, int timeout, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(target, cancellationToken);
        var destination = addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault()
            ?? throw new SocketException((int)SocketError.HostNotFound);
        for (var hop = 1; hop <= maxHops; hop++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var started = Stopwatch.GetTimestamp();
            IPAddress? address = null;
            IPStatus status;
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(destination, TimeSpan.FromMilliseconds(timeout), new byte[32], new PingOptions(hop, true), cancellationToken);
                status = reply.Status;
                address = reply.Address;
            }
            catch (PingException)
            {
                status = IPStatus.Unknown;
            }
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var addressText = address?.ToString() ?? "*";
            var hostName = address is null ? "—" : await ResolveNameAsync(address, cancellationToken);
            var latency = address is null ? "*" : $"{elapsed:0.0} ms";
            var state = status switch
            {
                IPStatus.Success => "到达目标",
                IPStatus.TtlExpired => "TTL 到期",
                IPStatus.TimedOut => "超时",
                _ => status.ToString()
            };
            await Dispatcher.InvokeAsync(() => Hops.Add(new TracerouteHop(hop, addressText, hostName, latency, state)));
            if (status == IPStatus.Success) { StatusText.Text = $"追踪完成，共 {hop} 跳"; break; }
        }
    }

    private static async Task<string> ResolveNameAsync(IPAddress address, CancellationToken cancellationToken)
    {
        try
        {
            var entry = await Dns.GetHostEntryAsync(address.ToString()).WaitAsync(TimeSpan.FromMilliseconds(800), cancellationToken);
            return string.IsNullOrWhiteSpace(entry.HostName) ? "—" : entry.HostName;
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return "—";
        }
    }

    private void SetBusy(bool busy)
    {
        StartButton.IsEnabled = !busy;
        StopButton.IsEnabled = busy;
        TargetBox.IsEnabled = !busy;
        MaxHopsBox.IsEnabled = !busy;
        TimeoutBox.IsEnabled = !busy;
    }

    public void Dispose()
    {
        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = null;
        GC.SuppressFinalize(this);
    }
}
