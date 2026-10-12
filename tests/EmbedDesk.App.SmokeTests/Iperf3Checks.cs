using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using EmbedDesk.Infrastructure.Networking;

namespace EmbedDesk.App.SmokeTests;

internal static class Iperf3Checks
{
    internal static async Task RunAsync()
    {
        using (var json = JsonDocument.Parse("{\"tcp\":true,\"time\":1,\"parallel\":1,\"reverse\":true,\"len\":4096}"))
        { var p = Iperf3Client.ValidateParameters(json.RootElement); Require(p.Seconds == 1 && p.BlockBytes == 4096 && p.Reverse && !p.Udp, "iperf3 standard parameter mapping"); }
        foreach (var text in new[] { "{\"udp\":true,\"len\":11}", "{\"tcp\":true,\"parallel\":2}", "{\"tcp\":true,\"len\":2147483647}", "{\"tcp\":true,\"time\":0}", "{\"tcp\":true,\"bandwidth\":-1}" })
        {
            using var json = JsonDocument.Parse(text);
            try { Iperf3Client.ValidateParameters(json.RootElement); throw new Exception("Unsupported parameters accepted"); }
            catch (NotSupportedException) { }
        }
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var port = FreePort(); var reports = new List<Iperf3Result>();
        var intervals = new ConcurrentQueue<Iperf3Progress>();
        using var serverStop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var server = Iperf3Client.RunServerAsync(IPAddress.Loopback, port, new InlineProgress<Iperf3Progress>(intervals.Enqueue), results: new InlineProgress<Iperf3Result>(r => reports.Add(r)), token: serverStop.Token);
        try
        {
            // A malicious length is refused before allocation; a subsequent valid session still succeeds.
            using (var bad = new TcpClient())
            {
                await bad.ConnectAsync(IPAddress.Loopback, port, lifetime.Token);
                await bad.GetStream().WriteAsync(Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("D") + "\0"), lifetime.Token);
                var state = new byte[1]; await bad.GetStream().ReadExactlyAsync(state, lifetime.Token);
                Require(state[0] == 9, "iperf3 server PARAM_EXCHANGE wire value");
                await bad.GetStream().WriteAsync(new byte[] { 0x7F, 0xFF, 0xFF, 0xFF }, lifetime.Token);
                await bad.GetStream().ReadExactlyAsync(state, lifetime.Token);
                Require(state[0] == 11, "Oversize JSON refused without allocation");
            }
            foreach (var reverse in new[] { false, true })
            {
                var result = await Iperf3Client.RunAsync("127.0.0.1", port, 1, reverse, token: lifetime.Token);
                Require(result.LocalBytes > 0 && result.PeerBytes > 0 && result.LocalSeconds is > .8 and < 4 && result.LocalSender != reverse, $"iperf3 loopback {(reverse ? "reverse" : "forward")} data and statistics");
                Require(Math.Abs(result.LocalBytes - result.PeerBytes) < Math.Max(1048576, result.LocalBytes / 100), "iperf3 local/peer counts agree within buffered tail");
            }
            await CheckStrayCookieAsync(port, lifetime.Token);
            foreach (var reverse in new[] { false, true })
            {
                using var cancel = new CancellationTokenSource(250);
                try { await Iperf3Client.RunAsync("127.0.0.1", port, 10, reverse, token: cancel.Token); throw new Exception("Active data cancellation ignored"); }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            }
            Console.WriteLine("PASS active forward/reverse cancellation is reported as cancellation");
        }
        finally { serverStop.Cancel(); try { await server; } catch (OperationCanceledException) { } }
        Require(reports.Count == 3, "Persistent iperf3 server survives malformed frames and stray stream candidates");
        Require(intervals.Any(p => p.Seconds < .8 && p.IntervalBytes == 4096 && p.IntervalSeconds > 0),
            "Server reports the final partial interval even for tests shorter than one second");

        // An independent peer verifies cookie, big-endian JSON lengths and state sequence.
        var fixtureListener = new TcpListener(IPAddress.Loopback, 0); fixtureListener.Start();
        var fixturePort = ((IPEndPoint)fixtureListener.LocalEndpoint).Port;
        var fixture = RunFixtureAsync(fixtureListener, lifetime.Token);
        try
        {
            var result = await Iperf3Client.RunAsync("127.0.0.1", fixturePort, 1, token: lifetime.Token);
            Require(result.LocalSender && result.LocalBytes > 0 && result.PeerBytes > 0, "Independent iperf3 fixture accepts native client wire exchange");
            await fixture;
        }
        finally { fixtureListener.Stop(); }

        await CheckReverseMeasurementAsync(lifetime.Token);
        await CheckUdpAndPacingAsync(lifetime.Token);

        var silentListener = new TcpListener(IPAddress.Loopback, 0); silentListener.Start();
        try
        {
            using var cancel = new CancellationTokenSource(100);
            try { await Iperf3Client.RunAsync("127.0.0.1", ((IPEndPoint)silentListener.LocalEndpoint).Port, 10, token: cancel.Token); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { }
        }
        finally { silentListener.Stop(); }
        Console.WriteLine("PASS native iperf3 wire fixture, forward/reverse loopback, malformed JSON recovery and cancellation");
    }
    internal static async Task CheckPeerAsync(string host, int port)
    {
        // Explicit opt-in only. Public checks use 2 seconds at 2 Mbit/s per case.
        foreach (var udp in new[] { false, true })
            foreach (var reverse in new[] { false, true })
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                var result = await Iperf3Client.RunAsync(host, port, new Iperf3Options(2, reverse, udp, udp ? 1200 : 8192, 2_000_000), token: timeout.Token);
                Require(result.LocalBytes > 0 && result.PeerBytes > 0, $"Standard peer {host}:{port} {(udp ? "UDP" : "TCP")} {(reverse ? "reverse" : "forward")}");
                Console.WriteLine($"PEER local={result.LocalBytes} bytes/{result.LocalSeconds:F3}s peer={result.PeerBytes} bytes/{result.PeerSeconds:F3}s UDP={result.ReceiverUdp}");
                await Task.Delay(300);
            }
    }

    private static async Task CheckUdpAndPacingAsync(CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var port = FreePort();
        var server = Iperf3Client.RunServerAsync(IPAddress.Loopback, port, token: stop.Token);
        try
        {
            foreach (var udp in new[] { false, true })
                foreach (var reverse in new[] { false, true })
                {
                    var result = await Iperf3Client.RunAsync("127.0.0.1", port, new Iperf3Options(1, reverse, udp, udp ? 256 : 8192, 500_000), token: token);
                    var sentRate = result.LocalSender ? result.LocalBitsPerSecond : result.PeerBitsPerSecond;
                    Require(sentRate is > 350_000 and < 650_000, $"{(udp ? "UDP" : "TCP")} {(reverse ? "reverse" : "forward")} follows negotiated 500 Kbit/s pacing");
                    if (udp) Require(result.ReceiverUdp is { Packets: > 0, LostPackets: 0 } && result.LocalBytes % 256 == 0,
                        "UDP counts whole configured datagrams and reports receiver loss");
                }
            foreach (var counters64 in new[] { false, true }) await CheckUdpCountersAsync(port, counters64, token);
            foreach (var reverse in new[] { false, true })
            {
                var result = await Iperf3Client.RunAsync("127.0.0.1", port, new Iperf3Options(1, reverse, true, 1200, 0, false), token: token);
                Require(result.LocalBytes > 0 && result.PeerBytes > 0 && result.LocalSeconds < 3,
                    $"Unlimited UDP {(reverse ? "reverse" : "forward")} keeps timers responsive with 32-bit packet counters");
            }
            using (var cancelled = new CancellationTokenSource(200))
            {
                try { await Iperf3Client.RunAsync("127.0.0.1", port, new Iperf3Options(10, false, true, 1200, 1), token: cancelled.Token); throw new Exception("Slow UDP pacing ignored cancellation"); }
                catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { Console.WriteLine("PASS UDP cancellation interrupts long pacing waits"); }
            }
        }
        finally { stop.Cancel(); try { await server; } catch (OperationCanceledException) { } }
        if (Socket.OSSupportsIPv6)
        {
            using var ipv6Stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            var ipv6Server = Iperf3Client.RunServerAsync(IPAddress.IPv6Loopback, port, token: ipv6Stop.Token);
            try
            {
                var result = await Iperf3Client.RunAsync("::1", port, new Iperf3Options(1, false, true, 1200, 1_000_000), token: token);
                Require(result.PeerBytes > 0, "UDP handshake and data run over IPv6");
            }
            finally { ipv6Stop.Cancel(); try { await ipv6Server; } catch (OperationCanceledException) { } }
        }
    }

    private static async Task CheckUdpCountersAsync(int port, bool counters64, CancellationToken token)
    {
        using var control = new TcpClient(); await control.ConnectAsync(IPAddress.Loopback, port, token);
        var stream = control.GetStream(); await stream.WriteAsync(Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("D") + "\0"), token);
        var state = new byte[1]; await stream.ReadExactlyAsync(state, token);
        await SendJsonAsync(stream, new { udp = true, time = 1, parallel = 1, len = 64, bandwidth = 1000000, udp_counters_64bit = counters64 ? 1 : 0 }, token);
        await stream.ReadExactlyAsync(state, token); Require(state[0] == 10, "UDP peer receives CREATE_STREAMS over TCP");
        using var data = new UdpClient(); data.Connect(IPAddress.Loopback, port);
        // Exercise legacy client handshake as well as modern iperf3's wire bytes.
        var magic = counters64 ? new byte[] { 0x39, 0x38, 0x37, 0x36 } : new byte[] { 0x15, 0xCD, 0x5B, 0x07 };
        await data.SendAsync(magic, token);
        var reply = await data.ReceiveAsync(token);
        Require(reply.Buffer.SequenceEqual(new byte[] { 0x36, 0x37, 0x38, 0x39 }), "UDP connect reply matches upstream iperf.h wire constants");
        var states = new byte[2]; await stream.ReadExactlyAsync(states, token);
        Require(states.SequenceEqual(new byte[] { 1, 2 }), "UDP starts with standard TEST_START / TEST_RUNNING");
        foreach (var sequence in new long[] { 1, 3, 2, 5 })
        {
            var packet = new byte[64]; BinaryPrimitives.WriteUInt32BigEndian(packet, 12345);
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), (uint)(sequence * 1000));
            if (counters64) BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(8), sequence);
            else BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), (uint)sequence);
            await data.SendAsync(packet, token); await Task.Delay(10, token);
        }
        await data.SendAsync(new byte[8], token); // Malformed/truncated header is not test traffic.
        await Task.Delay(60, token);
        await stream.WriteAsync(new byte[] { 4 }, token); await stream.ReadExactlyAsync(state, token);
        await SendJsonAsync(stream, new { streams = new[] { new { id = 1, bytes = 256, packets = 5, errors = 0, jitter = 0, start_time = 0, end_time = .1 } } }, token);
        using var result = await JsonAsync(stream, token); var stats = result.RootElement.GetProperty("streams")[0];
        Require(stats.GetProperty("bytes").GetInt64() == 256 && stats.GetProperty("packets").GetInt64() == 5 && stats.GetProperty("errors").GetInt64() == 1 && stats.GetProperty("out_of_order").GetInt64() == 1,
            $"UDP {(counters64 ? 64 : 32)}-bit counters repair reordered gaps and reject short packets");
        Require(stats.GetProperty("jitter").GetDouble() > 0, "UDP jitter reflects changing transit time");
        await stream.ReadExactlyAsync(state, token); await stream.WriteAsync(new byte[] { 16 }, token);
    }

    private static async Task SendJsonAsync(NetworkStream stream, object value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value); var header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(bytes, token);
    }
    private static async Task CheckReverseMeasurementAsync(CancellationToken token)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var intervals = new ConcurrentQueue<Iperf3Progress>();
        var fixture = ReverseFixtureAsync();
        var result = await Iperf3Client.RunAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, 2, true,
            new InlineProgress<Iperf3Progress>(intervals.Enqueue), token);
        await fixture;
        Require(result.LocalBytes == 8192 && result.LocalSeconds is >= 1.9 and < 2.8,
            "Reverse measurements exclude late data and a delayed result exchange");
        var samples = intervals.ToArray();
        Require(samples.Length >= 2 && samples.Any(p => p.IntervalBytes == 0 && p.BitsPerSecond == 0),
            "Idle TCP intervals are reported at zero rate without waiting for socket I/O");
        Require(samples.Sum(p => p.IntervalBytes) == result.LocalBytes &&
            Math.Abs(samples.Sum(p => p.IntervalSeconds) - result.LocalSeconds) < .000001 &&
            samples.All(p => double.IsFinite(p.BitsPerSecond) && p.BitsPerSecond >= 0),
            "Interval bytes and monotonic durations add up to the final measurement");

        async Task ReverseFixtureAsync()
        {
            using var control = await listener.AcceptTcpClientAsync(token); var stream = control.GetStream();
            var cookie = new byte[37]; await stream.ReadExactlyAsync(cookie, token);
            await stream.WriteAsync(new byte[] { 9 }, token);
            using var parameters = await JsonAsync(stream, token);
            Require(parameters.RootElement.GetProperty("reverse").GetBoolean(), "Reverse parameter reaches independent wire peer");
            await stream.WriteAsync(new byte[] { 10 }, token);
            using var data = await listener.AcceptTcpClientAsync(token);
            var dataCookie = new byte[37]; await data.GetStream().ReadExactlyAsync(dataCookie, token);
            Require(dataCookie.SequenceEqual(cookie), "Reverse data socket uses the control cookie");
            await stream.WriteAsync(new byte[] { 1, 2 }, token);
            await data.GetStream().WriteAsync(new byte[8192], token);
            var state = new byte[1]; await stream.ReadExactlyAsync(state, token);
            Require(state[0] == 4, "Client sends TEST_END while reverse I/O is idle");
            await data.GetStream().WriteAsync(new byte[8192], token);
            await Task.Delay(1100, token);
            await stream.WriteAsync(new byte[] { 13 }, token);
            using var local = await JsonAsync(stream, token);
            Require(local.RootElement.GetProperty("streams")[0].GetProperty("bytes").GetInt64() == 8192,
                "Result JSON excludes bytes sent after TEST_END");
            var json = Encoding.UTF8.GetBytes("{\"streams\":[{\"id\":1,\"bytes\":16384,\"start_time\":0,\"end_time\":2}]}");
            var header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, json.Length);
            await stream.WriteAsync(header, token); await stream.WriteAsync(json, token);
            await stream.WriteAsync(new byte[] { 14 }, token); await stream.ReadExactlyAsync(state, token);
            Require(state[0] == 16, "Reverse measurement completes the wire state sequence");
        }
    }
    private static async Task CheckStrayCookieAsync(int port, CancellationToken token)
    {
        using var control = new TcpClient(); await control.ConnectAsync(IPAddress.Loopback, port, token);
        var stream = control.GetStream(); var cookie = Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("D") + "\0");
        await stream.WriteAsync(cookie, token); var state = new byte[1]; await stream.ReadExactlyAsync(state, token);
        var parameters = Encoding.UTF8.GetBytes("{\"tcp\":true,\"time\":1,\"parallel\":1}"); var header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, parameters.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(parameters, token); await stream.ReadExactlyAsync(state, token);
        Require(state[0] == 10, "iperf3 server requests stream creation");
        using (var stray = new TcpClient())
        {
            await stray.ConnectAsync(IPAddress.Loopback, port, token); var wrong = (byte[])cookie.Clone(); wrong[0] ^= 1;
            await stray.GetStream().WriteAsync(wrong, token); await stray.GetStream().ReadExactlyAsync(state, token);
            Require(state[0] == 255, "Unrelated cookie is denied without aborting the valid control session");
        }
        using (var stalled = new TcpClient())
        {
            await stalled.ConnectAsync(IPAddress.Loopback, port, token);
            // Send no cookie: the per-candidate deadline must release this socket.
            var count = await stalled.GetStream().ReadAsync(new byte[1], token);
            Require(count == 0, "Stalled stream candidate times out without ending the valid session");
        }
        using var data = new TcpClient(); await data.ConnectAsync(IPAddress.Loopback, port, token); await data.GetStream().WriteAsync(cookie, token);
        var states = new byte[2]; await stream.ReadExactlyAsync(states, token); Require(states.SequenceEqual(new byte[] { 1, 2 }), "Valid stream starts after denied/stalled candidates");
        await data.GetStream().WriteAsync(new byte[4096], token);
        await Task.Delay(50, token); await stream.WriteAsync(new byte[] { 4 }, token); await stream.ReadExactlyAsync(state, token);
        var resultJson = Encoding.UTF8.GetBytes("{\"streams\":[{\"id\":1,\"bytes\":4096,\"start_time\":0,\"end_time\":0.05}]}"); BinaryPrimitives.WriteInt32BigEndian(header, resultJson.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(resultJson, token);
        using var peer = await JsonAsync(stream, token); await stream.ReadExactlyAsync(state, token);
        await stream.WriteAsync(new byte[] { 16 }, token);
    }
    private static async Task RunFixtureAsync(TcpListener listener, CancellationToken token)
    {
        using var control = await listener.AcceptTcpClientAsync(token); var stream = control.GetStream(); var cookie = new byte[37];
        await stream.ReadExactlyAsync(cookie, token); if (cookie[^1] != 0) throw new InvalidDataException();
        await stream.WriteAsync(new byte[] { 9 }, token);
        using var parameters = await JsonAsync(stream, token);
        if (!parameters.RootElement.GetProperty("tcp").GetBoolean() || parameters.RootElement.GetProperty("parallel").GetInt32() != 1) throw new InvalidDataException();
        await stream.WriteAsync(new byte[] { 10 }, token);
        using var data = await listener.AcceptTcpClientAsync(token); var dataStream = data.GetStream(); var dataCookie = new byte[37];
        await dataStream.ReadExactlyAsync(dataCookie, token); if (!dataCookie.SequenceEqual(cookie)) throw new InvalidDataException();
        await stream.WriteAsync(new byte[] { 1, 2 }, token);
        long bytes = 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var receive = Task.Run(async () =>
        {
            var buffer = new byte[65536];
            try { while (true) { var n = await dataStream.ReadAsync(buffer, stop.Token); if (n == 0) break; Interlocked.Add(ref bytes, n); } }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }, token);
        var state = new byte[1]; await stream.ReadExactlyAsync(state, token); if (state[0] != 4) throw new InvalidDataException();
        stop.Cancel(); await receive;
        await stream.WriteAsync(new byte[] { 13 }, token);
        using var local = await JsonAsync(stream, token);
        if (local.RootElement.GetProperty("streams")[0].GetProperty("id").GetInt32() != 1) throw new InvalidDataException();
        var json = Encoding.UTF8.GetBytes($"{{\"cpu_util_total\":0,\"cpu_util_user\":0,\"cpu_util_system\":0,\"sender_has_retransmits\":-1,\"streams\":[{{\"id\":1,\"bytes\":{bytes},\"retransmits\":-1,\"jitter\":0,\"errors\":0,\"packets\":0,\"start_time\":0,\"end_time\":1}}]}}");
        var header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, json.Length); await stream.WriteAsync(header, token); await stream.WriteAsync(json, token);
        await stream.WriteAsync(new byte[] { 14 }, token); await stream.ReadExactlyAsync(state, token); if (state[0] != 16) throw new InvalidDataException();
    }
    private static async Task<JsonDocument> JsonAsync(NetworkStream stream, CancellationToken token)
    { var header = new byte[4]; await stream.ReadExactlyAsync(header, token); var size = BinaryPrimitives.ReadInt32BigEndian(header); if (size is < 2 or > 65536) throw new InvalidDataException(); var json = new byte[size]; await stream.ReadExactlyAsync(json, token); return JsonDocument.Parse(json); }
    private static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    private static void Require(bool value, string label) { if (!value) throw new InvalidOperationException("FAIL " + label); Console.WriteLine("PASS " + label); }
    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T> { public void Report(T value) => callback(value); }
}
