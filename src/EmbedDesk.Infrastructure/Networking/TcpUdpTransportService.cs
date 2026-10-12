using EmbedDesk.Core.Abstractions;
using EmbedDesk.Core.Models;

namespace EmbedDesk.Infrastructure.Networking;

public sealed class TcpUdpTransportService : ITransportService
{
    public ITransportSession CreateSession(TransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Protocol == TransportProtocol.Mqtt ? new MqttSession(options) : new TcpUdpSession(options);
    }
}
