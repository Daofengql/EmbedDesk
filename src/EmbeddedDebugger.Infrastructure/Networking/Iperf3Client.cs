// TCP protocol and measurement lifecycle adapted from ESnet iperf 3.17.1.
// Copyright (c) 2014-2024, The Regents of the University of California,
// through Lawrence Berkeley National Laboratory. All rights reserved.
// BSD-style license: docs/third-party/iperf3-LICENSE.txt (also embedded in the app).
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmbeddedDebugger.Infrastructure.Networking;

public sealed record Iperf3Progress(double Seconds, long Bytes, double BitsPerSecond, bool Sender)
{
    public double IntervalSeconds { get; init; }
    public long IntervalBytes { get; init; }
}
public sealed record Iperf3Result(long LocalBytes, long PeerBytes, double LocalSeconds, double PeerSeconds, bool LocalSender)
{
    public double LocalBitsPerSecond => LocalBytes * 8d / Math.Max(LocalSeconds, .001);
    public double PeerBitsPerSecond => PeerBytes * 8d / Math.Max(PeerSeconds, .001);
}

/// <summary>Native iperf3 TCP control protocol: single stream, timed forward/reverse tests.</summary>
public static class Iperf3Client
{
    private const byte TestStart = 1, TestRunning = 2, TestEnd = 4, Parameters = 9, CreateStreams = 10, ExchangeResults = 13, DisplayResults = 14, Done = 16;
    private const int CookieBytes = 37, MaxJsonBytes = 65536;

    public static async Task<Iperf3Result> RunAsync(string host, int port, int seconds, bool reverse = false, IProgress<Iperf3Progress>? progress = null, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host); ValidatePort(port);
        if (seconds is < 1 or > 3600) throw new ArgumentOutOfRangeException(nameof(seconds), "时长应为 1–3600 秒。");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token); lifetime.CancelAfter(TimeSpan.FromSeconds(seconds + 30));
        try
        {
            using var control = NewClient(); await control.ConnectAsync(host, port, lifetime.Token).ConfigureAwait(false);
            var stream = control.GetStream();
            var cookie = Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("D") + "\0");
            await stream.WriteAsync(cookie, lifetime.Token).ConfigureAwait(false);
            await ExpectAsync(stream, Parameters, lifetime.Token).ConfigureAwait(false);
            var parameters = new Dictionary<string, object> { ["tcp"] = true, ["omit"] = 0, ["time"] = seconds, ["num"] = 0, ["blockcount"] = 0, ["parallel"] = 1, ["len"] = 131072, ["client_version"] = "EmbeddedDebugger native iperf3" };
            if (reverse) parameters["reverse"] = true;
            await WriteJsonAsync(stream, parameters, lifetime.Token).ConfigureAwait(false);
            await ExpectAsync(stream, CreateStreams, lifetime.Token).ConfigureAwait(false);
            using var data = NewClient(); await data.ConnectAsync(host, port, lifetime.Token).ConfigureAwait(false);
            await data.GetStream().WriteAsync(cookie, lifetime.Token).ConfigureAwait(false);
            await ExpectAsync(stream, TestStart, lifetime.Token).ConfigureAwait(false);
            await ExpectAsync(stream, TestRunning, lifetime.Token).ConfigureAwait(false);
            using var pump = new DataPump(data.GetStream(), !reverse, 131072, progress, lifetime.Token);
            var pumping = pump.RunAsync();
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
                await WriteJsonAsync(stream, Results(local.Bytes, local.Seconds, !reverse), lifetime.Token).ConfigureAwait(false);
                using var peerJson = await ReadJsonAsync(stream, lifetime.Token).ConfigureAwait(false);
                var peer = ParseResults(peerJson.RootElement, seconds);
                await ExpectAsync(stream, DisplayResults, lifetime.Token).ConfigureAwait(false);
                await WriteStateAsync(stream, Done, lifetime.Token).ConfigureAwait(false);
                return new(local.Bytes, peer.Bytes, local.Seconds, peer.Seconds, !reverse);
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
        { throw new TimeoutException("iperf3 握手或测试超时。请确认对端是 iperf3 服务端且防火墙允许 TCP 端口。"); }
    }

    public static async Task RunServerAsync(IPAddress address, int port, IProgress<Iperf3Progress>? progress = null, IProgress<string>? messages = null, IProgress<Iperf3Result>? results = null, CancellationToken token = default)
    {
        ValidatePort(port);
        var listener = new TcpListener(address, port);
        if (address.Equals(IPAddress.IPv6Any)) listener.Server.DualMode = true;
        listener.Start(8);
        try
        {
            messages?.Report($"iperf3 TCP 服务端正在监听 {address}:{port}（单连接流，支持正向 / 反向）。");
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
        await WriteStateAsync(stream, CreateStreams, ct).ConfigureAwait(false);
        TcpClient? data = null;
        try
        {
            // A different cookie belongs to another client; deny it instead of mixing test traffic.
            using var acceptDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            acceptDeadline.CancelAfter(TimeSpan.FromSeconds(10));
            while (data is null)
            {
                var candidate = await listener.AcceptTcpClientAsync(acceptDeadline.Token).ConfigureAwait(false);
                try
                {
                    using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(acceptDeadline.Token); readDeadline.CancelAfter(TimeSpan.FromSeconds(2));
                    var candidateCookie = new byte[CookieBytes]; await candidate.GetStream().ReadExactlyAsync(candidateCookie, readDeadline.Token).ConfigureAwait(false);
                    if (!CryptographicOperations.FixedTimeEquals(cookie, candidateCookie))
                    { await WriteStateAsync(candidate.GetStream(), 255, readDeadline.Token).ConfigureAwait(false); candidate.Dispose(); continue; }
                    data = candidate;
                }
                catch (OperationCanceledException) when (!acceptDeadline.IsCancellationRequested) { candidate.Dispose(); }
                catch (IOException) when (!acceptDeadline.IsCancellationRequested) { candidate.Dispose(); }
                catch { candidate.Dispose(); throw; }
            }
            await WriteStateAsync(stream, TestStart, ct).ConfigureAwait(false);
            await WriteStateAsync(stream, TestRunning, ct).ConfigureAwait(false);
            using var pump = new DataPump(data.GetStream(), settings.Reverse, settings.BlockBytes, progress, ct);
            var pumping = pump.RunAsync();
            try { await ExpectAsync(stream, TestEnd, ct).ConfigureAwait(false); }
            finally { pump.CompleteMeasurement(); pump.Stop(); await pumping.ConfigureAwait(false); }
            var local = pump.Snapshot;
            data.Dispose(); data = null;
            await WriteStateAsync(stream, ExchangeResults, ct).ConfigureAwait(false);
            using var peerJson = await ReadJsonAsync(stream, ct).ConfigureAwait(false);
            var peer = ParseResults(peerJson.RootElement, settings.Seconds);
            await WriteJsonAsync(stream, Results(local.Bytes, local.Seconds, settings.Reverse), ct).ConfigureAwait(false);
            await WriteStateAsync(stream, DisplayResults, ct).ConfigureAwait(false);
            await ExpectAsync(stream, Done, ct).ConfigureAwait(false);
            return new(local.Bytes, peer.Bytes, local.Seconds, peer.Seconds, settings.Reverse);
        }
        finally { data?.Dispose(); }
    }

    public static (int Seconds, int BlockBytes, bool Reverse) ValidateParameters(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object || !Flag(json, "tcp") || Flag(json, "udp") || Flag(json, "sctp") || Flag(json, "bidirectional"))
            throw new NotSupportedException("内置 iperf3 当前支持 TCP 单向 / 反向，不支持 UDP、SCTP 或双向同时测试。");
        var seconds = Integer(json, "time", 10); var block = Integer(json, "len", 131072);
        if (seconds is < 1 or > 3600 || block is < 1024 or > 1048576 || Integer(json, "parallel", 1) != 1 || Integer(json, "omit", 0) != 0 || Number(json, "num", 0) != 0 || Number(json, "blockcount", 0) != 0 || Number(json, "bandwidth", 0) != 0 || Number(json, "fqrate", 0) != 0 || Number(json, "MSS", 0) != 0 || Number(json, "window", 0) != 0)
            throw new NotSupportedException("内置 iperf3 使用 -P 1、-O 0、按时间测试（1–3600秒）、TCP不限速；块大小支持 1KiB–1MiB。");
        return (seconds, block, Flag(json, "reverse"));
    }
    private static bool Flag(JsonElement json, string key) => json.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
    private static int Integer(JsonElement json, string key, int fallback) => json.TryGetProperty(key, out var value) && value.TryGetInt32(out var n) ? n : json.TryGetProperty(key, out _) ? throw new InvalidDataException($"iperf3 {key} 参数格式无效。") : fallback;
    private static double Number(JsonElement json, string key, double fallback) => json.TryGetProperty(key, out var value) && value.TryGetDouble(out var n) && double.IsFinite(n) ? n : json.TryGetProperty(key, out _) ? throw new InvalidDataException($"iperf3 {key} 数值无效。") : fallback;
    private static object Results(long bytes, double seconds, bool sender) => new
    {
        cpu_util_total = 0, cpu_util_user = 0, cpu_util_system = 0, sender_has_retransmits = sender ? 0 : -1,
        streams = new[] { new { id = 1, bytes, retransmits = -1, jitter = 0, errors = 0, omitted_errors = 0, packets = 0, omitted_packets = 0, start_time = 0, end_time = seconds } }
    };
    private static (long Bytes, double Seconds) ParseResults(JsonElement json, double fallbackSeconds)
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
        return (bytes, elapsed);
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

    private sealed class DataPump : IDisposable
    {
        private readonly NetworkStream _stream;
        private readonly bool _sender;
        private readonly int _blockBytes;
        private readonly IProgress<Iperf3Progress>? _progress;
        private readonly CancellationTokenSource _stop;
        private readonly Stopwatch _watch = new();
        private readonly object _statisticsLock = new();
        private long _bytes;
        private long _previousBytes;
        private double _previousSeconds;
        private bool _measurementEnded;
        public (long Bytes, double Seconds) Snapshot
        { get { lock (_statisticsLock) return (_bytes, _watch.Elapsed.TotalSeconds); } }
        public DataPump(NetworkStream stream, bool sender, int blockBytes, IProgress<Iperf3Progress>? progress, CancellationToken token)
        { _stream = stream; _sender = sender; _blockBytes = blockBytes; _progress = progress; _stop = CancellationTokenSource.CreateLinkedTokenSource(token); }
        public async Task RunAsync()
        {
            var buffer = new byte[_blockBytes]; if (_sender) RandomNumberGenerator.Fill(buffer);
            _watch.Start();
            using var reportsStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            var reports = ReportIntervalsAsync(reportsStop.Token);
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    int transferred;
                    if (_sender) { await _stream.WriteAsync(buffer, _stop.Token).ConfigureAwait(false); transferred = buffer.Length; }
                    else { transferred = await _stream.ReadAsync(buffer, _stop.Token).ConfigureAwait(false); if (transferred == 0) break; }
                    lock (_statisticsLock)
                    {
                        // iperf_tcp_recv counts only data received in TEST_RUNNING.
                        if (!_measurementEnded) _bytes += transferred;
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (IOException) when (_stop.IsCancellationRequested) { }
            finally
            {
                CompleteMeasurement();
                reportsStop.Cancel();
                await reports.ConfigureAwait(false);
            }
        }
        private async Task ReportIntervalsAsync(CancellationToken token)
        {
            // Like iperf's stats timer, this must run even when socket I/O stalls.
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    lock (_statisticsLock)
                        if (!_measurementEnded) ReportInterval();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }
        private void ReportInterval()
        {
            // Port of iperf_stats_callback's byte delta / monotonic interval duration.
            var seconds = _watch.Elapsed.TotalSeconds;
            var duration = seconds - _previousSeconds;
            if (duration <= 0) return;
            var transferred = _bytes - _previousBytes;
            _progress?.Report(new(seconds, _bytes, transferred * 8d / duration, _sender)
                { IntervalSeconds = duration, IntervalBytes = transferred });
            _previousBytes = _bytes;
            _previousSeconds = seconds;
        }
        public void CompleteMeasurement()
        {
            lock (_statisticsLock)
            {
                if (_measurementEnded) return;
                _measurementEnded = true;
                _watch.Stop();
                ReportInterval();
            }
        }
        public void Stop() => _stop.Cancel();
        public void Dispose() { _stop.Cancel(); _stop.Dispose(); }
    }
}
