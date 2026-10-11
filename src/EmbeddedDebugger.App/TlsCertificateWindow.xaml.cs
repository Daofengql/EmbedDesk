using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Authentication;
using System.Text;
using System.Windows;
using System.Windows.Media;
using EmbeddedDebugger.Infrastructure.Networking;
using EmbeddedDebugger.Infrastructure.Security;
using Microsoft.Win32;

namespace EmbeddedDebugger.App;

public partial class TlsCertificateWindow : Window, IDisposable
{
    private CancellationTokenSource? _lifetime;
    private bool _closed;
    private CertificateArtifact? _latestCertificate;
    private string _latestInspectedPem = string.Empty;

    public TlsCertificateWindow()
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        Closed += (_, _) => Dispose();
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_lifetime is not null || _closed) return;
        using var lifetime = new CancellationTokenSource();
        _lifetime = lifetime;
        try
        {
            if (!int.TryParse(PortBox.Text, out var port) || !int.TryParse(TimeoutBox.Text, out var timeout)) throw new FormatException("端口和超时需要填写整数。");
            ParametersPanel.IsEnabled = StartButton.IsEnabled = CopyButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            TrustText.Text = "正在连接 / 握手…";
            TrustText.Foreground = (Brush)FindResource("TextBrush");
            DetailsBox.Clear();
            PemBox.Clear();
            SummaryText.Text = "等待服务器证书…";
            var report = await TlsCertificateInspector.InspectAsync(HostBox.Text, port,
                string.IsNullOrWhiteSpace(SniBox.Text) ? HostBox.Text : SniBox.Text, timeout, RevocationBox.IsChecked == true, lifetime.Token);
            if (_closed) return;
            TrustText.Text = report.IsTrusted ? "证书链与主机名校验通过" : "证书校验未通过 · " + report.ValidationErrors;
            TrustText.Foreground = new SolidColorBrush(report.IsTrusted ? Color.FromRgb(80, 200, 163) : Color.FromRgb(247, 157, 107));
            var remaining = (report.NotAfter - DateTimeOffset.UtcNow).TotalDays;
            var validity = remaining >= 0 ? $"剩余 {Math.Floor(remaining)} 天" : $"已过期 {Math.Ceiling(-remaining)} 天";
            SummaryText.Text = $"{report.Protocol} · {report.Cipher} · {report.ElapsedMilliseconds:F1} ms\n到期 {report.NotAfter.LocalDateTime:yyyy-MM-dd HH:mm:ss} · {validity}";
            DetailsBox.Text = $"目标: {report.Host}:{report.Port}\nSNI / 校验名称: {report.ServerName}\nTLS: {report.Protocol}\n密码套件: {report.Cipher}\n\n验证结果: {report.ValidationErrors}\n吊销检查: {(report.RevocationChecked ? "已启用" : "未检查")}\n{report.ChainStatus}\n\n主题: {report.Subject}\n颁发者: {report.Issuer}\n生效 (UTC): {report.NotBefore.UtcDateTime:yyyy-MM-dd HH:mm:ss}\n到期 (UTC): {report.NotAfter.UtcDateTime:yyyy-MM-dd HH:mm:ss}\n序列号: {report.SerialNumber}\nSHA-256: {report.Sha256}\n\nSAN:\n{report.AlternativeNames}\n\n证书链:\n{report.Chain}";
            PemBox.Text = report.Pem;
            CopyButton.IsEnabled = true;
            TlsStatusText.Text = "检查完成 · " + (report.RevocationChecked ? "已请求在线吊销检查，详见验证结果。" : "本次未检查吊销状态，可勾选后再次检查。");
            StatusText.Text = "TLS 检查完成。";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            if (!_closed) { TrustText.Text = "已停止"; TlsStatusText.Text = "检查已取消。"; }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or SocketException or TimeoutException or AuthenticationException or System.IO.IOException or System.Security.Cryptography.CryptographicException)
        {
            if (!_closed) { TrustText.Text = "检查失败"; TlsStatusText.Text = ex.Message; StatusText.Text = "TLS 检查失败。"; }
        }
        finally
        {
            _lifetime = null;
            if (!_closed) { ParametersPanel.IsEnabled = StartButton.IsEnabled = true; StopButton.IsEnabled = false; }
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _lifetime?.Cancel();

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyText(DetailsBox.Text + "\n\n" + PemBox.Text, "已复制检查结果和 PEM 证书。");

    private void GenerateCertificate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(ValidityBox.Text, out var days)) throw new FormatException("有效期需要填写整数天数。\n");
            var artifact = CertificateCryptoService.CreateSelfSigned(GetAlgorithm(), SubjectBox.Text, GetNames(), days,
                CaBox.IsChecked == true, ServerBox.IsChecked == true, ClientBox.IsChecked == true,
                string.IsNullOrEmpty(PfxPasswordBox.Password) ? null : PfxPasswordBox.Password);
            _latestCertificate = artifact;
            CertificateOutputBox.Text = artifact.CertificatePem;
            PrivateKeyOutputBox.Text = artifact.Key.PrivateKeyPem;
            PublicKeyOutputBox.Text = artifact.Key.PublicKeyPem;
            PfxOutputBox.Text = artifact.Pkcs12 is null ? "当前算法没有生成 PKCS#12。SM2 PFX 需使用支持国密私钥容器的目标系统。" : Convert.ToBase64String(artifact.Pkcs12);
            StatusText.Text = $"已生成 {artifact.Algorithm} 自签名证书；私钥仅显示在当前窗口。";
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or CryptographicException)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void GenerateCsr_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var artifact = CertificateCryptoService.CreateSigningRequest(GetAlgorithm(), SubjectBox.Text, GetNames());
            CertificateOutputBox.Text = artifact.RequestPem;
            PrivateKeyOutputBox.Text = artifact.Key.PrivateKeyPem;
            PublicKeyOutputBox.Text = artifact.Key.PublicKeyPem;
            PfxOutputBox.Text = "CSR 不包含 PKCS#12。请安全保存私钥，并将 CSR 提交给 CA。";
            StatusText.Text = $"已生成 {artifact.Algorithm} CSR；私钥仅显示在当前窗口。";
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or CryptographicException)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void IssueCertificate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(IssueValidityBox.Text, out var days)) throw new FormatException("有效期需要填写整数天数。\n");
            var issuer = CertificateCryptoService.ReadCertificate(IssuerCertificateBox.Text);
            var request = CertificateCryptoService.ReadPem(RequestBox.Text, "CERTIFICATE REQUEST");
            var privateKey = ReadTextOrPath(IssuerPrivateKeyBox.Text);
            var der = CertificateCryptoService.IssueCertificate(request, issuer, privateKey, days);
            IssuedCertificateBox.Text = CertificateCryptoService.ToPem("CERTIFICATE", der);
            IssueStatusText.Text = "签发完成。请用解析页检查主题、SAN、有效期和签名算法。";
            StatusText.Text = "已签发证书。";
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or CryptographicException or InvalidOperationException or NotSupportedException)
        {
            IssueStatusText.Text = ex.Message;
            StatusText.Text = "证书签发失败。";
        }
    }

    private void CopyIssued_Click(object sender, RoutedEventArgs e) => CopyText(IssuedCertificateBox.Text, "已复制签发结果。");

    private void InspectCertificate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var der = CertificateCryptoService.ReadCertificate(InspectInputBox.Text);
            var details = CertificateCryptoService.InspectCertificate(der);
            _latestInspectedPem = details.Pem;
            InspectOutputBox.Text = $"主题: {details.Subject}\n颁发者: {details.Issuer}\n序列号: {details.SerialNumber}\n生效 (UTC): {details.NotBefore.UtcDateTime:yyyy-MM-dd HH:mm:ss}\n到期 (UTC): {details.NotAfter.UtcDateTime:yyyy-MM-dd HH:mm:ss}\n签名算法: {details.SignatureAlgorithm}\n公钥: {details.PublicKeyAlgorithm} · {details.PublicKeySize}\nCA: {(details.IsCertificateAuthority ? "是" : "否")}\nSAN: {details.SubjectAlternativeNames}\nKey Usage: {details.KeyUsage}\nExtended Key Usage: {details.ExtendedKeyUsage}\nSHA-256: {details.Sha256}\n\n{details.Pem}";
            StatusText.Text = "证书解析完成。";
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or CryptographicException or IOException)
        {
            InspectOutputBox.Text = ex.Message;
            StatusText.Text = "证书解析失败。";
        }
    }

    private void CopyInspected_Click(object sender, RoutedEventArgs e) => CopyText(_latestInspectedPem, "已复制证书 PEM。");

    private void Sm3_Click(object sender, RoutedEventArgs e)
    {
        try { Sm3OutputBox.Text = CertificateCryptoService.ComputeSm3Hex(Encoding.UTF8.GetBytes(Sm3InputBox.Text)); StatusText.Text = "SM3 计算完成。"; }
        catch (Exception ex) { Sm3OutputBox.Text = ex.Message; }
    }

    private void Sm4Encrypt_Click(object sender, RoutedEventArgs e) => RunSm4(true);
    private void Sm4Decrypt_Click(object sender, RoutedEventArgs e) => RunSm4(false);

    private void RunSm4(bool encrypt)
    {
        try
        {
            var input = ParseHex(Sm4InputBox.Text, "输入");
            var key = ParseHex(Sm4KeyBox.Text, "密钥");
            var iv = ParseHex(Sm4IvBox.Text, "IV");
            Sm4OutputBox.Text = Convert.ToHexString(CertificateCryptoService.Sm4Cbc(input, key, iv, encrypt));
            StatusText.Text = encrypt ? "SM4 加密完成。" : "SM4 解密完成。";
        }
        catch (Exception ex)
        {
            Sm4OutputBox.Text = ex.Message;
            StatusText.Text = "SM4 操作失败。";
        }
    }

    private CertificateKeyAlgorithm GetAlgorithm() => (AlgorithmBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() switch
    {
        nameof(CertificateKeyAlgorithm.EcdsaP256) => CertificateKeyAlgorithm.EcdsaP256,
        nameof(CertificateKeyAlgorithm.Sm2) => CertificateKeyAlgorithm.Sm2,
        _ => CertificateKeyAlgorithm.Rsa2048
    };

    private string[] GetNames() => SanBox.Text.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static byte[] ParseHex(string value, string name)
    {
        var compact = new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (compact.Length == 0) throw new FormatException($"{name}不能为空。");
        try { return Convert.FromHexString(compact); }
        catch (FormatException) { throw new FormatException($"{name}需要填写偶数长度的十六进制字符串。"); }
    }

    private static string ReadTextOrPath(string value) => File.Exists(value.Trim()) ? File.ReadAllText(value.Trim()) : value.Trim();

    private void CopyText(string text, string message)
    {
        if (string.IsNullOrWhiteSpace(text)) { StatusText.Text = "没有可复制的内容。"; return; }
        try { Clipboard.SetText(text); StatusText.Text = message; }
        catch (System.Runtime.InteropServices.COMException) { StatusText.Text = "剪贴板正被占用，请重试。"; }
    }

    public void Dispose()
    {
        _closed = true;
        _lifetime?.Cancel();
        GC.SuppressFinalize(this);
    }
}
