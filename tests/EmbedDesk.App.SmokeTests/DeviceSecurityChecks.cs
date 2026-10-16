using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using EmbedDesk.Infrastructure.Connections;
namespace EmbedDesk.App.SmokeTests;
internal static class DeviceSecurityChecks
{
    public static async Task RunAsync()
    {
        await RelayAdmissionChecks.RunAsync();
        var directory = Path.Combine(Path.GetTempPath(), "EmbedDesk-Tls-Test", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        using var rootKey = RSA.Create(2048); var rootRequest = new CertificateRequest("CN=EmbedDesk test root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true)); rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var leafKey = RSA.Create(2048); var leafRequest = new CertificateRequest("CN=localhost", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); leafRequest.CertificateExtensions.Add(names.Build());
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        using var leaf = leafRequest.Create(root, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1), RandomNumberGenerator.GetBytes(16));
        var rootPath = Path.Combine(directory, "root.pem");
        await File.WriteAllTextAsync(rootPath, root.ExportCertificatePem());
        using var generated = leaf.CopyWithPrivateKey(leafKey);
        // Schannel needs an imported certificate/key rather than an ephemeral CopyWithPrivateKey handle.
        using var tlsCertificate = new X509Certificate2(generated.Export(X509ContentType.Pfx));
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        var start = await DeviceConnectionsChecks.RelayStartAsync(directory, port, 2000000);
        using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await using var tls = new TlsTerminator(port, tlsCertificate);
        await using var proxy = new TunnelProxy(tls.Port);
        try
        {
            await Task.Delay(300); var url = "https://localhost:" + tls.Port;
            var trustedHealth = await RelayHealthProbe.CheckAsync(Options(url, rootPath, proxy.Url));
            var untrustedHealth = await RelayHealthProbe.CheckAsync(Options(url, "", ""));
            var mismatchedHealth = await RelayHealthProbe.CheckAsync(Options("https://127.0.0.1:" + tls.Port, rootPath, ""));
            DeviceConnectionsChecks.Require(trustedHealth.Available && !untrustedHealth.Available && !mismatchedHealth.Available,
                "relay list latency probes honor the channel's proxy, custom TLS trust and hostname validation");
            await using var untrusted = new DeviceNode(new FakeSerialService()); await untrusted.StartAsync(Options(url, "", ""));
            await DeviceConnectionsChecks.UntilAsync(() => untrusted.Status.StartsWith("中继连接失败", StringComparison.Ordinal));
            DeviceConnectionsChecks.Require(!untrusted.IsOnline, "untrusted TLS certificate is refused"); await untrusted.StopAsync();
            await using var mismatch = new DeviceNode(new FakeSerialService()); await mismatch.StartAsync(Options("https://127.0.0.1:" + tls.Port, rootPath, ""));
            await DeviceConnectionsChecks.UntilAsync(() => mismatch.Status.StartsWith("中继连接失败", StringComparison.Ordinal));
            DeviceConnectionsChecks.Require(!mismatch.IsOnline, "custom trust retains TLS hostname validation"); await mismatch.StopAsync();
            await using var owner = new DeviceNode(new FakeSerialService()); await using var visitor = new DeviceNode(new FakeSerialService());
            await owner.StartAsync(Options(url, rootPath, proxy.Url)); await visitor.StartAsync(Options(url, rootPath, proxy.Url));
            try { await DeviceConnectionsChecks.UntilAsync(() => owner.IsOnline && visitor.IsOnline); }
            catch { Console.WriteLine("TLS OWNER " + owner.Status); Console.WriteLine("TLS VISITOR " + visitor.Status); Console.WriteLine("PROXY CONNECTS " + proxy.Connections); throw; }
            await visitor.ConnectPeerAsync(owner.DeviceId, "test-pair-code");
            DeviceConnectionsChecks.Require(proxy.Connections >= 4 && visitor.Peers.Single().State == "已连接", "registration and WebSocket both use the HTTP CONNECT proxy with custom TLS trust");
            DeviceConnectionsChecks.Require(visitor.Status.Contains("2000 kbit/s", StringComparison.Ordinal), "client displays configured relay bandwidth through TLS reverse proxy");
            await visitor.StopAsync(); await owner.StopAsync();
            Console.WriteLine("PASS HTTP relay behind TLS reverse proxy and HTTP CONNECT proxy checks");
        }
        finally { if (!process.HasExited) process.Kill(); await process.WaitForExitAsync(); await Task.WhenAll(stdout, stderr); Directory.Delete(directory, true); }
    }
    private static DeviceNodeOptions Options(string url, string root, string proxy) => new() { ServerUrl = url, TrustedRootFile = root, ProxyMode = proxy.Length == 0 ? "Direct" : "Custom", ProxyUrl = proxy, Password = "test-pair-code", Credential = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), PreferDirect = false };
    // Test fixture for the nginx boundary: terminate TLS, forward HTTP/WS bytes to the HTTP-only Go relay.
    private sealed class TlsTerminator : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _life = new();
        private readonly ConcurrentDictionary<int, Task> _tasks = new();
        private readonly int _target;
        private readonly X509Certificate2 _certificate;
        private readonly Task _accept;
        private int _sequence;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public TlsTerminator(int target, X509Certificate2 certificate)
        { _target = target; _certificate = certificate; _listener.Start(); _accept = AcceptAsync(); }
        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync(_life.Token); var id = Interlocked.Increment(ref _sequence);
                    var task = PipeAsync(client); _tasks[id] = task;
                    _ = task.ContinueWith(_ => _tasks.TryRemove(id, out var ignored), TaskScheduler.Default);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
        private async Task PipeAsync(TcpClient client)
        {
            using var owned = client; using var target = new TcpClient(); using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
            using var secure = new SslStream(client.GetStream(), false); deadline.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await secure.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, deadline.Token);
                await target.ConnectAsync(IPAddress.Loopback, _target, deadline.Token); deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                var outgoing = secure.CopyToAsync(target.GetStream(), deadline.Token); var incoming = target.GetStream().CopyToAsync(secure, deadline.Token);
                await Task.WhenAny(outgoing, incoming); deadline.Cancel(); client.Close(); target.Close();
                try { await Task.WhenAll(outgoing, incoming); } catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or AuthenticationException) { }
        }
        public async ValueTask DisposeAsync()
        { _life.Cancel(); _listener.Stop(); await _accept; await Task.WhenAll(_tasks.Values); _life.Dispose(); }
    }
    private sealed class TunnelProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0); private readonly CancellationTokenSource _life = new();
        private readonly ConcurrentDictionary<int, Task> _tasks = new(); private readonly Task _accept; private int _sequence;
        private readonly int _target;
        public int Connections => _sequence;
        public string Url => "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;
        public TunnelProxy(int target) { _target = target; _listener.Start(); _accept = AcceptAsync(); }
        private async Task AcceptAsync()
        {
            try { while (true) { var client = await _listener.AcceptTcpClientAsync(_life.Token); var id = Interlocked.Increment(ref _sequence); var task = PipeAsync(client); _tasks[id] = task; _ = task.ContinueWith(_ => _tasks.TryRemove(id, out var ignored), TaskScheduler.Default); } }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        }
        private async Task PipeAsync(TcpClient client)
        {
            using var owned = client; using var target = new TcpClient(); using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_life.Token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var input = client.GetStream(); using var header = new MemoryStream(); var b = new byte[1];
                while (header.Length < 16384) { await input.ReadExactlyAsync(b, timeout.Token); header.WriteByte(b[0]); if (header.Length >= 4 && Encoding.ASCII.GetString(header.ToArray()).EndsWith("\r\n\r\n", StringComparison.Ordinal)) break; }
                if (!Encoding.ASCII.GetString(header.ToArray()).StartsWith("CONNECT localhost:" + _target + " ", StringComparison.Ordinal)) { Console.WriteLine("Proxy unexpected method: " + Encoding.ASCII.GetString(header.ToArray()).Split("\r\n")[0]); return; }
                await target.ConnectAsync(IPAddress.Loopback, _target, timeout.Token); await input.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), timeout.Token);
                timeout.CancelAfter(Timeout.InfiniteTimeSpan); var outgoing = input.CopyToAsync(target.GetStream(), timeout.Token); var incoming = target.GetStream().CopyToAsync(input, timeout.Token);
                await Task.WhenAny(outgoing, incoming); timeout.Cancel(); client.Close(); target.Close(); try { await Task.WhenAll(outgoing, incoming); } catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException) { }
        }
        public async ValueTask DisposeAsync() { _life.Cancel(); _listener.Stop(); await _accept; await Task.WhenAll(_tasks.Values); _life.Dispose(); }
    }
}
