using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace EmbedDesk.Infrastructure.Connections;

public sealed record RelayHealthResult(bool Available, long Milliseconds, string Error);

/// <summary>Small HTTP health/latency request using the same transport policy as the relay channel.</summary>
public static class RelayHealthProbe
{
    public static async Task<RelayHealthResult> CheckAsync(DeviceNodeOptions options, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var handler = RelayHttpTransport.CreateHandler(options);
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(3),
                MaxResponseContentBufferSize = 4096
            };
            var address = new Uri(new Uri(options.ServerUrl.TrimEnd('/') + "/"), "health");
            using var response = await client.GetAsync(address, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new(false, watch.ElapsedMilliseconds, $"HTTP {(int)response.StatusCode}");
            using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
            var compatible = json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String && status.GetString() == "ok"
                && json.RootElement.TryGetProperty("protocol", out var protocol) && protocol.TryGetInt32(out var version) && version == 1;
            return new(compatible, watch.ElapsedMilliseconds, compatible ? string.Empty : "不是兼容的 EmbedDesk 中继");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(false, watch.ElapsedMilliseconds, "连接超时"); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ArgumentException or JsonException or CryptographicException or InvalidOperationException or UnauthorizedAccessException)
        { return new(false, watch.ElapsedMilliseconds, ex.Message); }
    }
}

internal static class RelayHttpTransport
{
    internal static IWebProxy? CreateProxy(DeviceNodeOptions options)
    {
        if (options.ProxyMode == "System") return HttpClient.DefaultProxy;
        if (options.ProxyMode == "Direct") return null;
        if (!Uri.TryCreate(options.ProxyUrl, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.Scheme is not ("http" or "https" or "socks5"))
            throw new ArgumentException("代理地址须为 http、https 或 socks5 URL。");
        return new WebProxy(uri) { Credentials = string.IsNullOrEmpty(options.ProxyUser) ? null : new NetworkCredential(options.ProxyUser, options.ProxyPassword) };
    }

    internal static HttpClientHandler CreateHandler(DeviceNodeOptions options) => new()
    {
        Proxy = CreateProxy(options),
        UseProxy = options.ProxyMode != "Direct",
        AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = (_, cert, _, errors) => ValidateCertificate(options, cert, errors)
    };

    internal static bool ValidateCertificate(DeviceNodeOptions options, X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (certificate is null || string.IsNullOrEmpty(options.TrustedRootFile) || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0) return false;
        using var root = X509Certificate2.CreateFromPem(File.ReadAllText(options.TrustedRootFile));
        using var custom = new X509Chain();
        custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        custom.ChainPolicy.CustomTrustStore.Add(root);
        custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        using var target = new X509Certificate2(certificate);
        return custom.Build(target);
    }
}
