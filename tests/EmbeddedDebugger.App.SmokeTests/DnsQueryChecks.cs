using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using EmbeddedDebugger.Infrastructure.Networking;

namespace EmbeddedDebugger.App.SmokeTests;

internal static class DnsQueryChecks
{
    internal static async Task RunAsync()
    {
        await CheckDohAsync();
        await using (var server = new Fixture((request, _, _) => Reply(request, 0x8580,
            new(1, IPAddress.Parse("203.0.113.9").GetAddressBytes()),
            new(28, IPAddress.Parse("2001:db8::9").GetAddressBytes()),
            new(5, Compressed("alias")), new(15, Join(U16(10), Compressed("mail"))),
            new(16, Join(CharacterString("first"), CharacterString("second\nline"))),
            new(6, Join(Compressed("ns"), Compressed("hostmaster"), U32(2026101101), U32(3600), U32(600), U32(86400), U32(60))),
            new(12, Compressed("host")), new(33, Join(U16(10), U16(20), U16(443), Compressed("service"))),
            new(257, Join([0, 5], Encoding.ASCII.GetBytes("issueletsencrypt.org"))),
            new(39, Compressed("target")), new(35, Join(U16(10), U16(20), CharacterString("S"), CharacterString("SIP+D2U"), CharacterString(""), Compressed("sip"))),
            new(65280, [1, 2, 3]), new(2, Compressed("ns"), 1), new(1, [192, 0, 2, 53], 2))))
        {
            var result = await Lookup(server, "example.test", 255);
            Require(result.Records.Count == 14 && result.Flags.Contains("AA") && result.Flags.Contains("RA"), "DNS parses all response sections and header flags");
            Require(result.Records[0].Value == "203.0.113.9" && result.Records[1].Value == "2001:db8::9", "A and AAAA decode to addresses");
            Require(result.Records[2].Value == "alias.example.test" && result.Records[3].Value == "10 mail.example.test", "Compressed CNAME and MX names decode correctly");
            Require(result.Records[4].Value == "\"first\" \"second\\nline\"", "TXT preserves separate character strings and escapes newlines");
            Require(result.Records[5].Value.Contains("serial=2026101101") && result.Records[5].Value.Contains("minimum=60"), "SOA retains all five timing fields");
            Require(result.Records[6].Value == "host.example.test" && result.Records[7].Value.Contains("port=443 target=service.example.test"), "PTR and SRV retain names and service parameters");
            Require(result.Records[8].Value == "0 issue \"letsencrypt.org\"" && result.Records[9].Value == "target.example.test", "CAA and DNAME decode correctly");
            Require(result.Records[10].Value.Contains("SIP+D2U") && result.Records[10].Value.EndsWith("sip.example.test", StringComparison.Ordinal), "NAPTR retains order, preference and replacement");
            Require(result.Records[11].Type == "TYPE65280" && result.Records[11].Value == "\\# 3 010203", "Unknown record types remain available as RFC-style raw data");
            Require(result.Records.All(record => record.Ttl == 300) && result.Records[12].Section == "权威" && result.Records[13].Section == "附加", "TTL and section labels come from the response");
        }

        await using (var server = new Fixture((request, _, _) => Reply(request, 0x8000)))
        {
            var options = Options(server, "_sip._tcp.example.test", 33) with { QueryClass = 3, RecursionDesired = false, UseTcp = true };
            var result = await new DnsLookupClient().QueryAsync(options);
            var request = server.Requests.Single();
            Require(request.Tcp && (request.Packet[2] & 1) == 0 && BinaryPrimitives.ReadUInt16BigEndian(request.Packet.AsSpan(request.Packet.Length - 2)) == 3,
                "Custom port, TCP, recursion-off and CH class reach the selected DNS server");
            Require(result.Transport == "TCP" && result.Type == "SRV" && result.Class == "CH" && result.Records.Count == 0 && result.ResponseCode == 0,
                "A successful empty answer is distinct from a nonexistent domain");
            Require((await Lookup(server, "例子.测试.", 1)).Question == "xn--fsqu00a.xn--0zwm56d", "Internationalized names and trailing root dots are encoded correctly");
            Require((await Lookup(server, ".", 2)).Question == ".", "Root-zone queries are supported");
            var v4 = await Lookup(server, "192.0.2.9", 0);
            Require(v4.Type == "PTR" && v4.Question == "9.2.0.192.in-addr.arpa", "Automatic IPv4 input performs reverse PTR queries");
            var v6 = await Lookup(server, "2001:db8::9", 12);
            Require(v6.Question == "9.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.8.b.d.0.1.0.0.2.ip6.arpa", "IPv6 reverse queries reverse every address nibble");
        }
        await using (var server = new Fixture((request, tcp, _) => Reply(request, tcp ? (ushort)0x8180 : (ushort)0x8380,
            new Record(1, [192, 0, 2, 1]))))
        {
            var response = await Lookup(server, "example.test", 1);
            Require(response.Transport == "UDP → TCP" && server.Requests.Count == 2 && server.Requests.Last().Tcp,
                "Truncated UDP responses retry over TCP on the same custom server and port");
        }
        await using (var server = new Fixture((request, _, _) => Reply(request, 0x8183)))
            Require((await Lookup(server, "missing.test", 1)).Status.StartsWith("NXDOMAIN", StringComparison.Ordinal), "DNS errors retain their real RCODE");
        await using (var server = new Fixture((request, _, _) => Reply(request, 0x8185)))
            Require((await Lookup(server, "refused.test", 1)).ResponseCode == 5, "REFUSED is shown instead of being mistaken for an empty answer");
        await using (var server = new Fixture((request, _, count) => count == 1 ? null : Reply(request, 0x8180)))
            Require((await new DnsLookupClient().QueryAsync(Options(server, "retry.test", 1) with { Retries = 1, TimeoutMilliseconds = 150 })).Attempts == 2,
                "Timeout retry count is honored");
        await using (var server = new Fixture((request, _, _) => Reply(request, 0x8180, new Record(1, [192, 0, 2, 1])), noise: true))
            Require((await Lookup(server, "validated.test", 1)).Records.Single().Value == "192.0.2.1", "Unrelated transaction IDs and echoed questions are ignored");
        foreach (var failure in new[] { "length", "pointer", "txt" })
        {
            await using var server = new Fixture((request, _, _) =>
            {
                var reply = Reply(request, 0x8180, new Record(5, [0xC0, 0x0C]));
                if (failure == "length") reply[^3] = 255;
                if (failure == "pointer") reply[^1] = (byte)(reply.Length - 2);
                if (failure == "txt") reply = Reply(request, 0x8180, new Record(16, [10, 65]));
                return reply;
            });
            await ThrowsAsync<InvalidDataException>(() => Lookup(server, "broken.test", 1), "Malformed DNS " + failure + " is rejected");
        }
        await using (var server = new Fixture((_, _, _) => null))
        {
            await ThrowsAsync<TimeoutException>(() => new DnsLookupClient().QueryAsync(Options(server, "timeout.test", 1) with { TimeoutMilliseconds = 100 }), "Silent DNS servers time out");
            using var cancel = new CancellationTokenSource(80);
            await ThrowsAsync<OperationCanceledException>(() => new DnsLookupClient().QueryAsync(Options(server, "cancel.test", 1), cancel.Token), "DNS receives are cancelable");
            await ThrowsAsync<FormatException>(() => Lookup(server, "a..test", 1), "Invalid labels fail before sending");
            await ThrowsAsync<FormatException>(() => Lookup(server, new string('a', 64) + ".test", 1), "Overlong labels fail before sending");
        }
        if (Socket.OSSupportsIPv6)
        {
            await using var server = new Fixture((request, _, _) => Reply(request, 0x8180), ipv6: true);
            Require((await Lookup(server, "ipv6-server.test", 1)).Server.StartsWith("[::1]", StringComparison.Ordinal), "An IPv6 DNS server is contacted directly");
        }
        Require(DnsLookupClient.ParseType("type65280") == 65280 && DnsLookupClient.ParseType("aaaa") == 28, "Record type input is case insensitive and supports TYPE numbers");
        await CheckWindowAsync();
    }

    internal static async Task RunLiveAsync()
    {
        foreach (var (name, type, tcp) in new[] { ("example.com", (ushort)1, false), ("example.com", (ushort)28, true), ("qq.com", (ushort)15, false), ("example.com", (ushort)16, false) })
        {
            var result = await new DnsLookupClient().QueryAsync(new(name, type, new(IPAddress.Parse("223.5.5.5"), 53)) { UseTcp = tcp, Retries = 0, TimeoutMilliseconds = 3000 });
            Console.WriteLine($"{result.Question} {result.Type} @ {result.Server}: {result.Status} / {result.Transport} / {result.ElapsedMilliseconds:0.0} ms");
            foreach (var record in result.Records) Console.WriteLine($"{record.Section}: {record.Name} {record.Ttl} {record.Type} {record.Value}");
        }
    }

    internal static async Task RunLiveDohAsync()
    {
        foreach (var endpoint in new[] { "https://dns.alidns.com/dns-query", "https://doh.pub/dns-query", "https://cloudflare-dns.com/dns-query", "https://dns.google/dns-query" })
        {
            var result = await new DnsLookupClient().QueryAsync(new("example.com", 1, new(IPAddress.Loopback, 53))
                { HttpsEndpoint = new(endpoint), TimeoutMilliseconds = 5000, Retries = 1 });
            Require(result.ResponseCode == 0 && result.Records.Any(r => r.Type == "A" && r.Section == "应答"), "Live DoH answers via validated HTTPS: " + endpoint);
            Console.WriteLine($"{result.Transport} / {result.ElapsedMilliseconds:F1} ms / {string.Join(", ", result.Records.Select(r => r.Value))}");
        }
    }

    private static async Task CheckDohAsync()
    {
        var options = new DnsLookupOptions("example.test", 1, new(IPAddress.Loopback, 53))
            { HttpsEndpoint = new("https://resolver.example:8443/dns-query"), TimeoutMilliseconds = 200, Retries = 0 };
        using var handler = new DohHandler(async (request, token) =>
        {
            Require(request.Method == HttpMethod.Post && request.RequestUri == options.HttpsEndpoint, "DoH uses the exact custom HTTPS endpoint and POST");
            Require(request.Content!.Headers.ContentType?.MediaType == "application/dns-message" && request.Headers.Accept.Any(h => h.MediaType == "application/dns-message"), "DoH exchanges RFC 8484 wire messages");
            var packet = await request.Content.ReadAsByteArrayAsync(token);
            return DohResponse(Reply(packet, 0x8180, new Record(1, [192, 0, 2, 6])));
        });
        using var http = new HttpClient(handler);
        var client = new DnsLookupClient(http);
        var response = await client.QueryAsync(options);
        Require(response.Transport == "DoH · HTTPS" && response.Records.Single().Value == "192.0.2.6" && response.Server == options.HttpsEndpoint.AbsoluteUri,
            "DoH preserves decoded records and identifies its actual resolver");
        await ThrowsAsync<ArgumentException>(() => client.QueryAsync(options with { HttpsEndpoint = new("http://resolver.example/dns-query") }), "DoH rejects unencrypted endpoints before sending");
        foreach (var failure in new[] { "media", "size", "id", "question", "truncated", "http" })
        {
            using var badHandler = new DohHandler(async (request, token) =>
            {
                var packet = Reply(await request.Content!.ReadAsByteArrayAsync(token), 0x8180);
                if (failure == "size") packet = new byte[65536];
                if (failure == "id") packet[0] ^= 1;
                if (failure == "question") packet[13] ^= 1;
                if (failure == "truncated") packet[2] |= 2;
                var reply = DohResponse(packet);
                if (failure == "media") reply.Content.Headers.ContentType = new("text/html");
                if (failure == "http") reply.StatusCode = HttpStatusCode.BadGateway;
                return reply;
            });
            using var badHttp = new HttpClient(badHandler);
            if (failure == "http") await ThrowsAsync<HttpRequestException>(() => new DnsLookupClient(badHttp).QueryAsync(options), "DoH rejects HTTP errors");
            else await ThrowsAsync<InvalidDataException>(() => new DnsLookupClient(badHttp).QueryAsync(options), "DoH rejects invalid " + failure);
        }
        var attempts = 0;
        using var slowHandler = new DohHandler(async (request, token) =>
        {
            attempts++;
            await Task.Delay(1000, token);
            return DohResponse(Reply(await request.Content!.ReadAsByteArrayAsync(token), 0x8180));
        });
        using var slowHttp = new HttpClient(slowHandler);
        await ThrowsAsync<TimeoutException>(() => new DnsLookupClient(slowHttp).QueryAsync(options with { Retries = 1 }), "DoH timeout includes retry limit");
        Require(attempts == 2, "DoH observes configured retries");
        using var cancel = new CancellationTokenSource(30);
        await ThrowsAsync<OperationCanceledException>(() => new DnsLookupClient(slowHttp).QueryAsync(options, cancel.Token), "DoH request cancellation is immediate");
    }

    private static HttpResponseMessage DohResponse(byte[] packet)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(packet) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
        return response;
    }
    private sealed class DohHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private static async Task CheckWindowAsync()
    {
        await using var server = new Fixture((request, _, _) => Reply(request, 0x8580, new(15, Join(U16(10), Compressed("mail"))), new(1, [192, 0, 2, 10], 2)));
        var window = new DnsQueryWindow(); window.Show();
        try
        {
            await FileWorkspaceChecks.CaptureAsync(window, "embedded-debugger-dns-default.png");
            Require(!((ComboBox)window.FindName("ServerBox")).Text.StartsWith("fec0:0:0:ffff::", StringComparison.OrdinalIgnoreCase),
                "Default DNS selection excludes Windows virtual-adapter placeholder servers");
            ((TextBox)window.FindName("QueryBox")).Text = "example.test";
            ((ComboBox)window.FindName("TypeBox")).Text = "MX";
            ((ComboBox)window.FindName("ServerBox")).Text = server.Endpoint.Address.ToString();
            ((TextBox)window.FindName("PortBox")).Text = server.Endpoint.Port.ToString();
            ((ComboBox)window.FindName("TransportBox")).SelectedIndex = 1;
            Click(window, "QueryButton");
            await WaitAsync(() => ((Button)window.FindName("QueryButton")).IsEnabled);
            Require(window.Results.Count == 2 && ((TextBox)window.FindName("DetailsBox")).Text.Contains("mail.example.test"), "DNS window sends the selected query and displays full records and text details");
            Require(server.Requests.Single().Tcp && BinaryPrimitives.ReadUInt16BigEndian(server.Requests.Single().Packet.AsSpan(server.Requests.Single().Packet.Length - 4)) == 15,
                "DNS UI type/server/port/protocol controls affect the wire request");
            await FileWorkspaceChecks.CaptureAsync(window, "embedded-debugger-dns-options.png");
            window.Width = window.MinWidth; window.Height = window.MinHeight;
            await FileWorkspaceChecks.CaptureAsync(window, "embedded-debugger-dns-options-small.png");
            ((TextBox)window.FindName("PortBox")).Text = "70000";
            Click(window, "QueryButton");
            Require(((Button)window.FindName("QueryButton")).IsEnabled && server.Requests.Count == 1, "Invalid parameters keep the UI usable and do not send a request");
            ((ComboBox)window.FindName("TransportBox")).SelectedIndex = 2;
            Require(((ComboBox)window.FindName("ServerBox")).Text == "https://dns.alidns.com/dns-query" && !((FrameworkElement)window.FindName("PortPanel")).IsVisible,
                "DoH switches server presets and gives the HTTPS URL the port column's space");
            await FileWorkspaceChecks.CaptureAsync(window, "embedded-debugger-doh-small.png");
            ((ComboBox)window.FindName("ServerBox")).Text = "https://resolver.test:8443/custom-query";
            ((ComboBox)window.FindName("TransportBox")).SelectedIndex = 0;
            Require(((ComboBox)window.FindName("ServerBox")).Text == server.Endpoint.Address.ToString(), "Switching back from DoH restores the entered DNS server");
            ((ComboBox)window.FindName("TransportBox")).SelectedIndex = 2;
            Require(((ComboBox)window.FindName("ServerBox")).Text == "https://resolver.test:8443/custom-query", "Custom DoH endpoint survives protocol switches");
        }
        finally { window.Close(); }
        await using var silent = new Fixture((_, _, _) => null);
        var canceled = new DnsQueryWindow(); canceled.Show();
        ((ComboBox)canceled.FindName("ServerBox")).Text = "127.0.0.1";
        ((TextBox)canceled.FindName("PortBox")).Text = silent.Endpoint.Port.ToString();
        Click(canceled, "QueryButton"); Click(canceled, "StopButton");
        await WaitAsync(() => ((Button)canceled.FindName("QueryButton")).IsEnabled);
        Require(((TextBlock)canceled.FindName("ResponseText")).Text == "已停止", "Stop cancels DNS and restores parameter controls");
        Click(canceled, "QueryButton"); canceled.Close();
        await Task.Delay(80);
        Require(!((Button)canceled.FindName("QueryButton")).IsEnabled, "Closing a DNS window cancels its pending query without updating disposed UI");
    }

    private static DnsLookupOptions Options(Fixture server, string name, ushort type) => new(name, type, server.Endpoint) { TimeoutMilliseconds = 1500, Retries = 0 };
    private static Task<DnsLookupResponse> Lookup(Fixture server, string name, ushort type) => new DnsLookupClient().QueryAsync(Options(server, name, type));
    private static void Require(bool condition, string text) { if (!condition) throw new InvalidOperationException(text); Console.WriteLine("PASS " + text); }
    private static async Task ThrowsAsync<T>(Func<Task> task, string text) where T : Exception
    { try { await task(); } catch (T) { Console.WriteLine("PASS " + text); return; } throw new InvalidOperationException(text); }
    private static void Click(Window window, string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task WaitAsync(Func<bool> predicate)
    { var until = DateTime.UtcNow.AddSeconds(5); while (!predicate()) { if (DateTime.UtcNow > until) throw new TimeoutException(); await Task.Delay(20); } }

    private sealed record Record(ushort Type, byte[] Data, int Section = 0);
    private static byte[] Reply(byte[] request, ushort flags, params Record[] records)
    {
        using var stream = new MemoryStream();
        stream.Write(request);
        foreach (var record in records.OrderBy(record => record.Section))
        {
            stream.Write(Join([0xC0, 0x0C], U16(record.Type), U16(1), U32(300), U16((ushort)record.Data.Length), record.Data));
        }
        var packet = stream.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), flags);
        for (var section = 0; section < 3; section++) BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6 + section * 2), (ushort)records.Count(record => record.Section == section));
        return packet;
    }
    private static byte[] Compressed(string label) => Join(CharacterString(label), [0xC0, 0x0C]);
    private static byte[] CharacterString(string value) { var bytes = Encoding.UTF8.GetBytes(value); return Join([(byte)bytes.Length], bytes); }
    private static byte[] U16(ushort value) { var data = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(data, value); return data; }
    private static byte[] U32(uint value) { var data = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(data, value); return data; }
    private static byte[] Join(params byte[][] arrays) => arrays.SelectMany(bytes => bytes).ToArray();

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly UdpClient _udp;
        private readonly TcpListener _tcp;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _worker;
        private readonly Func<byte[], bool, int, byte[]?> _respond;
        private readonly bool _noise;
        public ConcurrentQueue<(byte[] Packet, bool Tcp)> Requests { get; } = new();
        public IPEndPoint Endpoint => (IPEndPoint)_udp.Client.LocalEndPoint!;
        public Fixture(Func<byte[], bool, int, byte[]?> respond, bool noise = false, bool ipv6 = false)
        {
            _respond = respond; _noise = noise;
            var address = ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
            _tcp = new(address, 0); _tcp.Start();
            _udp = new((IPEndPoint)_tcp.LocalEndpoint);
            _worker = Task.WhenAll(ServeUdp(), ServeTcp());
        }
        private async Task ServeUdp()
        {
            try
            {
                while (true)
                {
                    UdpReceiveResult request;
                    try { request = await _udp.ReceiveAsync(_stop.Token); }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset) { continue; }
                    Requests.Enqueue((request.Buffer, false));
                    var response = _respond(request.Buffer, false, Requests.Count);
                    if (response is null) continue;
                    if (_noise)
                    {
                        var wrong = response.ToArray(); wrong[0] ^= 0x80;
                        await _udp.SendAsync(wrong, request.RemoteEndPoint, _stop.Token);
                        wrong = response.ToArray(); wrong[13] ^= 1;
                        await _udp.SendAsync(wrong, request.RemoteEndPoint, _stop.Token);
                    }
                    await _udp.SendAsync(response, request.RemoteEndPoint, _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }
        private async Task ServeTcp()
        {
            try
            {
                while (true)
                {
                    using var client = await _tcp.AcceptTcpClientAsync(_stop.Token);
                    using var stream = client.GetStream();
                    var prefix = new byte[2]; await stream.ReadExactlyAsync(prefix, _stop.Token);
                    var request = new byte[BinaryPrimitives.ReadUInt16BigEndian(prefix)];
                    await stream.ReadExactlyAsync(request, _stop.Token);
                    Requests.Enqueue((request, true));
                    var response = _respond(request, true, Requests.Count);
                    if (response is null) continue;
                    var frame = Join(U16((ushort)response.Length), response);
                    await stream.WriteAsync(frame.AsMemory(0, 1), _stop.Token);
                    await stream.WriteAsync(frame.AsMemory(1), _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync()
        { _stop.Cancel(); await _worker; _udp.Dispose(); _tcp.Stop(); _stop.Dispose(); }
    }
}
