using EmbedDesk.Core.Abstractions;

namespace EmbedDesk.App;

public enum NetworkProbeToolMode { Icmp, Tcping, Traceroute }

/// <summary>Last-used, non-secret values for the local Ping / TCPing / route trace tool.</summary>
public sealed record NetworkProbePreferences
{
    public string Host { get; init; } = "127.0.0.1";
    public NetworkProbeToolMode Mode { get; init; } = NetworkProbeToolMode.Icmp;
    public NetworkProbeAddressFamily AddressFamily { get; init; } = NetworkProbeAddressFamily.Any;
    public string LocalAddress { get; init; } = string.Empty;
    public int TcpPort { get; init; } = 22;
    public int PacketSize { get; init; } = 32;
    public int Count { get; init; }
    public int IntervalMilliseconds { get; init; } = 1000;
    public int MaxHops { get; init; } = 30;
    public int TimeoutMilliseconds { get; init; } = 1000;
    public bool ResolveNames { get; init; } = true;
}
