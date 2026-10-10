using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Input;

namespace EmbeddedDebugger.App;

public sealed record DnsQueryResult(string Value, string Type, string HostName, string Latency, string Status);

public partial class DnsQueryWindow : Window
{
    private CancellationTokenSource? _queryLifetime;

    public DnsQueryWindow()
    {
        InitializeComponent();
        DataContext = this;
        WindowTheme.ApplyDarkTitleBar(this);
    }

    public ObservableCollection<DnsQueryResult> Results { get; } = [];

    private void Mode_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (QueryBox is null) return;
        QueryBox.Text = ModeBox.SelectedIndex == 1 ? "192.168.1.1" : "example.com";
        QueryBox.SelectAll();
    }

    private async void Query_Click(object sender, RoutedEventArgs e) => await QueryAsync();
    private async void Query_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await QueryAsync();
    }

    private async Task QueryAsync()
    {
        _queryLifetime?.Cancel();
        _queryLifetime?.Dispose();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        _queryLifetime = lifetime;
        Results.Clear();
        var query = QueryBox.Text.Trim();
        if (query.Length == 0)
        {
            StatusText.Text = "请输入域名或 IP 地址。";
            return;
        }
        QueryBox.IsEnabled = false;
        ModeBox.IsEnabled = false;
        QueryButton.IsEnabled = false;
        StatusText.Text = "正在查询…";
        try
        {
            if (ModeBox.SelectedIndex == 1) await ReverseAsync(query, lifetime.Token);
            else await ForwardAsync(query, lifetime.Token);
            StatusText.Text = Results.Count == 0 ? "查询完成，没有返回结果。" : $"查询完成，共 {Results.Count} 条结果";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            StatusText.Text = "查询已停止或超时。";
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException or FormatException)
        {
            StatusText.Text = $"查询失败：{ex.Message}";
            Results.Add(new DnsQueryResult(query, "—", "—", "—", ex.Message));
        }
        finally
        {
            if (ReferenceEquals(_queryLifetime, lifetime)) _queryLifetime = null;
            QueryBox.IsEnabled = true;
            ModeBox.IsEnabled = true;
            QueryButton.IsEnabled = true;
        }
    }

    private async Task ForwardAsync(string query, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var addresses = await Dns.GetHostAddressesAsync(query, cancellationToken);
        var latency = $"{Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.0} ms";
        if (addresses.Length == 0) return;
        foreach (var address in addresses.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            Results.Add(new DnsQueryResult(address.ToString(), address.AddressFamily == AddressFamily.InterNetwork ? "A" : "AAAA", query, latency, "成功"));
        }
    }

    private async Task ReverseAsync(string query, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(query, out var address)) throw new FormatException("反向查询需要输入有效的 IP 地址。");
        var started = Stopwatch.GetTimestamp();
        var entry = await Dns.GetHostEntryAsync(address.ToString()).WaitAsync(cancellationToken);
        var latency = $"{Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.0} ms";
        var names = entry.Aliases.Append(entry.HostName).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length == 0) names = ["—"];
        foreach (var name in names)
            Results.Add(new DnsQueryResult(query, address.AddressFamily == AddressFamily.InterNetwork ? "PTR / IPv4" : "PTR / IPv6", name, latency, "成功"));
    }
}
