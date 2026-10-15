using EmbedDesk.Core.Models;
using EmbedDesk.Infrastructure.Diagnostics;

namespace EmbedDesk.App;

internal static class DebugToolConnectionChoices
{
    internal static readonly KeyValuePair<DebugToolTransport, string>[] Types =
    [
        new(DebugToolTransport.Serial, "串口"),
        new(DebugToolTransport.TcpClient, "TCP/UDP 客户端"),
        new(DebugToolTransport.TcpServer, "TCP/UDP 服务端")
    ];

    internal static readonly KeyValuePair<TransportProtocol, string>[] Protocols =
    [
        new(TransportProtocol.Tcp, "TCP"),
        new(TransportProtocol.Udp, "UDP")
    ];

    internal static DebugToolTransport TypeFor(DebugToolTransport transport) => transport switch
    {
        DebugToolTransport.Serial => DebugToolTransport.Serial,
        DebugToolTransport.TcpClient or DebugToolTransport.UdpTarget => DebugToolTransport.TcpClient,
        _ => DebugToolTransport.TcpServer
    };

    internal static TransportProtocol ProtocolFor(DebugToolTransport transport) =>
        transport is DebugToolTransport.Udp or DebugToolTransport.UdpTarget or DebugToolTransport.UdpListen ? TransportProtocol.Udp : TransportProtocol.Tcp;

    internal static DebugToolTransport Resolve(DebugToolTransport type, TransportProtocol protocol) => (type, protocol) switch
    {
        (DebugToolTransport.Serial, _) => DebugToolTransport.Serial,
        (DebugToolTransport.TcpClient, TransportProtocol.Tcp) => DebugToolTransport.TcpClient,
        (DebugToolTransport.TcpServer, TransportProtocol.Tcp) => DebugToolTransport.TcpServer,
        (DebugToolTransport.TcpClient, TransportProtocol.Udp) => DebugToolTransport.UdpTarget,
        (DebugToolTransport.TcpServer, TransportProtocol.Udp) => DebugToolTransport.Udp,
        _ => throw new ArgumentException("请选择连接方式和协议。")
    };
}
