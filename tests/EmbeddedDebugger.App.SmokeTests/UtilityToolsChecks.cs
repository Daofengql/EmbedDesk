using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EmbeddedDebugger.App.Controls;
using EmbeddedDebugger.App.ViewModels;
using EmbeddedDebugger.Infrastructure.Diagnostics;
using EmbeddedDebugger.Infrastructure.Networking;

namespace EmbeddedDebugger.App.SmokeTests;

internal static class UtilityToolsChecks
{
    internal static async Task RunAsync(MainWindowViewModel vm)
    {
        var values = ByteUtilities.Checksums(Encoding.ASCII.GetBytes("123456789"));
        Require(values[0].Value == "4B37" && values[0].LittleEndian == "37 4B" && values[1].Value == "29B1" && values[2].Value == "CBF43926", "Published CRC check vectors and MODBUS low-byte-first order");
        Require(values[3].Value == "DD" && values[4].Value == "31" && values[6].Value == "25F9E794323B453885F5181F1B624D0B", "SUM-8, XOR-8 and MD5 known vectors");
        Require(ByteUtilities.Checksums([])[2].Value == "00000000", "Empty CRC-32 has the canonical zero result");
        Require(ByteUtilities.Decode("0x01, 0x03:AB-CD\r\nEF", ByteFormat.Hex).SequenceEqual(new byte[] { 1, 3, 171, 205, 239 }), "HEX accepts common byte separators and 0x prefixes");
        foreach (var bad in new[] { "0", "GG", "01 Z2", "0x" }) Throws<FormatException>(() => ByteUtilities.Decode(bad, ByteFormat.Hex), "Invalid HEX rejected: " + bad);
        var utf8 = ByteUtilities.Decode("Hello, 你好", ByteFormat.Utf8);
        Require(ByteUtilities.Encode(ByteUtilities.Decode(ByteUtilities.Encode(utf8, ByteFormat.Base64), ByteFormat.Base64), ByteFormat.Utf8) == "Hello, 你好", "UTF-8/Base64 round trip preserves multilingual data");
        Require(ByteUtilities.Decode("A", ByteFormat.Utf16Le).SequenceEqual(new byte[] { 65, 0 }), "UTF-16 LE emits little-endian bytes without BOM");
        Throws<DecoderFallbackException>(() => ByteUtilities.Encode([0xC0, 0xAF], ByteFormat.Utf8), "Overlong UTF-8 is rejected instead of silently replaced");
        Throws<EncoderFallbackException>(() => ByteUtilities.Decode("中", ByteFormat.Ascii), "ASCII rejects unrepresentable characters");
        Throws<FormatException>(() => ByteUtilities.Decode("bad!", ByteFormat.Base64), "Invalid Base64 rejected");
        Require(TcpPortChecker.ParsePorts("22,80,79-81,22").SequenceEqual(new[] { 22, 79, 80, 81 }), "Port lists deduplicate and expand inclusive ranges");
        foreach (var ports in new[] { "0", "65536", "80-79", "1-1025", "" }) Throws<FormatException>(() => TcpPortChecker.ParsePorts(ports), "Invalid or oversized port list rejected: " + ports);

        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var openPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var closedListener = new TcpListener(IPAddress.Loopback, 0); closedListener.Start();
        var closedPort = ((IPEndPoint)closedListener.LocalEndpoint).Port; closedListener.Stop();
        var portsResult = await TcpPortChecker.CheckAsync("127.0.0.1", [openPort, closedPort], 5000, 2);
        foreach (var result in portsResult) Console.WriteLine($"PORT {result.Port}: {result.Status} {result.ElapsedMilliseconds:F1} ms {result.Detail}");
        Require(portsResult.Single(r => r.Port == openPort).Status == "开放" && portsResult.Single(r => r.Port == closedPort).Status == "拒绝", "Real loopback TCP checks distinguish listening and refused endpoints");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => TcpPortChecker.CheckAsync("127.0.0.1", [openPort], 1000, 1, token: cancel.Token), "Port detection honors cancellation");
        }

        await using var tls = new TlsFixture();
        var cert = await TlsCertificateInspector.InspectAsync("127.0.0.1", tls.Port, "localhost", 5000, false);
        Require(!cert.IsTrusted && cert.ValidationErrors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors) && !cert.ValidationErrors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch), "Inspection shows a self-signed certificate as untrusted while matching localhost SAN");
        Require(cert.AlternativeNames.Contains("localhost") && cert.Sha256.Length == 64 && cert.Pem.Contains("BEGIN CERTIFICATE"), "TLS report retains SAN, fingerprint and PEM");
        var mismatch = await TlsCertificateInspector.InspectAsync("127.0.0.1", tls.Port, "wrong.example", 5000, false);
        Require(mismatch.ValidationErrors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch), "TLS name mismatches remain visible");
        await ThrowsAsync<HttpRequestException>(() => new DnsLookupClient().QueryAsync(new("example.test", 1, new(IPAddress.Loopback, 53))
            { HttpsEndpoint = new($"https://localhost:{tls.Port}/dns-query"), TimeoutMilliseconds = 3000, Retries = 0 }), "Production DoH refuses an untrusted HTTPS certificate");
        await ThrowsAsync<TimeoutException>(() => TlsCertificateInspector.InspectAsync("127.0.0.1", openPort, "localhost", 100, false), "TLS handshake on a silent TCP server times out");
        using (var cancel = new CancellationTokenSource(50))
            await ThrowsAsync<OperationCanceledException>(() => TlsCertificateInspector.InspectAsync("127.0.0.1", openPort, "localhost", 3000, false, cancel.Token), "TLS handshake is cancelable");

        await CheckWindowsAsync(vm, openPort, closedPort, tls.Port);
    }

    private static async Task CheckWindowsAsync(MainWindowViewModel vm, int openPort, int closedPort, int tlsPort)
    {
        foreach (var checksum in new[] { true, false })
        {
            var window = new ByteToolWindow(checksum); window.Show();
            try
            {
                Click(window, "RunButton"); await Until(() => ((Panel)window.FindName("ParametersPanel")).IsEnabled);
                var output = ((TextBox)window.FindName("OutputBox")).Text;
                Require(output.Contains(checksum ? "4B37" : "E4 BD A0"), "Byte utility UI runs the selected calculation");
                window.Width = window.MinWidth; window.Height = window.MinHeight;
                await FileWorkspaceChecks.CaptureAsync(window, checksum ? "embedded-debugger-checksum.png" : "embedded-debugger-codec.png");
                ((ComboBox)window.FindName("InputFormatBox")).SelectedIndex = 0;
                ((TextBox)window.FindName("InputBox")).Text = "ZZ"; Click(window, "RunButton");
                await Until(() => ((Panel)window.FindName("ParametersPanel")).IsEnabled);
                Require(((TextBlock)window.FindName("StatusText")).Text.StartsWith("输入无效", StringComparison.Ordinal), "Byte utility UI reports invalid input and stays usable");
            }
            finally { window.Close(); }
        }
        var ports = new TcpPortCheckWindow(); ports.Show();
        try
        {
            ((TextBox)ports.FindName("PortsBox")).Text = $"{openPort},{closedPort}";
            ((TextBox)ports.FindName("TimeoutBox")).Text = "5000";
            Click(ports, "StartButton"); await Until(() => ((Button)ports.FindName("StartButton")).IsEnabled);
            Require(ports.Results.Count == 1 && ports.Results.Single().Port == openPort && ports.Results.Single().Status == "开放",
                "Port window displays only open endpoints without duplicate progress callbacks");
            ports.Width = ports.MinWidth; ports.Height = ports.MinHeight;
            await FileWorkspaceChecks.CaptureAsync(ports, "embedded-debugger-ports.png");
        }
        finally { ports.Close(); }
        var tls = new TlsCertificateWindow(); tls.Show();
        try
        {
            ((TextBox)tls.FindName("HostBox")).Text = "127.0.0.1";
            ((TextBox)tls.FindName("PortBox")).Text = tlsPort.ToString();
            ((TextBox)tls.FindName("SniBox")).Text = "localhost";
            Click(tls, "StartButton"); await Until(() => ((Button)tls.FindName("StartButton")).IsEnabled);
            Require(((TextBlock)tls.FindName("TrustText")).Text.StartsWith("证书校验未通过", StringComparison.Ordinal) && ((TextBox)tls.FindName("PemBox")).Text.Contains("BEGIN CERTIFICATE"), "Certificate window shows validation failure and still makes the certificate inspectable");
            tls.Width = tls.MinWidth; tls.Height = tls.MinHeight;
            await FileWorkspaceChecks.CaptureAsync(tls, "embedded-debugger-tls.png");
            ((TextBox)tls.FindName("PortBox")).Text = openPort.ToString();
            Click(tls, "StartButton"); Click(tls, "StopButton");
            await Until(() => ((Button)tls.FindName("StartButton")).IsEnabled);
            Require(((TextBlock)tls.FindName("TrustText")).Text == "已停止", "TLS Stop button restores idle controls");
            Click(tls, "StartButton"); tls.Close();
            await Task.Delay(80);
        }
        finally { if (tls.IsVisible) tls.Close(); }

        await vm.InitializeAsync();
        vm.SelectedSerialSession!.SelectedSerialPort!.BaudRate = 2000000;
        vm.SelectedSerialSession.SelectedSerialPort.Handshake = System.IO.Ports.Handshake.RequestToSendXOnXOff;
        var main = new MainWindow(vm); main.Show();
        try
        {
            await FileWorkspaceChecks.CaptureAsync(main, "embedded-debugger-serial-spacing.png");
            var serial = Descendants<SerialWorkspace>(main).First(w => w.IsVisible);
            var selector = (ComboBox)serial.FindName("PortSelector"); var width = selector.ActualWidth;
            main.Width = main.MinWidth; main.Height = main.MinHeight;
            await FileWorkspaceChecks.CaptureAsync(main, "embedded-debugger-serial-spacing-small.png");
            Require(Math.Abs(width - selector.ActualWidth) < .01 && width == 260, "Serial device field retains its fixed 260 px width after window resizing");
            var baud = (ComboBox)serial.FindName("BaudSelector");
            var editor = (TextBox)baud.Template.FindName("PART_EditableTextBox", baud);
            var lastDigit = editor.GetRectFromCharacterIndex(editor.Text.Length - 1, true);
            Require(!lastDigit.IsEmpty && lastDigit.Right <= editor.ActualWidth - editor.Padding.Right + 1,
                "Seven-digit custom baud rate fits completely before the dropdown arrow");
            var tabs = Descendants<TabControl>(main).First(); tabs.SelectedIndex = 3;
            main.Width = 1340; main.Height = 940;
            await FileWorkspaceChecks.CaptureAsync(main, "embedded-debugger-tools-expanded.png");
            main.Width = main.MinWidth; main.Height = main.MinHeight;
            await FileWorkspaceChecks.CaptureAsync(main, "embedded-debugger-tools-expanded-small.png");
            var gallery = Descendants<NetworkToolsWorkspace>(main).Single();
            var buttons = Descendants<Button>(gallery).Where(b => b.Tag is string).ToArray();
            Require(buttons.Length == 9, "Network gallery contains only network tools");
            foreach (var button in buttons)
            {
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var dialog = main.OwnedWindows.Cast<Window>().Single(w => w.IsVisible);
                Require(dialog.Title.Length > 0, "Gallery opens " + button.Tag);
                dialog.Close();
            }
            tabs.SelectedIndex = 4;
            await Task.Delay(40);
            main.UpdateLayout();
            var embedded = Descendants<EmbeddedToolsWorkspace>(main).Single();
            var embeddedButtons = Descendants<Button>(embedded).Where(b => b.Tag is string).ToArray();
            Require(embeddedButtons.Length == 8, "Embedded gallery owns ESP, firmware, media, adapter and data tools");
        }
        finally { main.Close(); }
    }

    private static void Click(Window window, string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Until(Func<bool> ready)
    { var deadline = DateTime.UtcNow.AddSeconds(12); while (!ready()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException(); await Task.Delay(20); } }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T typed) yield return typed; foreach (var item in Descendants<T>(child)) yield return item; } }
    private static void Require(bool pass, string message) { if (!pass) throw new InvalidOperationException(message); Console.WriteLine("PASS " + message); }
    private static void Throws<T>(Action action, string message) where T : Exception
    { try { action(); } catch (T) { Console.WriteLine("PASS " + message); return; } throw new InvalidOperationException(message); }
    private static async Task ThrowsAsync<T>(Func<Task> action, string message) where T : Exception
    { try { await action(); } catch (T) { Console.WriteLine("PASS " + message); return; } throw new InvalidOperationException(message); }

    private sealed class TlsFixture : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly X509Certificate2 _certificate;
        private readonly Task _loop;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public TlsFixture()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost");
            request.CertificateExtensions.Add(san.Build());
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(14));
            _certificate = new X509Certificate2(generated.Export(X509ContentType.Pfx));
            _listener.Start(); _loop = RunAsync();
        }
        private async Task RunAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    using var stream = new SslStream(client.GetStream());
                    try
                    {
                        await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate }, _stop.Token);
                        var buffer = new byte[1]; await stream.ReadAsync(buffer, _stop.Token);
                    }
                    catch (Exception ex) when (ex is AuthenticationException or IOException) { }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync()
        { _stop.Cancel(); _listener.Stop(); await _loop; _certificate.Dispose(); _stop.Dispose(); }
    }
}
