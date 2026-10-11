// TCP/UDP protocol and measurement lifecycle adapted from ESnet iperf 3.17.1.
// Copyright (c) 2014-2024, The Regents of the University of California,
// through Lawrence Berkeley National Laboratory. All rights reserved.
// BSD-style license: docs/third-party/iperf3-LICENSE.txt (also embedded in the app).
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmbeddedDebugger.Infrastructure.Networking;

/// <summary>Managed iperf3 TCP/UDP: single stream, timed forward/reverse tests.</summary>
public static class Iperf3Client
{
    private const byte TestStart = 1, TestRunning = 2, TestEnd = 4, Parameters = 9, CreateStreams = 10, ExchangeResults = 13, DisplayResults = 14, Done = 16;
    private const int CookieBytes = 37, MaxJsonBytes = 65536;

    public static Task<Iperf3Result> RunAsync(string host, int port, int seconds, bool reverse = false, IProgress<Iperf3Progress>? progress = null, CancellationToken token = default)
        => RunAsync(host, port, new Iperf3Options(seconds, reverse), progress, token);

    public static async Task<Iperf3Result> RunAsync(string host, int port, Iperf3Options settings, IProgress<Iperf3Progress>? progress = null, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host); ValidatePort(port);
        settings.Validate(); var seconds = settings.Seconds; var reverse = settings.Reverse;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token); lifetime.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var control = NewClient(); await control.ConnectAsync(host, port, lifetime.Token).ConfigureAwait(false);
            var stream = control.GetStream();
            var cookie = Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("D") + "\0");
            await stream.WriteAsync(cookie, lifetime.Token).ConfigureAwait(false);
            await ExpectAsync(stream, Parameters, lifetime.Token).ConfigureAwait(false);
            var parameters = new Dictionary<string, object> { [settings.Udp ? "udp" : "tcp"] = true, ["omit"] = 0, ["time"] = seconds, ["num"] = 0, ["blockcount"] = 0, ["parallel"] = 1, ["len"] = settings.EffectiveBlockBytes, ["bandwidth"] = settings.BitsPerSecond, ["client_version"] = "EmbeddedDebugger managed iperf3" };
            if (reverse) parameters["reverse"] = true;
            if (settings.Udp && settings.UdpCounters64Bit) parameters["udp_counters_64bit"] = 1;
            await WriteJsonAsync(stream, parameters, lifetime.Token).ConfigureAwait(false);
            await ExpectAsync(stream, CreateStreams, lifetime.Token).ConfigureAwait(false);
            var endpoint = NormalizeEndpoint((IPEndPoint)control.Client.RemoteEndPoint!);
            using var data = new Socket(endpoint.AddressFamily, settings.Udp ? SocketType.Dgram : SocketType.Stream, settings.Udp ? ProtocolType.Udp : ProtocolType.Tcp);
            await data.ConnectAsync(endpoint, lifetime.Token).ConfigureAwait(false);
            if (settings.Udp) await ConnectUdpAsync(data, lifetime.Token).ConfigureAwait(false);
            else
            {
                using var dataStream = new NetworkStream(data, ownsSocket: false);
                await dataStream.WriteAsync(cookie, lifetime.Token).ConfigureAwait(false);
            }
            await ExpectAsync(stream, TestStart, lifetime.Token).ConfigureAwait(false);
            await ExpectAsync(stream, TestRunning, lifetime.Token).ConfigureAwait(false);
            lifetime.CancelAfter(TimeSpan.FromSeconds(seconds + 15));
            using var pump = new Iperf3DataPump(data, !reverse, settings, progress, lifetime.Token);
            var pumping = Task.Run(pump.RunAsync);
            using var phase = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            var response = ReadStateAsync(stream, phase.Token);
            var duration = Task.Delay(TimeSpan.FromSeconds(seconds), phase.Token);
            try
            {
                var early = await Task.WhenAny(pumping, response, duration).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                lifetime.Token.ThrowIfCancellationRequested();
                if (early == response) throw new IOException($"iperf3 测试被对端提前终止，状态 {await response.ConfigureAwait(false)}。");
                if (early == pumping)
                {
                    await pumping.ConfigureAwait(false);
                    throw new IOException("iperf3 数据连接在测试时长结束前关闭。");
                }
                await duration.ConfigureAwait(false);
                if (!reverse) { pump.Stop(); await pumping.ConfigureAwait(false); }
                // iperf_run_client takes its final stats before TEST_END. Keep draining a
                // reverse stream, but exclude late bytes/control latency from measurements.
                pump.CompleteMeasurement();
                await WriteStateAsync(stream, TestEnd, lifetime.Token).ConfigureAwait(false);
                var next = await response.ConfigureAwait(false);
                if (next != ExchangeResults) throw new InvalidDataException($"iperf3 结束阶段状态错误：{next}。");
                if (reverse) { pump.Stop(); await pumping.ConfigureAwait(false); }
                var local = pump.Snapshot;
                await WriteJsonAsync(stream, Results(local, !reverse), lifetime.Token).ConfigureAwait(false);
                using var peerJson = await ReadJsonAsync(stream, lifetime.Token).ConfigureAwait(false);
                var peer = ParseResults(peerJson.RootElement, seconds, settings.Udp);
                await ExpectAsync(stream, DisplayResults, lifetime.Token).ConfigureAwait(false);
                await WriteStateAsync(stream, Done, lifetime.Token).ConfigureAwait(false);
                return MakeResult(local, peer, !reverse);
            }
            finally
            {
                pump.Stop();
                phase.Cancel();
                try { await pumping.ConfigureAwait(false); } catch { /* Preserve the control-protocol failure. */ }
                try { await response.ConfigureAwait(false); } catch { /* Observe the cancelled control read too. */ }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("iperf3 握手或测试超时。请确认对端是 iperf3 服务端；UDP 测试需放行同一端口的 TCP 和 UDP。"); }
    }

    public static async Task RunServerAsync(IPAddress address, int port, IProgress<Iperf3Progress>? progress = null, IProgress<string>? messages = null, IProgress<Iperf3Result>? results = null, CancellationToken token = default)
    {
        ValidatePort(port);
        var listener = new TcpListener(address, port);
        if (address.Equals(IPAddress.IPv6Any)) listener.Server.DualMode = true;
        listener.Start(8);
        try
        {
            messages?.Report($"iperf3 服务端正在监听 {address}:{port}（TCP / UDP 单流，正向 / 反向，参数由客户端指定）。");
            while (true)
            {
                token.ThrowIfCancellationRequested();
                using var control = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                try
                {
                    messages?.Report($"控制连接：{control.Client.RemoteEndPoint}");
                    var result = await ServeAsync(listener, control, progress, token).ConfigureAwait(false);
                    results?.Report(result);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is IOException or InvalidDataException or SocketException or JsonException or InvalidOperationException or ArgumentException or NotSupportedException or OperationCanceledException)
                {
                    messages?.Report("测试结束 / 拒绝：" + (ex is OperationCanceledException ? "握手或控制连接超时" : ex.Message));
                    // SERVER_TERMINATE is understood by standard iperf3 clients.
                    try { using var send = new CancellationTokenSource(500); await WriteStateAsync(control.GetStream(), 11, send.Token).ConfigureAwait(false); } catch { }
                }
            }
        }
        finally { listener.Stop(); }
    }

    private static async Task<Iperf3Result> ServeAsync(TcpListener listener, TcpClient control, IProgress<Iperf3Progress>? progress, CancellationToken token)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(token); session.CancelAfter(TimeSpan.FromSeconds(15));
        var ct = session.Token; var stream = control.GetStream(); var cookie = new byte[CookieBytes];
        await stream.ReadExactlyAsync(cookie, ct).ConfigureAwait(false);
        if (cookie[^1] != 0 || cookie.AsSpan(0, 36).IndexOf((byte)0) >= 0) throw new InvalidDataException("iperf3 cookie 无效。");
        await WriteStateAsync(stream, Parameters, ct).ConfigureAwait(false);
        using var parameters = await ReadJsonAsync(stream, ct).ConfigureAwait(false);
        var settings = ValidateParameters(parameters.RootElement);
        session.CancelAfter(TimeSpan.FromSeconds(settings.Seconds + 20));
        TcpClient? tcpData = null;
        Socket? udpData = null;
        try
        {
            if (settings.Udp)
            {
                var localEndpoint = NormalizeEndpoint((IPEndPoint)control.Client.LocalEndPoint!);
                udpData = new Socket(localEndpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                udpData.Bind(localEndpoint);
            }
            await WriteStateAsync(stream, CreateStreams, ct).ConfigureAwait(false);
            // A different cookie belongs to another client; deny it instead of mixing test traffic.
            using var acceptDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            acceptDeadline.CancelAfter(TimeSpan.FromSeconds(10));
            if (udpData is not null)
                await AcceptUdpAsync(udpData, NormalizeEndpoint((IPEndPoint)control.Client.RemoteEndPoint!), acceptDeadline.Token).ConfigureAwait(false);
            while (udpData is null && tcpData is null)
            {
                var candidate = await listener.AcceptTcpClientAsync(acceptDeadline.Token).ConfigureAwait(false);
                try
                {
                    using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(acceptDeadline.Token); readDeadline.CancelAfter(TimeSpan.FromSeconds(2));
                    var candidateCookie = new byte[CookieBytes]; await candidate.GetStream().ReadExactlyAsync(candidateCookie, readDeadline.Token).ConfigureAwait(false);
                    if (!CryptographicOperations.FixedTimeEquals(cookie, candidateCookie))
                    { await WriteStateAsync(candidate.GetStream(), 255, readDeadline.Token).ConfigureAwait(false); candidate.Dispose(); continue; }
                    tcpData = candidate;
                }
                catch (OperationCanceledException) when (!acceptDeadline.IsCancellationRequested) { candidate.Dispose(); }
                catch (IOException) when (!acceptDeadline.IsCancellationRequested) { candidate.Dispose(); }
                catch { candidate.Dispose(); throw; }
            }
            await WriteStateAsync(stream, TestStart, ct).ConfigureAwait(false);
            await WriteStateAsync(stream, TestRunning, ct).ConfigureAwait(false);
            using var pump = new Iperf3DataPump(udpData ?? tcpData!.Client, settings.Reverse, settings, progress, ct);
            var pumping = Task.Run(pump.RunAsync);
            try { await ExpectAsync(stream, TestEnd, ct).ConfigureAwait(false); }
            finally { pump.CompleteMeasurement(); pump.Stop(); await pumping.ConfigureAwait(false); }
            var local = pump.Snapshot;
            tcpData?.Dispose(); tcpData = null; udpData?.Dispose(); udpData = null;
            await WriteStateAsync(stream, ExchangeResults, ct).ConfigureAwait(false);
            using var peerJson = await ReadJsonAsync(stream, ct).ConfigureAwait(false);
            var peer = ParseResults(peerJson.RootElement, settings.Seconds, settings.Udp);
            await WriteJsonAsync(stream, Results(local, settings.Reverse), ct).ConfigureAwait(false);
            await WriteStateAsync(stream, DisplayResults, ct).ConfigureAwait(false);
            await ExpectAsync(stream, Done, ct).ConfigureAwait(false);
            return MakeResult(local, peer, settings.Reverse);
        }
        finally { tcpData?.Dispose(); udpData?.Dispose(); }
    }

    public static Iperf3Options ValidateParameters(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object || Flag(json, "tcp") == Flag(json, "udp") || Flag(json, "sctp") || Flag(json, "bidirectional"))
            throw new NotSupportedException("内置 iperf3 支持 TCP / UDP 单向或反向，不支持 SCTP 或双向同时测试。");
        if (Integer(json, "parallel", 1) != 1 || Integer(json, "omit", 0) != 0 || Number(json, "num", 0) != 0 || Number(json, "blockcount", 0) != 0 || Number(json, "fqrate", 0) != 0 || Number(json, "MSS", 0) != 0 || Number(json, "window", 0) != 0 || Number(json, "burst", 0) != 0)
            throw new NotSupportedException("内置 iperf3 使用 -P 1、-O 0、按时间测试；支持 -u、-l、-b、-R，不支持分组突发或内核 socket 调优。");
        var udp = Flag(json, "udp"); var bandwidth = Number(json, "bandwidth", udp ? 1_000_000 : 0);
        if (bandwidth < 0 || bandwidth > 100_000_000_000 || Math.Truncate(bandwidth) != bandwidth)
            throw new NotSupportedException("iperf3 带宽应为 0–100 Gbit/s 的整数 bit/s。");
        var settings = new Iperf3Options(Integer(json, "time", 10), Flag(json, "reverse"), udp,
            Integer(json, "len", udp ? 1200 : 131072), (long)bandwidth, Flag(json, "udp_counters_64bit"));
        try { settings.Validate(); } catch (ArgumentOutOfRangeException ex) { throw new NotSupportedException(ex.Message, ex); }
        return settings;
    }
    private static bool Flag(JsonElement json, string key) => json.TryGetProperty(key, out var value) && (value.ValueKind == JsonValueKind.True || (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n != 0));
    private static int Integer(JsonElement json, string key, int fallback) => json.TryGetProperty(key, out var value) && value.TryGetInt32(out var n) ? n : json.TryGetProperty(key, out _) ? throw new InvalidDataException($"iperf3 {key} 参数格式无效。") : fallback;
    private static double Number(JsonElement json, string key, double fallback) => json.TryGetProperty(key, out var value) && value.TryGetDouble(out var n) && double.IsFinite(n) ? n : json.TryGetProperty(key, out _) ? throw new InvalidDataException($"iperf3 {key} 数值无效。") : fallback;
    private static object Results(Iperf3Measurement result, bool sender) => new
    {
        cpu_util_total = 0, cpu_util_user = 0, cpu_util_system = 0, sender_has_retransmits = sender ? 0 : -1,
        streams = new[] { new { id = 1, bytes = result.Bytes, retransmits = -1, jitter = (result.Udp?.JitterMilliseconds ?? 0) / 1000, errors = result.Udp?.LostPackets ?? 0, omitted_errors = 0, packets = result.Udp?.Packets ?? 0, omitted_packets = 0, out_of_order = result.Udp?.OutOfOrderPackets ?? 0, start_time = 0, end_time = result.Seconds } }
    };
    private static Iperf3Measurement ParseResults(JsonElement json, double fallbackSeconds, bool udp)
    {
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array || streams.GetArrayLength() != 1)
            throw new InvalidDataException("iperf3 对端统计数据无效。");
        var stream = streams[0];
        var count = Number(stream, "bytes", -1);
        if (Integer(stream, "id", -1) != 1 || count < 0 || count >= long.MaxValue || Math.Truncate(count) != count)
            throw new InvalidDataException("iperf3 对端字节计数无效。");
        var bytes = (long)count;
        var elapsed = Number(stream, "end_time", fallbackSeconds) - Number(stream, "start_time", 0);
        if (elapsed <= 0 || elapsed > 7200) throw new InvalidDataException("iperf3 对端统计时长无效。");
        Iperf3UdpStatistics? statistics = null;
        if (udp)
        {
            var packets = Count(stream, "packets"); var lost = Count(stream, "errors");
            var jitter = Number(stream, "jitter", 0);
            if (lost > packets || jitter < 0) throw new InvalidDataException("iperf3 对端 UDP 统计无效。");
            statistics = new(packets, lost, stream.TryGetProperty("out_of_order", out _) ? Count(stream, "out_of_order") : null, jitter * 1000);
        }
        return new(bytes, elapsed, statistics);
    }
    private static long Count(JsonElement json, string key)
    { var value = Number(json, key, 0); return value >= 0 && value < long.MaxValue && Math.Truncate(value) == value ? (long)value : throw new InvalidDataException($"iperf3 {key} 计数无效。"); }
    private static Iperf3Result MakeResult(Iperf3Measurement local, Iperf3Measurement peer, bool sender)
        => new(local.Bytes, peer.Bytes, local.Seconds, peer.Seconds, sender) { LocalUdp = local.Udp, PeerUdp = peer.Udp };
    private static IPEndPoint NormalizeEndpoint(IPEndPoint endpoint) => endpoint.Address.IsIPv4MappedToIPv6 ? new(endpoint.Address.MapToIPv4(), endpoint.Port) : endpoint;

    private static async Task ConnectUdpAsync(Socket socket, CancellationToken token)
    {
        // These four wire bytes are endian-independent in modern iperf3 (iperf.h).
        await socket.SendAsync(new byte[] { 0x39, 0x38, 0x37, 0x36 }, SocketFlags.None, token).ConfigureAwait(false);
        var buffer = new byte[65536];
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var count = await socket.ReceiveAsync(buffer, SocketFlags.None, token).ConfigureAwait(false);
            if (count == 4 && (buffer.AsSpan(0, 4).SequenceEqual(new byte[] { 0x36, 0x37, 0x38, 0x39 }) ||
                BinaryPrimitives.ReadUInt32LittleEndian(buffer) == 987654321 || BinaryPrimitives.ReadUInt32BigEndian(buffer) == 987654321)) return;
        }
        throw new InvalidDataException("iperf3 UDP 握手应答无效。");
    }
    private static async Task AcceptUdpAsync(Socket socket, IPEndPoint controlPeer, CancellationToken token)
    {
        var buffer = new byte[65536];
        EndPoint any = new IPEndPoint(socket.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);
        while (true)
        {
            var packet = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, token).ConfigureAwait(false);
            var peer = (IPEndPoint)packet.RemoteEndPoint;
            if (packet.ReceivedBytes != 4 || !peer.Address.MapToIPv6().Equals(controlPeer.Address.MapToIPv6())) continue;
            var magic = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
            if (magic != 0x36373839 && magic != 123456789 && BinaryPrimitives.ReadUInt32BigEndian(buffer) != 123456789) continue;
            socket.Connect(peer);
            await socket.SendAsync(new byte[] { 0x36, 0x37, 0x38, 0x39 }, SocketFlags.None, token).ConfigureAwait(false);
            return;
        }
    }
    private static TcpClient NewClient() => new() { NoDelay = true };
    private static void ValidatePort(int port) { if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port)); }
    private static async Task ExpectAsync(NetworkStream stream, byte expected, CancellationToken token)
    { var state = await ReadStateAsync(stream, token).ConfigureAwait(false); if (state != expected) throw new InvalidDataException($"iperf3 状态错误：期望 {expected}，收到 {state}。"); }
    private static async Task<byte> ReadStateAsync(NetworkStream stream, CancellationToken token)
    {
        var state = new byte[1]; await stream.ReadExactlyAsync(state, token).ConfigureAwait(false);
        if (state[0] == 255) throw new IOException("iperf3 服务端忙，已拒绝连接。");
        if (state[0] is 11 or 12) throw new IOException("iperf3 对端已终止测试。");
        if (state[0] == 254)
        {
            var error = new byte[8]; await stream.ReadExactlyAsync(error, token).ConfigureAwait(false);
            throw new IOException($"iperf3 服务端错误 {BinaryPrimitives.ReadInt32BigEndian(error)} / 系统错误 {BinaryPrimitives.ReadInt32BigEndian(error.AsSpan(4))}。");
        }
        return state[0];
    }
    private static ValueTask WriteStateAsync(NetworkStream stream, byte state, CancellationToken token) => stream.WriteAsync(new byte[] { state }, token);
    private static async Task WriteJsonAsync(NetworkStream stream, object value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value); var header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false); await stream.WriteAsync(bytes, token).ConfigureAwait(false);
    }
    private static async Task<JsonDocument> ReadJsonAsync(NetworkStream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is < 2 or > MaxJsonBytes) throw new InvalidDataException("iperf3 JSON 长度超过限制或无效。");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
    }

}
