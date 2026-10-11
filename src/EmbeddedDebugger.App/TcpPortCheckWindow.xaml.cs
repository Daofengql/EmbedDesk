using System.Collections.ObjectModel;
using System.Net.Sockets;
using System.Windows;
using EmbeddedDebugger.Infrastructure.Networking;

namespace EmbeddedDebugger.App;

public partial class TcpPortCheckWindow : Window, IDisposable
{
    private CancellationTokenSource? _lifetime;
    private bool _closed;
    private int _scannedCount;
    public ObservableCollection<PortCheckResult> Results { get; } = [];

    public TcpPortCheckWindow()
    {
        InitializeComponent(); DataContext = this;
        WindowTheme.ApplyDarkTitleBar(this); Closed += (_, _) => Dispose();
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_lifetime is not null || _closed) return;
        using var lifetime = new CancellationTokenSource(); _lifetime = lifetime;
        try
        {
            var ports = TcpPortChecker.ParsePorts(PortsBox.Text);
            if (!int.TryParse(TimeoutBox.Text, out var timeout) || !int.TryParse(ConcurrencyBox.Text, out var concurrency)) throw new FormatException("超时和并发连接数需要填写整数。");
            var family = FamilyBox.SelectedIndex switch { 1 => AddressFamily.InterNetwork, 2 => AddressFamily.InterNetworkV6, _ => AddressFamily.Unspecified };
            Results.Clear(); _scannedCount = 0; CopyButton.IsEnabled = false; SummaryText.Text = "正在解析 / 扫描…";
            ParametersPanel.IsEnabled = StartButton.IsEnabled = false; StopButton.IsEnabled = true;
            StatusText.Text = "正在扫描，可随时停止。";
            var progress = new Progress<PortCheckResult>(result =>
            {
                if (_closed || lifetime != _lifetime) return;
                _scannedCount++;
                if (result.Status == "开放") Results.Add(result);
                UpdateSummary();
            });
            var results = await TcpPortChecker.CheckAsync(HostBox.Text, ports, timeout, concurrency, family, progress, lifetime.Token);
            if (_closed) return;
            // Replace with the ordered result after completion, including any queued progress callbacks.
            _lifetime = null;
            Results.Clear();
            foreach (var result in results.Where(result => result.Status == "开放")) Results.Add(result);
            _scannedCount = results.Count;
            UpdateSummary(); StatusText.Text = "扫描完成 · 耗时为 TCP 建立连接的时间，不代表应用层服务可用性。";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { if (!_closed) StatusText.Text = "已停止，保留已完成的结果。"; }
        catch (Exception ex) when (ex is ArgumentException or FormatException or SocketException or TimeoutException)
        { if (!_closed) StatusText.Text = ex.Message; }
        finally
        {
            _lifetime = null;
            if (!_closed) { ParametersPanel.IsEnabled = StartButton.IsEnabled = true; StopButton.IsEnabled = false; CopyButton.IsEnabled = Results.Count > 0; }
        }
    }

    private void UpdateSummary() => SummaryText.Text = $"已扫描 {_scannedCount} 项 · 开放 {Results.Count} 项";
    private void Stop_Click(object sender, RoutedEventArgs e) => _lifetime?.Cancel();
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText("地址\t端口\t状态\t耗时/ms\t详情\n" + string.Join("\n", Results.Select(r => $"{r.Address}\t{r.Port}\t{r.Status}\t{r.ElapsedMilliseconds:F1}\t{r.Detail}")));
            StatusText.Text = "已复制结果。";
        }
        catch (System.Runtime.InteropServices.COMException) { StatusText.Text = "剪贴板正被占用，请重试。"; }
    }
    public void Dispose() { _closed = true; _lifetime?.Cancel(); GC.SuppressFinalize(this); }
}
