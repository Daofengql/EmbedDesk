using System.Net.Sockets;
using System.Security.Authentication;
using System.Windows;
using System.Windows.Media;
using EmbeddedDebugger.Infrastructure.Networking;

namespace EmbeddedDebugger.App;

public partial class TlsCertificateWindow : Window, IDisposable
{
    private CancellationTokenSource? _lifetime;
    private bool _closed;
    public TlsCertificateWindow()
    {
        InitializeComponent(); WindowTheme.ApplyDarkTitleBar(this); Closed += (_, _) => Dispose();
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_lifetime is not null || _closed) return;
        using var lifetime = new CancellationTokenSource(); _lifetime = lifetime;
        try
        {
            if (!int.TryParse(PortBox.Text, out var port) || !int.TryParse(TimeoutBox.Text, out var timeout)) throw new FormatException("端口和超时需要填写整数。");
            ParametersPanel.IsEnabled = StartButton.IsEnabled = CopyButton.IsEnabled = false;
            StopButton.IsEnabled = true; TrustText.Text = "正在连接 / 握手…";
            TrustText.Foreground = (Brush)FindResource("TextBrush");
            DetailsBox.Clear(); PemBox.Clear(); SummaryText.Text = "等待服务器证书…";
            var report = await TlsCertificateInspector.InspectAsync(HostBox.Text, port,
                string.IsNullOrWhiteSpace(SniBox.Text) ? HostBox.Text : SniBox.Text, timeout, RevocationBox.IsChecked == true, lifetime.Token);
            if (_closed) return;
            TrustText.Text = report.IsTrusted ? "证书链与主机名校验通过" : "证书校验未通过 · " + report.ValidationErrors;
            TrustText.Foreground = new SolidColorBrush(report.IsTrusted ? Color.FromRgb(80, 200, 163) : Color.FromRgb(247, 157, 107));
            var remaining = (report.NotAfter - DateTimeOffset.UtcNow).TotalDays;
            var validity = remaining >= 0 ? $"剩余 {Math.Floor(remaining)} 天" : $"已过期 {Math.Ceiling(-remaining)} 天";
            SummaryText.Text = $"{report.Protocol} · {report.Cipher} · {report.ElapsedMilliseconds:F1} ms\n到期 {report.NotAfter.LocalDateTime:yyyy-MM-dd HH:mm:ss} · {validity}";
            DetailsBox.Text = $"目标: {report.Host}:{report.Port}\nSNI / 校验名称: {report.ServerName}\nTLS: {report.Protocol}\n密码套件: {report.Cipher}\n\n验证结果: {report.ValidationErrors}\n吊销检查: {(report.RevocationChecked ? "已启用" : "未检查")}\n{report.ChainStatus}\n\n主题: {report.Subject}\n颁发者: {report.Issuer}\n生效 (UTC): {report.NotBefore.UtcDateTime:yyyy-MM-dd HH:mm:ss}\n到期 (UTC): {report.NotAfter.UtcDateTime:yyyy-MM-dd HH:mm:ss}\n序列号: {report.SerialNumber}\nSHA-256: {report.Sha256}\n\nSAN:\n{report.AlternativeNames}\n\n证书链:\n{report.Chain}";
            PemBox.Text = report.Pem; CopyButton.IsEnabled = true;
            StatusText.Text = "检查完成 · " + (report.RevocationChecked ? "已请求在线吊销检查，详见验证结果。" : "本次未检查吊销状态，可勾选后再次检查。");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { if (!_closed) { TrustText.Text = "已停止"; StatusText.Text = "检查已取消。"; } }
        catch (Exception ex) when (ex is ArgumentException or FormatException or SocketException or TimeoutException or AuthenticationException or System.IO.IOException or System.Security.Cryptography.CryptographicException)
        { if (!_closed) { TrustText.Text = "检查失败"; StatusText.Text = ex.Message; } }
        finally { _lifetime = null; if (!_closed) { ParametersPanel.IsEnabled = StartButton.IsEnabled = true; StopButton.IsEnabled = false; } }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _lifetime?.Cancel();
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(DetailsBox.Text + "\n\n" + PemBox.Text); StatusText.Text = "已复制检查结果和 PEM 证书。"; }
        catch (System.Runtime.InteropServices.COMException) { StatusText.Text = "剪贴板正被占用，请重试。"; }
    }
    public void Dispose() { _closed = true; _lifetime?.Cancel(); GC.SuppressFinalize(this); }
}
