// TCP/UDP counters, jitter and test lifecycle adapted from ESnet iperf 3.17.1.
// Copyright (c) 2014-2024, The Regents of the University of California,
// through Lawrence Berkeley National Laboratory. All rights reserved.
// See docs/third-party/iperf3-LICENSE.txt for the BSD-style license.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace EmbedDesk.Infrastructure.Networking;

internal sealed class Iperf3DataPump : IDisposable
{
    private readonly Socket _socket;
    private readonly bool _sender;
    private readonly Iperf3Options _settings;
    private readonly IProgress<Iperf3Progress>? _progress;
    private readonly CancellationTokenSource _stop;
    private readonly Stopwatch _watch = new();
    private readonly object _statisticsLock = new();
    private long _bytes, _previousBytes, _packets, _lost, _outOfOrder;
    private double _previousSeconds, _previousTransit, _jitter;
    private bool _measurementEnded, _receivedPacket;

    public Iperf3DataPump(Socket socket, bool sender, Iperf3Options settings, IProgress<Iperf3Progress>? progress, CancellationToken token)
    { _socket = socket; _sender = sender; _settings = settings; _progress = progress; _stop = CancellationTokenSource.CreateLinkedTokenSource(token); }

    private Iperf3UdpStatistics? Udp => _settings.Udp ? new(_packets, _lost, _outOfOrder, _jitter * 1000) : null;
    public Iperf3Measurement Snapshot
    { get { lock (_statisticsLock) return new(_bytes, _watch.Elapsed.TotalSeconds, Udp); } }

    public async Task RunAsync()
    {
        var buffer = new byte[_settings.Udp && !_sender ? 65536 : _settings.EffectiveBlockBytes];
        if (_sender) RandomNumberGenerator.Fill(buffer);
        _watch.Start();
        using var reportsStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var reports = ReportIntervalsAsync(reportsStop.Token);
        long sentBytes = 0, sentPackets = 0;
        var synchronousBatch = 0;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                // Datagram sends may complete synchronously forever. Let timers,
                // cancellation and the control channel run even at unlimited rate.
                if (++synchronousBatch == 128) { synchronousBatch = 0; await Task.Yield(); }
                int transferred;
                if (_sender)
                {
                    await PaceAsync(sentBytes, _stop.Token).ConfigureAwait(false);
                    if (_settings.Udp) WriteUdpHeader(buffer, ++sentPackets);
                    transferred = await _socket.SendAsync(buffer, SocketFlags.None, _stop.Token).ConfigureAwait(false);
                    if (transferred == 0 || (_settings.Udp && transferred != buffer.Length)) throw new IOException("iperf3 数据发送未完成。");
                    sentBytes += transferred;
                }
                else
                {
                    transferred = await _socket.ReceiveAsync(buffer, SocketFlags.None, _stop.Token).ConfigureAwait(false);
                    if (transferred == 0 && !_settings.Udp) break;
                }
                lock (_statisticsLock)
                {
                    if (_measurementEnded) continue;
                    if (_settings.Udp)
                    {
                        if (_sender) _packets = sentPackets;
                        else if (!CountUdpPacket(buffer.AsSpan(0, transferred))) continue;
                    }
                    _bytes += transferred;
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
        finally
        {
            CompleteMeasurement(); reportsStop.Cancel(); await reports.ConfigureAwait(false);
        }
    }

    private async ValueTask PaceAsync(long sentBytes, CancellationToken token)
    {
        if (_settings.BitsPerSecond == 0) return;
        // iperf's application pacing compares sent bits with monotonic elapsed time.
        // Account partial TCP writes, include the UDP header, and avoid a busy loop.
        while (true)
        {
            var ahead = sentBytes * 8d / _settings.BitsPerSecond - _watch.Elapsed.TotalSeconds;
            if (ahead <= 0) return;
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(Math.Ceiling(ahead * 1000), 1, 1000)), token).ConfigureAwait(false);
        }
    }

    private void WriteUdpHeader(Span<byte> buffer, long sequence)
    {
        var now = (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)now);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[4..], (uint)((now - Math.Floor(now)) * 1_000_000));
        if (_settings.UdpCounters64Bit) BinaryPrimitives.WriteInt64BigEndian(buffer[8..], sequence);
        else
        {
            if (sequence > uint.MaxValue) throw new IOException("UDP 32 位包序号已用尽，请使用 --udp-counters-64bit。");
            BinaryPrimitives.WriteUInt32BigEndian(buffer[8..], (uint)sequence);
        }
    }

    private bool CountUdpPacket(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < (_settings.UdpCounters64Bit ? 16 : 12)) return false;
        var sequence = _settings.UdpCounters64Bit ? BinaryPrimitives.ReadInt64BigEndian(packet[8..]) : BinaryPrimitives.ReadUInt32BigEndian(packet[8..]);
        var microseconds = BinaryPrimitives.ReadUInt32BigEndian(packet[4..]);
        if (sequence <= 0 || microseconds >= 1_000_000) return false;
        // Match iperf_udp_recv: highest sequence estimates total packets; a late
        // packet repairs an earlier gap. Like upstream, duplicates are out-of-order.
        if (sequence > _packets) { _lost += sequence - _packets - 1; _packets = sequence; }
        else { _outOfOrder++; if (_lost > 0) _lost--; }
        var sent = BinaryPrimitives.ReadUInt32BigEndian(packet) + microseconds / 1_000_000d;
        var transit = (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency - sent;
        if (!_receivedPacket) { _previousTransit = transit; _receivedPacket = true; }
        _jitter += (Math.Abs(transit - _previousTransit) - _jitter) / 16d;
        _previousTransit = transit;
        return true;
    }

    private async Task ReportIntervalsAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                lock (_statisticsLock) if (!_measurementEnded) ReportInterval();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private void ReportInterval()
    {
        var seconds = _watch.Elapsed.TotalSeconds; var duration = seconds - _previousSeconds;
        if (duration <= 0) return;
        var transferred = _bytes - _previousBytes;
        _progress?.Report(new(seconds, _bytes, transferred * 8d / duration, _sender)
            { IntervalSeconds = duration, IntervalBytes = transferred, Udp = Udp });
        _previousBytes = _bytes; _previousSeconds = seconds;
    }
    public void CompleteMeasurement()
    {
        lock (_statisticsLock)
        {
            if (_measurementEnded) return;
            _measurementEnded = true; _watch.Stop(); ReportInterval();
        }
    }
    public void Stop() => _stop.Cancel();
    public void Dispose() { _stop.Cancel(); _stop.Dispose(); }
}
