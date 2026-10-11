namespace EmbeddedDebugger.Infrastructure.Networking;

public sealed record Iperf3Options(int Seconds = 10, bool Reverse = false, bool Udp = false,
    int BlockBytes = 0, long BitsPerSecond = 0, bool UdpCounters64Bit = true)
{
    public int EffectiveBlockBytes => BlockBytes == 0 ? (Udp ? 1200 : 131072) : BlockBytes;
    public void Validate()
    {
        if (Seconds is < 1 or > 3600) throw new ArgumentOutOfRangeException(nameof(Seconds), "时长应为 1–3600 秒。");
        var minimum = Udp ? (UdpCounters64Bit ? 16 : 12) : 1;
        var maximum = Udp ? 65507 : 1048576;
        if (EffectiveBlockBytes < minimum || EffectiveBlockBytes > maximum)
            throw new ArgumentOutOfRangeException(nameof(BlockBytes), $"{(Udp ? "UDP 报文" : "TCP 写入块")}大小应为 {minimum}–{maximum} 字节。");
        if (BitsPerSecond is < 0 or > 100_000_000_000)
            throw new ArgumentOutOfRangeException(nameof(BitsPerSecond), "带宽应为 0–100 Gbit/s，0 表示不限速。");
    }
}

public sealed record Iperf3Progress(double Seconds, long Bytes, double BitsPerSecond, bool Sender)
{
    public double IntervalSeconds { get; init; }
    public long IntervalBytes { get; init; }
    public Iperf3UdpStatistics? Udp { get; init; }
}

public sealed record Iperf3UdpStatistics(long Packets, long LostPackets, long? OutOfOrderPackets, double JitterMilliseconds)
{
    public double LossPercent => Packets == 0 ? 0 : LostPackets * 100d / Packets;
}

public sealed record Iperf3Result(long LocalBytes, long PeerBytes, double LocalSeconds, double PeerSeconds, bool LocalSender)
{
    public double LocalBitsPerSecond => LocalBytes * 8d / Math.Max(LocalSeconds, .001);
    public double PeerBitsPerSecond => PeerBytes * 8d / Math.Max(PeerSeconds, .001);
    public Iperf3UdpStatistics? LocalUdp { get; init; }
    public Iperf3UdpStatistics? PeerUdp { get; init; }
    public Iperf3UdpStatistics? ReceiverUdp => LocalSender ? PeerUdp : LocalUdp;
}

internal sealed record Iperf3Measurement(long Bytes, double Seconds, Iperf3UdpStatistics? Udp);
