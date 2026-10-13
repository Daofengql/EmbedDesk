using EmbedDesk.Core.Models;

namespace EmbedDesk.App;

public sealed record NetworkConnectionPreset
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public TransportProtocol Protocol { get; init; }
    public TransportMode Mode { get; init; }
    public string LocalHost { get; init; } = "0.0.0.0";
    public int LocalPort { get; init; } = 9000;
    public string ClientLocalAddress { get; init; } = string.Empty;
    public string RemoteHost { get; init; } = "127.0.0.1";
    public int RemotePort { get; init; } = 9000;
    public bool AutoReconnect { get; init; }
    public bool TreatAsHttp { get; init; }
    public string MqttTopic { get; init; } = "embeddesk";
    public string MqttUsername { get; init; } = string.Empty;
    public bool MqttUseTls { get; init; }

    public TransportOptions ToOptions() => new()
    {
        Protocol = Protocol,
        Mode = Mode,
        LocalHost = LocalHost,
        LocalPort = LocalPort,
        ClientLocalAddress = string.IsNullOrWhiteSpace(ClientLocalAddress) ? null : ClientLocalAddress,
        RemoteHost = RemoteHost,
        RemotePort = RemotePort,
        AutoReconnect = AutoReconnect,
        TreatAsHttp = TreatAsHttp,
        MqttTopic = MqttTopic,
        MqttUsername = string.IsNullOrWhiteSpace(MqttUsername) ? null : MqttUsername,
        MqttUseTls = MqttUseTls
    };
}

public sealed record SerialBridgeClientPreset
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 9100;
}

public sealed record NetworkDisplayPreferences
{
    public bool HexDisplay { get; init; }
    public bool ShowTime { get; init; } = true;
    public bool ShowSent { get; init; } = true;
    public bool AutoScroll { get; init; } = true;
}
