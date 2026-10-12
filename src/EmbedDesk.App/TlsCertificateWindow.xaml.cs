using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Authentication;
using System.Text;
using System.Windows;
using System.Windows.Media;
using EmbedDesk.Infrastructure.Networking;
using EmbedDesk.Infrastructure.Security;
using Microsoft.Win32;

namespace EmbedDesk.App;

public partial class TlsCertificateWindow : Window, IDisposable
{
    private CancellationTokenSource? _lifetime;
    private bool _closed;
    private bool _cryptoBusy;
    private string _generatedLabel = "CERTIFICATE";
    private string _latestInspectedPem = string.Empty;

    public TlsCertificateWindow()
    {
        InitializeComponent();
        AlgorithmBox.ItemsSource = Enum.GetValues<CertificateKeyAlgorithm>().Select(algorithm => new KeyAlgorithmOption(algorithm, algorithm switch
        {
            CertificateKeyAlgorithm.Rsa2048 => "RSA · 2048 bit",
            CertificateKeyAlgorithm.Rsa3072 => "RSA · 3072 bit",
            CertificateKeyAlgorithm.Rsa4096 => "RSA · 4096 bit",
            CertificateKeyAlgorithm.EcdsaP256 => "ECDSA · NIST P-256",
            CertificateKeyAlgorithm.EcdsaP384 => "ECDSA · NIST P-384",
            CertificateKeyAlgorithm.EcdsaP521 => "ECDSA · NIST P-521",
            CertificateKeyAlgorithm.Ed25519 => "Ed25519",
            CertificateKeyAlgorithm.Ed448 => "Ed448",
            CertificateKeyAlgorithm.Sm2 => "SM2 · sm2p256v1",
            _ => algorithm.ToString()
        })).ToArray();
        AlgorithmBox.SelectedValue = CertificateKeyAlgorithm.Rsa2048;
        DigestAlgorithmBox.ItemsSource = CryptoOperations.DigestAlgorithms; DigestAlgorithmBox.SelectedIndex = 0;
        CipherAlgorithmBox.ItemsSource = CryptoOperations.CipherAlgorithms; CipherAlgorithmBox.SelectedIndex = 0;
        AsymmetricAlgorithmBox.ItemsSource = CryptoOperations.SignatureAlgorithms.Concat(CryptoOperations.AsymmetricEncryptionAlgorithms).ToArray();
        AsymmetricAlgorithmBox.SelectedIndex = 0;
        foreach (var box in new[] { DigestFormatBox, AsymmetricFormatBox }) { box.ItemsSource = new[] { "UTF-8", "HEX", "Base64" }; box.SelectedIndex = 0; }
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

    private async void GenerateCertificate_Click(object sender, RoutedEventArgs e)
    {
        var algorithm = GetAlgorithm(); var subject = SubjectBox.Text; var names = GetNames();
        var days = ValidityBox.Text; var ca = CaBox.IsChecked == true;
        var server = ServerBox.IsChecked == true; var client = ClientBox.IsChecked == true; var password = PfxPasswordBox.Password;
        await RunCryptoAsync(() => CertificateCryptoService.CreateSelfSigned(algorithm, subject, names, ParseDays(days), ca, server, client,
            string.IsNullOrEmpty(password) ? null : password), artifact =>
        {
            ShowGenerated(artifact.CertificatePem, artifact.Key, "CERTIFICATE");
        }, "自签名证书已生成；PFX 可使用下方导出按钮保存。");
    }

    private async void GenerateCsr_Click(object sender, RoutedEventArgs e)
    {
        var algorithm = GetAlgorithm(); var subject = SubjectBox.Text; var names = GetNames();
        await RunCryptoAsync(() => CertificateCryptoService.CreateSigningRequest(algorithm, subject, names), artifact =>
            ShowGenerated(artifact.RequestPem, artifact.Key, "CERTIFICATE REQUEST"), "CSR 已生成（SAN 请求；最终用途由签发 CA 决定）。");
    }

    private async void GenerateKey_Click(object sender, RoutedEventArgs e)
    {
        var algorithm = GetAlgorithm();
        await RunCryptoAsync(() => CertificateCryptoService.GenerateKey(algorithm), key =>
        {
            ShowGenerated("", key, "CERTIFICATE"); GeneratedTabs.SelectedIndex = 1;
        }, "已生成密钥；私钥 PEM 为明文。");
    }

    private void ShowGenerated(string pem, CertificateKeyMaterial key, string label)
    {
        CertificateOutputBox.Text = pem; PrivateKeyOutputBox.Text = key.PrivateKeyPem; PublicKeyOutputBox.Text = key.PublicKeyPem;
        _generatedLabel = label; GeneratedTabs.SelectedIndex = 0;
    }

    private async void IssueCertificate_Click(object sender, RoutedEventArgs e)
    {
        var ca = IssuerCertificateBox.Text; var csr = RequestBox.Text; var key = IssuerPrivateKeyBox.Text; var days = IssueValidityBox.Text;
        IssuedCertificateBox.Clear();
        await RunCryptoAsync(() => CertificateCryptoService.IssueCertificate(CertificateCryptoService.ReadPem(csr, "CERTIFICATE REQUEST"),
            CertificateCryptoService.ReadCertificate(ca), CertificateCryptoService.ReadTextOrPath(key), ParseDays(days)), der =>
            IssuedCertificateBox.Text = CertificateCryptoService.ToPem("CERTIFICATE", der), "终端证书已签发。");
    }

    private async void InspectCertificate_Click(object sender, RoutedEventArgs e)
    {
        var input = InspectInputBox.Text;
        InspectOutputBox.Clear(); _latestInspectedPem = "";
        await RunCryptoAsync(() => CertificateCryptoService.InspectCertificate(CertificateCryptoService.ReadCertificate(input)), details =>
        {
            _latestInspectedPem = details.Pem;
            InspectOutputBox.Text = $"主题: {details.Subject}\n颁发者: {details.Issuer}\n序列号: {details.SerialNumber}\n生效 (UTC): {details.NotBefore.UtcDateTime:yyyy-MM-dd HH:mm:ss}\n到期 (UTC): {details.NotAfter.UtcDateTime:yyyy-MM-dd HH:mm:ss}\n签名算法: {details.SignatureAlgorithm}\n公钥: {details.PublicKeyAlgorithm} · {details.PublicKeySize}\nCA: {(details.IsCertificateAuthority ? "是" : "否")}\nSAN: {details.SubjectAlternativeNames}\nKey Usage: {details.KeyUsage}\nExtended Key Usage: {details.ExtendedKeyUsage}\nSHA-256: {details.Sha256}\n\n{details.Pem}";
        }, "解析完成；本地解析未验证证书信任链。");
    }

    private void OpenCertificate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "证书|*.pem;*.cer;*.crt;*.der|所有文件|*.*" };
        if (dialog.ShowDialog(this) == true) { InspectInputBox.Text = dialog.FileName; InspectCertificate_Click(sender, e); }
    }

    private async void ImportPfx_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "PKCS#12|*.pfx;*.p12", Title = "导入证书和私钥（使用 PFX 密码框中的密码）" };
        if (dialog.ShowDialog(this) != true) return;
        var password = PfxPasswordBox.Password; var path = dialog.FileName;
        await RunCryptoAsync(() => CertificateCryptoService.ImportPkcs12(CertificateCryptoService.ReadFile(path), password), result =>
        {
            CertificateOutputBox.Text = result.CertificatePem; PrivateKeyOutputBox.Text = result.PrivateKeyPem;
            PublicKeyOutputBox.Clear(); _generatedLabel = "CERTIFICATE"; GeneratedTabs.SelectedIndex = 0;
        }, "PFX 已导入；证书页包含证书链，私钥页为明文。");
    }

    private async void SavePfx_Click(object sender, RoutedEventArgs e)
    {
        if (_generatedLabel != "CERTIFICATE" || string.IsNullOrWhiteSpace(CertificateOutputBox.Text))
        { StatusText.Text = "请先生成或导入证书及其私钥。"; return; }
        var pem = CertificateOutputBox.Text; var key = PrivateKeyOutputBox.Text; var password = PfxPasswordBox.Password;
        byte[]? bytes = null;
        await RunCryptoAsync(() => CertificateCryptoService.ExportPkcs12(CertificateCryptoService.ReadCertificate(pem), key, password),
            result => bytes = result, "PFX 已准备。");
        if (bytes is null || _closed) return;
        try { await SaveBytesAsync(bytes, "certificate.pfx", "PKCS#12|*.pfx"); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private (string Text, string Label) GeneratedOutput() => GeneratedTabs.SelectedIndex switch
    {
        1 => (PrivateKeyOutputBox.Text, "PRIVATE KEY"), 2 => (PublicKeyOutputBox.Text, "PUBLIC KEY"),
        _ => (CertificateOutputBox.Text, _generatedLabel)
    };
    private void CopyGenerated_Click(object sender, RoutedEventArgs e) => CopyText(GeneratedOutput().Text, "已复制当前输出。");
    private async void SaveGenerated_Click(object sender, RoutedEventArgs e)
    {
        var output = GeneratedOutput(); await SavePemAsync(output.Text, output.Label);
    }
    private void CopyIssued_Click(object sender, RoutedEventArgs e) => CopyText(IssuedCertificateBox.Text, "已复制签发结果。");
    private async void SaveIssued_Click(object sender, RoutedEventArgs e) => await SavePemAsync(IssuedCertificateBox.Text, "CERTIFICATE");
    private void CopyInspected_Click(object sender, RoutedEventArgs e) => CopyText(_latestInspectedPem, "已复制证书 PEM。");
    private async void SaveInspected_Click(object sender, RoutedEventArgs e) => await SavePemAsync(_latestInspectedPem, "CERTIFICATE");

    private async Task SavePemAsync(string pem, string label)
    {
        if (string.IsNullOrWhiteSpace(pem)) { StatusText.Text = "没有可保存的内容。"; return; }
        var dialog = new SaveFileDialog { Title = label.Contains("PRIVATE", StringComparison.Ordinal) ? "保存明文私钥" : "保存输出",
            FileName = "output.pem", Filter = "PEM|*.pem|DER（二进制，证书链仅保存首证书）|*.der" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var bytes = dialog.FilterIndex == 2 ? CertificateCryptoService.ReadPem(pem, label) : Encoding.UTF8.GetBytes(pem);
            try { await File.WriteAllBytesAsync(dialog.FileName, bytes); }
            finally { if (label.Contains("PRIVATE", StringComparison.Ordinal)) CryptographicOperations.ZeroMemory(bytes); }
            if (!_closed) StatusText.Text = "文件已保存。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FormatException) { StatusText.Text = ex.Message; }
    }
    private async Task SaveBytesAsync(byte[] bytes, string filename, string filter)
    {
        var dialog = new SaveFileDialog { FileName = filename, Filter = filter };
        if (dialog.ShowDialog(this) != true) return;
        try { await File.WriteAllBytesAsync(dialog.FileName, bytes); if (!_closed) StatusText.Text = "文件已保存。"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StatusText.Text = ex.Message; }
    }

    private async void Digest_Click(object sender, RoutedEventArgs e)
    {
        var algorithm = (string)DigestAlgorithmBox.SelectedItem; var input = DigestInputBox.Text; var format = DigestFormatBox.SelectedIndex;
        var hmac = HmacBox.IsChecked == true; var key = HmacKeyBox.Text; DigestOutputBox.Clear();
        await RunCryptoAsync(() => CryptoOperations.Digest(algorithm, Decode(input, format), hmac ? Decode(key, 1) : null),
            bytes => DigestOutputBox.Text = Convert.ToHexString(bytes), "摘要 / HMAC 计算完成。");
    }
    private void CopyDigest_Click(object sender, RoutedEventArgs e) => CopyText(DigestOutputBox.Text, "已复制摘要。");
    private async void Encrypt_Click(object sender, RoutedEventArgs e) => await CipherAsync(true);
    private async void Decrypt_Click(object sender, RoutedEventArgs e) => await CipherAsync(false);
    private async Task CipherAsync(bool encrypt)
    {
        var algorithm = (string)CipherAlgorithmBox.SelectedItem; var input = CipherInputBox.Text;
        var key = CipherKeyBox.Text; var iv = CipherIvBox.Text; var aad = CipherAadBox.Text;
        CipherOutputBox.Clear();
        await RunCryptoAsync(() =>
        {
            var keyBytes = Decode(key, 1);
            try { return CryptoOperations.Transform(algorithm, Decode(input, 1), keyBytes, Decode(iv, 1), Decode(aad, 1), encrypt); }
            finally { CryptographicOperations.ZeroMemory(keyBytes); }
        }, bytes => CipherOutputBox.Text = Convert.ToHexString(bytes), encrypt ? "加密完成。" : "解密完成。");
    }
    private void CipherAlgorithm_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CipherIvBox is null || CipherAlgorithmBox.SelectedItem is not string algorithm) return;
        var ecb = algorithm.EndsWith("ECB", StringComparison.Ordinal);
        var aead = algorithm.EndsWith("GCM", StringComparison.Ordinal) || algorithm.EndsWith("CCM", StringComparison.Ordinal) || algorithm == "ChaCha20-Poly1305";
        CipherIvBox.IsEnabled = !ecb; if (ecb) CipherIvBox.Clear();
        CipherAadBox.IsEnabled = aead; if (!aead) CipherAadBox.Clear();
        CipherOutputBox.Clear();
    }
    private void RandomCipher_Click(object sender, RoutedEventArgs e)
    {
        var algorithm = (string)CipherAlgorithmBox.SelectedItem;
        CipherKeyBox.Text = Convert.ToHexString(RandomNumberGenerator.GetBytes(algorithm.StartsWith("SM4", StringComparison.Ordinal) ? 16 : 32));
        CipherIvBox.Text = algorithm.EndsWith("ECB", StringComparison.Ordinal) ? "" : Convert.ToHexString(RandomNumberGenerator.GetBytes(
            algorithm.EndsWith("GCM", StringComparison.Ordinal) || algorithm.EndsWith("CCM", StringComparison.Ordinal) || algorithm == "ChaCha20-Poly1305" ? 12 : 16));
    }
    private void CopyCipher_Click(object sender, RoutedEventArgs e) => CopyText(CipherOutputBox.Text, "已复制结果。");
    private async void Sign_Click(object sender, RoutedEventArgs e) => await AsymmetricAsync("sign");
    private async void Verify_Click(object sender, RoutedEventArgs e) => await AsymmetricAsync("verify");
    private async void AsymmetricEncrypt_Click(object sender, RoutedEventArgs e) => await AsymmetricAsync("encrypt");
    private async void AsymmetricDecrypt_Click(object sender, RoutedEventArgs e) => await AsymmetricAsync("decrypt");
    private async Task AsymmetricAsync(string action)
    {
        var algorithm = (string)AsymmetricAlgorithmBox.SelectedItem; var key = AsymmetricKeyBox.Text;
        var input = AsymmetricInputBox.Text; var format = AsymmetricFormatBox.SelectedIndex; var id = Sm2IdBox.Text;
        var signature = SignatureInputBox.Text; AsymmetricOutputBox.Clear();
        await RunCryptoAsync(() =>
        {
            var bytes = Decode(input, format);
            return action switch
            {
                "sign" => Convert.ToBase64String(CryptoOperations.Sign(bytes, key, algorithm, id)),
                "verify" => CryptoOperations.Verify(bytes, Decode(signature, 2), key, algorithm, id) ? "验签通过" : "验签失败",
                _ => Convert.ToBase64String(CryptoOperations.AsymmetricTransform(algorithm, bytes, key, action == "encrypt"))
            };
        }, result => AsymmetricOutputBox.Text = result, "操作完成；结果见输出区。");
    }
    private void CopyAsymmetric_Click(object sender, RoutedEventArgs e) => CopyText(AsymmetricOutputBox.Text, "已复制结果。");

    private async Task RunCryptoAsync<T>(Func<T> work, Action<T> show, string success)
    {
        if (_cryptoBusy || _closed) return;
        _cryptoBusy = true;
        SetCryptoEnabled(false); StatusText.Text = "正在处理…";
        try
        {
            var result = await Task.Run(work);
            if (!_closed) { show(result); StatusText.Text = success; }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        { if (!_closed) StatusText.Text = "操作失败：" + ex.Message; }
        finally { _cryptoBusy = false; if (!_closed) SetCryptoEnabled(true); }
    }
    private void SetCryptoEnabled(bool enabled)
    {
        foreach (var tab in ToolTabs.Items.Cast<System.Windows.Controls.TabItem>().Skip(1))
            if (tab.Content is UIElement content) content.IsEnabled = enabled;
    }
    private static byte[] Decode(string input, int format)
    {
        if (input.Length > CertificateCryptoService.MaxInputBytes) throw new ArgumentException("输入超过 4 MiB 上限。");
        return format switch
        {
            1 => Convert.FromHexString(string.Concat(input.Where(c => !char.IsWhiteSpace(c)))),
            2 => Convert.FromBase64String(input),
            _ => new UTF8Encoding(false, true).GetBytes(input)
        };
    }
    private CertificateKeyAlgorithm GetAlgorithm() => (CertificateKeyAlgorithm)AlgorithmBox.SelectedValue;
    private string[] GetNames() => SanBox.Text.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static int ParseDays(string value) => int.TryParse(value, out var days) ? days : throw new FormatException("有效期需要填写整数天数。");
    private void CopyText(string text, string message)
    {
        if (string.IsNullOrWhiteSpace(text)) { StatusText.Text = "没有可复制的内容。"; return; }
        try { Clipboard.SetText(text); StatusText.Text = message; }
        catch (System.Runtime.InteropServices.COMException) { StatusText.Text = "剪贴板正被占用，请重试。"; }
    }
    public void Dispose()
    {
        _closed = true; _lifetime?.Cancel();
        // Drop references on close; managed strings cannot be securely wiped in place.
        PrivateKeyOutputBox.Clear(); IssuerPrivateKeyBox.Clear(); AsymmetricKeyBox.Clear(); PfxPasswordBox.Clear();
        CipherKeyBox.Clear(); HmacKeyBox.Clear();
        GC.SuppressFinalize(this);
    }

    private sealed record KeyAlgorithmOption(CertificateKeyAlgorithm Algorithm, string Label);
}
