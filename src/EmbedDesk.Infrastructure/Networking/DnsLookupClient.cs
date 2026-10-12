using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Net.Http.Headers;

namespace EmbedDesk.Infrastructure.Networking;

public sealed record DnsLookupOptions(string Query, ushort RecordType, IPEndPoint Server)
{
    public ushort QueryClass { get; init; } = 1;
    public bool UseTcp { get; init; }
    public Uri? HttpsEndpoint { get; init; }
    public bool RecursionDesired { get; init; } = true;
    public int TimeoutMilliseconds { get; init; } = 2000;
    public int Retries { get; init; } = 1;
}

public sealed record DnsResourceRecord(string Section, string Name, string Type, string Class, uint Ttl, string Value);

public sealed record DnsLookupResponse(string Question, string Type, string Class, string Server, string Transport,
    double ElapsedMilliseconds, int Attempts, int ResponseCode, string Flags, int MessageBytes, IReadOnlyList<DnsResourceRecord> Records)
{
    public string Status => ResponseCode switch
    {
        0 => "NOERROR · 查询成功", 1 => "FORMERR · 请求格式错误", 2 => "SERVFAIL · 服务器失败",
        3 => "NXDOMAIN · 域名不存在", 4 => "NOTIMP · 不支持此查询", 5 => "REFUSED · 拒绝查询",
        _ => $"RCODE {ResponseCode}"
    };
}

/// <summary>DNS wire-format queries over UDP, TCP or HTTPS, without external executables.</summary>
public sealed class DnsLookupClient
{
    private static readonly HttpClient SharedHttpClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxResponseHeadersLength = 16
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _httpClient;

    public DnsLookupClient() : this(SharedHttpClient) { }
    public DnsLookupClient(HttpClient httpClient) => _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public static IReadOnlyList<string> RecordTypes { get; } =
        ["A", "AAAA", "CNAME", "MX", "TXT", "NS", "SOA", "PTR", "SRV", "CAA", "DNAME", "NAPTR", "ANY"];

    public static ushort ParseType(string value)
    {
        var text = value.Trim().ToUpperInvariant();
        return text switch
        {
            "A" => 1, "NS" => 2, "CNAME" => 5, "SOA" => 6, "PTR" => 12, "MX" => 15,
            "TXT" => 16, "AAAA" => 28, "SRV" => 33, "NAPTR" => 35, "DNAME" => 39, "ANY" => 255, "CAA" => 257,
            _ when text.StartsWith("TYPE", StringComparison.Ordinal) && ushort.TryParse(text.AsSpan(4), NumberStyles.None,
                CultureInfo.InvariantCulture, out var number) && number is > 0 and not (251 or 252) => number,
            _ => throw new FormatException("记录类型无效；可选常用类型或输入 TYPE数字（不支持 AXFR / IXFR 区域传送）。")
        };
    }

    internal static string TypeName(ushort value) => value switch
    {
        1 => "A", 2 => "NS", 5 => "CNAME", 6 => "SOA", 12 => "PTR", 15 => "MX", 16 => "TXT",
        28 => "AAAA", 33 => "SRV", 35 => "NAPTR", 39 => "DNAME", 41 => "OPT", 64 => "SVCB", 65 => "HTTPS",
        255 => "ANY", 257 => "CAA", _ => $"TYPE{value}"
    };

    internal static string ClassName(ushort value) => value switch { 1 => "IN", 3 => "CH", 4 => "HS", _ => $"CLASS{value}" };

    public async Task<DnsLookupResponse> QueryAsync(DnsLookupOptions options, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Query);
        ArgumentNullException.ThrowIfNull(options.Server);
        if (options.Server.Port is < 1 or > 65535 || options.TimeoutMilliseconds is < 100 or > 60000 || options.Retries is < 0 or > 5)
            throw new ArgumentException("端口应为 1–65535，单次超时为 100–60000 毫秒，重试次数为 0–5。");
        if (options.RecordType is 251 or 252 || options.QueryClass is not (1 or 3 or 4))
            throw new ArgumentException("不支持此查询类型或查询类。");
        if (options.HttpsEndpoint is { } endpoint && (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps ||
            endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0))
            throw new ArgumentException("DoH 地址必须是完整的 HTTPS URL，不能包含用户凭据或片段。");
        var serverLabel = options.HttpsEndpoint?.AbsoluteUri ?? options.Server.ToString();
        var query = DnsMessageCodec.CreateRequest(options);
        var started = Stopwatch.GetTimestamp();
        for (var attempt = 0; attempt <= options.Retries; attempt++)
        {
            token.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(options.TimeoutMilliseconds);
            try
            {
                byte[] packet;
                var transport = options.UseTcp ? "TCP" : "UDP";
                if (options.HttpsEndpoint is { } httpsEndpoint)
                {
                    transport = "DoH · HTTPS";
                    packet = await ExchangeHttpsAsync(httpsEndpoint, query.Packet, deadline.Token).ConfigureAwait(false);
                    if (packet.Length >= 4 && (packet[2] & 2) != 0)
                        throw new InvalidDataException("DoH 服务器返回了截断的 DNS 响应。");
                }
                else if (options.UseTcp)
                    packet = await ExchangeTcpAsync(options.Server, query.Packet, deadline.Token).ConfigureAwait(false);
                else
                {
                    using var socket = new UdpClient(options.Server.AddressFamily);
                    socket.Connect(options.Server);
                    await socket.SendAsync(query.Packet, deadline.Token).ConfigureAwait(false);
                    while (true)
                    {
                        var datagram = await socket.ReceiveAsync(deadline.Token).ConfigureAwait(false);
                        // Connected UDP restricts the peer; ID and echoed question must also match.
                        if (!DnsMessageCodec.MatchesQuestion(datagram.Buffer, query)) continue;
                        packet = datagram.Buffer;
                        break;
                    }
                    if ((BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2)) & 0x0200) != 0)
                    {
                        transport = "UDP → TCP";
                        packet = await ExchangeTcpAsync(options.Server, query.Packet, deadline.Token).ConfigureAwait(false);
                    }
                }
                if (!DnsMessageCodec.MatchesQuestion(packet, query)) throw new InvalidDataException("DNS 响应与请求的 ID、名称或类型不匹配。");
                var parsed = DnsMessageCodec.ParseResponse(packet);
                return new(query.Name, TypeName(query.Type), ClassName(options.QueryClass), serverLabel, transport,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds, attempt + 1, parsed.Code, parsed.Flags, packet.Length, parsed.Records);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                if (attempt == options.Retries)
                    throw new TimeoutException($"DNS 服务器 {serverLabel} 未在限定时间内响应（共 {attempt + 1} 次尝试）。");
            }
            catch (Exception ex) when ((ex is SocketException or IOException or InvalidDataException or HttpRequestException) && attempt < options.Retries)
            {
                // Retry network/framing failures on a new socket, retaining the selected server.
            }
        }
        throw new InvalidOperationException("DNS 查询未完成。");
    }

    private async Task<byte[]> ExchangeHttpsAsync(Uri endpoint, byte[] packet, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));
        request.Content = new ByteArrayContent(packet);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/dns-message", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("DoH 服务器没有返回 application/dns-message 格式。");
        if (response.Content.Headers.ContentLength is < 12 or > 65535)
            throw new InvalidDataException("DoH 响应长度超出 DNS 报文范围。");
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = new byte[65536];
        var length = 0;
        while (length < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(length), token).ConfigureAwait(false);
            if (count == 0) break;
            length += count;
        }
        if (length is < 12 or > 65535) throw new InvalidDataException("DoH 响应长度超出 DNS 报文范围。");
        return buffer[..length];
    }

    private static async Task<byte[]> ExchangeTcpAsync(IPEndPoint server, byte[] request, CancellationToken token)
    {
        using var client = new TcpClient(server.AddressFamily);
        await client.ConnectAsync(server, token).ConfigureAwait(false);
        using var stream = client.GetStream();
        var framed = new byte[request.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(framed, checked((ushort)request.Length));
        request.CopyTo(framed, 2);
        await stream.WriteAsync(framed, token).ConfigureAwait(false);
        var prefix = new byte[2];
        await stream.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt16BigEndian(prefix);
        if (length < 12) throw new InvalidDataException("DNS TCP 响应长度无效。");
        var packet = new byte[length];
        await stream.ReadExactlyAsync(packet, token).ConfigureAwait(false);
        return packet;
    }
}
