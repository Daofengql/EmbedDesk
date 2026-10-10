namespace EmbeddedDebugger.Core.Abstractions;

public enum NetworkProbeOutcome { Success, NoReply, Unavailable }

public sealed record NetworkProbeTarget(string Host, int? TcpPort = null);
public sealed record NetworkProbeResult(NetworkProbeOutcome Outcome, double? Milliseconds, string Detail);

public interface INetworkProbe
{
    Task<NetworkProbeResult> ProbeAsync(NetworkProbeTarget target, CancellationToken cancellationToken);
}
