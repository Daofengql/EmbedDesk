using System.IO.Ports;
using System.Net;
using EmbedDesk.Core.Models;
using EmbedDesk.Infrastructure.Diagnostics;

namespace EmbedDesk.App;

/// <summary>Communication parameters only. Each application creates a new live connection.</summary>
public sealed record DebugConnectionPreset
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public DebugToolEndpoint Endpoint { get; init; } = new(DebugToolTransport.Serial, "127.0.0.1", 9000, new());

    public static void Validate(DebugToolEndpoint endpoint, bool allowUnconfiguredSerial = false)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!Enum.IsDefined(endpoint.Transport)) throw new ArgumentException("无效的连接方式。");
        if (endpoint.Transport == DebugToolTransport.Serial)
        {
            var s = endpoint.Serial ?? throw new ArgumentException("请选择串口。");
            if ((!allowUnconfiguredSerial && string.IsNullOrWhiteSpace(s.PortName)) || s.PortName.Length > 256 || s.BaudRate is < 50 or > 12000000
                || s.DataBits is < 5 or > 8 || !Enum.IsDefined(s.Parity) || !Enum.IsDefined(s.Handshake)
                || s.StopBits == StopBits.None || !Enum.IsDefined(s.StopBits))
                throw new ArgumentException("串口设备或通信参数无效。");
            return;
        }
        if (endpoint.Host is not { Length: > 0 and <= 255 } || endpoint.Host.Any(char.IsWhiteSpace)
            || endpoint.Port is < 0 or > 65535 || endpoint.LocalPort is < 0 or > 65535
            || endpoint.TimeoutMilliseconds is < 100 or > 120000 || !IPAddress.TryParse(endpoint.LocalHost, out _))
            throw new ArgumentException("请检查连接地址、端口与超时（100–120000 ms）。");
        if (endpoint.Transport is DebugToolTransport.TcpClient or DebugToolTransport.UdpTarget)
        { if (endpoint.Port == 0) throw new ArgumentException("目标端口应为 1–65535。"); }
        else if (!IPAddress.TryParse(endpoint.Host, out _)) throw new ArgumentException("监听地址必须是本机 IP 地址。");
    }

    internal static DebugConnectionPreset FromNetwork(NetworkConnectionPreset preset) => new()
    {
        Id = preset.Id, Name = preset.Name, Endpoint = FromOptions(preset.ToOptions())
    };
    internal static DebugToolEndpoint FromOptions(TransportOptions options) => new(
        options.Protocol == TransportProtocol.Udp
            ? options.Mode == TransportMode.Client ? DebugToolTransport.UdpTarget : options.LockFirstUdpPeer ? DebugToolTransport.Udp : DebugToolTransport.UdpListen
            : options.Mode == TransportMode.Client ? DebugToolTransport.TcpClient : DebugToolTransport.TcpServer,
        options.Mode == TransportMode.Client ? options.RemoteHost : options.LocalHost,
        options.Mode == TransportMode.Client ? options.RemotePort : options.LocalPort)
    {
        LocalHost = string.IsNullOrEmpty(options.ClientLocalAddress) ? "0.0.0.0" : options.ClientLocalAddress,
        LocalPort = options.Mode == TransportMode.Client ? options.LocalPort : 0,
        TimeoutMilliseconds = options.ConnectTimeoutMilliseconds
    };

    internal static TransportOptions ToOptions(DebugToolEndpoint endpoint)
    {
        Validate(endpoint);
        var client = endpoint.Transport is DebugToolTransport.TcpClient or DebugToolTransport.UdpTarget;
        return new()
        {
            Protocol = endpoint.Transport is DebugToolTransport.TcpClient or DebugToolTransport.TcpServer ? TransportProtocol.Tcp : TransportProtocol.Udp,
            Mode = client ? TransportMode.Client : TransportMode.Server,
            LocalHost = client ? endpoint.LocalHost : endpoint.Host, LocalPort = client ? endpoint.LocalPort : endpoint.Port,
            RemoteHost = client ? endpoint.Host : "127.0.0.1", RemotePort = client ? endpoint.Port : 9000,
            ClientLocalAddress = endpoint.LocalHost == "0.0.0.0" ? null : endpoint.LocalHost,
            ConnectTimeoutMilliseconds = endpoint.TimeoutMilliseconds, LockFirstUdpPeer = endpoint.Transport == DebugToolTransport.Udp,
            AutoReconnect = false
        };
    }
}
