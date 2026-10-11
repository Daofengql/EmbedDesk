using System.Net;
using System.Windows;
using System.Windows.Controls;
using EmbeddedDebugger.Infrastructure.Networking;

namespace EmbeddedDebugger.App;

public partial class Iperf3Window : Window, IDisposable
{
    private CancellationTokenSource? _operation;
    private bool _closed;
    public Iperf3Window()
    {
        InitializeComponent(); ModeBox.SelectedIndex = 0;
        WindowTheme.ApplyDarkTitleBar(this); Closed += (_, _) => Dispose();
    }
    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (HostBox is null) return;
        var server = ModeBox.SelectedIndex == 1;
        HostLabel.Text = server ? "监听地址（IPv4 / IPv6）" : "远程地址";
        HostBox.Text = server ? "0.0.0.0" : "127.0.0.1";
        SetRunning(false);
    }
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is not null) return;
        if (!int.TryParse(PortBox.Text, out var port) || port is < 1 or > 65535) { StatusText.Text = "端口应为 1–65535。"; return; }
        var server = ModeBox.SelectedIndex == 1; var host = HostBox.Text.Trim();
        if (string.IsNullOrEmpty(host)) { StatusText.Text = server ? "请输入监听 IP 地址。" : "请输入远程地址。"; return; }
        IPAddress? address = null;
        if (server && !IPAddress.TryParse(host, out address)) { StatusText.Text = "监听地址必须为 IPv4 或 IPv6 地址，0.0.0.0 / :: 表示所有接口。"; return; }
        var seconds = 10;
        if (!server && (!int.TryParse(DurationBox.Text, out seconds) || seconds is < 1 or > 3600)) { StatusText.Text = "时长应为 1–3600 秒。"; return; }
        using var operation = new CancellationTokenSource(); _operation = operation; SetRunning(true); OutputBox.Clear();
        var reverse = ReverseBox.IsChecked == true;
        StatusText.Text = server ? $"正在监听 {host}:{port}" : $"正在连接 {host}:{port}（{(reverse ? "反向 / 下载" : "正向 / 上传")}）";
        try
        {
            var progress = new Progress<Iperf3Progress>(p => { if (ReferenceEquals(_operation, operation)) Append($"{p.Seconds - p.IntervalSeconds,6:F2}–{p.Seconds:F2}s  {(p.Sender ? "发送" : "接收")}  {p.IntervalBytes / 1048576d,10:F2} MiB  {Rate(p.BitsPerSecond)}"); });
            if (server) await Iperf3Client.RunServerAsync(address!, port, progress,
                new Progress<string>(message => { if (ReferenceEquals(_operation, operation)) Append(message); }),
                new Progress<Iperf3Result>(result => { if (ReferenceEquals(_operation, operation)) Report(result); }), operation.Token);
            else { Report(await Iperf3Client.RunAsync(host, port, seconds, reverse, progress, operation.Token)); if (!_closed) StatusText.Text = "测试完成。"; }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { if (!_closed) StatusText.Text = "已停止。"; }
        catch (Exception ex) { if (!_closed) { StatusText.Text = "测试失败：" + ex.Message; Append(StatusText.Text); } }
        finally { _operation = null; if (!_closed) SetRunning(false); }
    }
    private void Report(Iperf3Result result)
    {
        Append($"本地{(result.LocalSender ? "发送" : "接收")}：{result.LocalSeconds:F2}s · {result.LocalBytes:N0} bytes · {Rate(result.LocalBitsPerSecond)}");
        Append($"对端{(result.LocalSender ? "接收" : "发送")}：{result.PeerSeconds:F2}s · {result.PeerBytes:N0} bytes · {Rate(result.PeerBitsPerSecond)}");
    }
    private void Append(string message)
    {
        if (_closed) return;
        if (OutputBox.Text.Length > 120000) OutputBox.Text = OutputBox.Text[^60000..];
        OutputBox.AppendText(message + Environment.NewLine); OutputBox.ScrollToEnd();
    }
    private static string Rate(double bits) => bits >= 1e9 ? $"{bits / 1e9:F2} Gbit/s" : bits >= 1e6 ? $"{bits / 1e6:F2} Mbit/s" : $"{bits / 1e3:F2} Kbit/s";
    private void Stop_Click(object sender, RoutedEventArgs e) { _operation?.Cancel(); StopButton.IsEnabled = false; }
    private void SetRunning(bool running)
    {
        if (StartButton is null) return;
        StartButton.IsEnabled = ModeBox.IsEnabled = HostBox.IsEnabled = PortBox.IsEnabled = !running;
        StopButton.IsEnabled = running; DurationBox.IsEnabled = ReverseBox.IsEnabled = !running && ModeBox.SelectedIndex == 0;
    }
    public void Dispose() { _closed = true; _operation?.Cancel(); GC.SuppressFinalize(this); }
}
