using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace EmbeddedDebugger.Infrastructure.Networking;

public sealed record TlsCertificateReport(string Host, int Port, string ServerName, string Protocol, string Cipher,
    double ElapsedMilliseconds, SslPolicyErrors ValidationErrors, string ChainStatus, bool RevocationChecked,
    string Subject, string Issuer, DateTimeOffset NotBefore, DateTimeOffset NotAfter,
    string AlternativeNames, string SerialNumber, string Sha256, string Chain, string Pem)
{
    public bool IsTrusted => ValidationErrors == SslPolicyErrors.None;
}

public static class TlsCertificateInspector
{
    public static async Task<TlsCertificateReport> InspectAsync(string host, int port, string serverName,
        int timeoutMilliseconds, bool checkRevocation, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(serverName) || port is < 1 or > 65535 || timeoutMilliseconds is < 100 or > 60000)
            throw new ArgumentException("请输入主机、有效的端口和 SNI 名称；超时为 100–60000 ms。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeoutMilliseconds);
        var start = Stopwatch.GetTimestamp();
        byte[]? certificate = null;
        var validation = SslPolicyErrors.RemoteCertificateNotAvailable;
        var chainStatus = ""; var chainText = "";
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host.Trim(), port, deadline.Token).ConfigureAwait(false);
            using var tls = new SslStream(client.GetStream(), false, (_, cert, chain, errors) =>
            {
                validation = errors;
                certificate = cert?.Export(X509ContentType.Cert);
                chainStatus = chain is null ? "没有证书链" : string.Join("\n", chain.ChainStatus.Select(s => $"{s.Status}: {s.StatusInformation.Trim()}"));
                chainText = chain is null ? "" : string.Join("\n\n", chain.ChainElements.Cast<X509ChainElement>().Select((element, i) =>
                    $"[{i + 1}] {element.Certificate.Subject}\n    Issuer: {element.Certificate.Issuer}\n    {string.Join("; ", element.ChainElementStatus.Select(s => s.Status.ToString()))}"));
                // Inspection only: capture invalid certificates too. No application data or credentials are sent.
                return cert is not null;
            });
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = serverName.Trim(), EnabledSslProtocols = SslProtocols.None,
                CertificateRevocationCheckMode = checkRevocation ? X509RevocationMode.Online : X509RevocationMode.NoCheck
            }, deadline.Token).ConfigureAwait(false);
            if (certificate is null) throw new AuthenticationException("服务器未提供证书。");
            using var leaf = new X509Certificate2(certificate);
            var sans = leaf.Extensions.Cast<X509Extension>().FirstOrDefault(ext => ext.Oid?.Value == "2.5.29.17")?.Format(true) ?? "无 SAN 扩展";
            string cipher;
            try { cipher = tls.NegotiatedCipherSuite.ToString(); }
            catch (PlatformNotSupportedException) { cipher = $"{tls.CipherAlgorithm} / {tls.CipherStrength} bit · {tls.HashAlgorithm}"; }
            return new(host.Trim(), port, serverName.Trim(), tls.SslProtocol.ToString(), cipher,
                Stopwatch.GetElapsedTime(start).TotalMilliseconds, validation,
                string.IsNullOrEmpty(chainStatus) ? "证书链无错误" : chainStatus, checkRevocation,
                leaf.Subject, leaf.Issuer, leaf.NotBefore, leaf.NotAfter, sans, leaf.SerialNumber,
                Convert.ToHexString(SHA256.HashData(certificate)), chainText, leaf.ExportCertificatePem());
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("TLS 连接 / 握手超时。");
        }
    }
}
