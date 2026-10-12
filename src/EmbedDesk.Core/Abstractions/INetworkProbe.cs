namespace EmbedDesk.Core.Abstractions;

public enum NetworkProbeOutcome { Success, NoReply, Unavailable }

public enum NetworkProbeAddressFamily { Any, IPv4, IPv6 }

public sealed record NetworkProbeTarget(
    string Host,
    int? TcpPort = null,
    NetworkProbeAddressFamily AddressFamily = NetworkProbeAddressFamily.Any,
    string? LocalAddress = null,
    int PacketSize = 32);
public sealed record NetworkProbeResult(NetworkProbeOutcome Outcome, double? Milliseconds, string Detail);

public interface INetworkProbe
{
    Task<NetworkProbeResult> ProbeAsync(NetworkProbeTarget target, CancellationToken cancellationToken);
}
