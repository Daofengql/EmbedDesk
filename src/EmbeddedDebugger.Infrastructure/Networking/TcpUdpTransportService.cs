using EmbeddedDebugger.Core.Abstractions;
using EmbeddedDebugger.Core.Models;

namespace EmbeddedDebugger.Infrastructure.Networking;

public sealed class TcpUdpTransportService : ITransportService
{
    public ITransportSession CreateSession(TransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Protocol == TransportProtocol.Mqtt ? new MqttSession(options) : new TcpUdpSession(options);
    }
}
