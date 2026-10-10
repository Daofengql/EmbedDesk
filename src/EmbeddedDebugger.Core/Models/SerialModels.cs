namespace EmbeddedDebugger.Core.Models;

public sealed record SerialPortDescriptor(
    string PortName,
    string DisplayName,
    string? Description,
    string? Manufacturer);

public sealed class SerialPortSettings
{
    public string PortName { get; init; } = string.Empty;
    public int BaudRate { get; init; } = 115200;
    public int DataBits { get; init; } = 8;
    public System.IO.Ports.StopBits StopBits { get; init; } = System.IO.Ports.StopBits.One;
    public System.IO.Ports.Parity Parity { get; init; } = System.IO.Ports.Parity.None;
    public System.IO.Ports.Handshake Handshake { get; init; } = System.IO.Ports.Handshake.None;
    public bool DtrEnable { get; init; }
    public bool RtsEnable { get; init; }
    public int ReadBufferSize { get; init; } = 64 * 1024;
}

public enum SerialBridgeMode
{
    Server,
    Client
}

public sealed class SerialBridgeProfile
{
    public string Name { get; init; } = "串口网络映射";
    public SerialBridgeMode Mode { get; init; } = SerialBridgeMode.Server;
    public string LocalHost { get; init; } = "0.0.0.0";
    public int LocalPort { get; init; } = 9100;
    public string RemoteHost { get; init; } = "127.0.0.1";
    public int RemotePort { get; init; } = 9100;
    public IReadOnlyList<SerialPortSettings> Channels { get; init; } = Array.Empty<SerialPortSettings>();
    public bool EnableMultiplexing { get; init; } = true;
    public bool CreateVirtualPorts { get; init; }
}
