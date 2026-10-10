namespace EmbeddedDebugger.Core.Models;

public enum TransportProtocol
{
    Tcp,
    Udp,
    Mqtt
}

public enum TransportMode
{
    Client,
    Server
}

public enum TransportSessionState
{
    Created,
    Starting,
    Listening,
    Connecting,
    Connected,
    Stopping,
    Stopped,
    Faulted
}

public enum TransportEventKind
{
    StateChanged,
    Connected,
    Disconnected,
    Received,
    Sent,
    Error,
    Info
}

public sealed record TransportEndpoint(string Host, int Port)
{
    public override string ToString() => $"{Host}:{Port}";
}

public sealed class TransportOptions
{
    public TransportProtocol Protocol { get; init; } = TransportProtocol.Tcp;
    public TransportMode Mode { get; init; } = TransportMode.Client;
    public string LocalHost { get; init; } = "0.0.0.0";
    public int LocalPort { get; init; } = 9000;
    public string RemoteHost { get; init; } = "127.0.0.1";
    public int RemotePort { get; init; } = 9000;
    public int ReceiveBufferSize { get; init; } = 64 * 1024;
    public bool AutoReconnect { get; init; } = true;
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan? SendInterval { get; init; }
    public string? ScheduledPayload { get; init; }
    public bool TreatAsHttp { get; init; }
    public bool UseCrLf { get; init; } = true;
    public string MqttTopic { get; init; } = "embedded-debugger";
    public string MqttClientId { get; init; } = string.Empty;
    public string? MqttUsername { get; init; }
    public string? MqttPassword { get; init; }
    public bool MqttUseTls { get; init; }
}

public sealed record TransportMessage(
    DateTimeOffset Timestamp,
    string SessionId,
    TransportEventKind Kind,
    TransportSessionState State,
    string? RemoteEndPoint,
    ReadOnlyMemory<byte> Data,
    string? Text,
    string? Error);

public sealed record HttpHeader(string Name, string Value);

public sealed record HttpMessageInfo(
    string? StartLine,
    IReadOnlyList<HttpHeader> Headers,
    ReadOnlyMemory<byte> Body,
    bool IsRequest,
    string? Method,
    string? Path,
    string? Protocol,
    int? StatusCode);
