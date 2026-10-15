using System.Text.Json;
using EmbedDesk.Core.Models;

namespace EmbedDesk.Infrastructure.Connections;

public enum SharedServiceKind { Serial, Tcp, Udp }
public sealed record SharedDeviceService
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = string.Empty;
    public SharedServiceKind Kind { get; init; }
    public string TargetHost { get; init; } = "127.0.0.1";
    public int TargetPort { get; init; } = 22;
    public string PortName { get; init; } = string.Empty;
    // Device metadata is supplied by the current scan for display, never persisted or sent to the relay.
    [System.Text.Json.Serialization.JsonIgnore]
    public string Description { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    public string Endpoint => Kind == SharedServiceKind.Serial ? PortName : $"{TargetHost}:{TargetPort}";
    public string ProtocolLabel => Kind switch
    {
        SharedServiceKind.Serial => "串口",
        SharedServiceKind.Udp => "UDP",
        _ => "TCP"
    };
    public string StatusLabel => Enabled ? "已发布" : "已停用";
    public void Validate()
    {
        if (Id is not { Length: 32 } || !Guid.TryParseExact(Id, "N", out _) || Name is not { Length: > 0 and <= 64 } || Name.Any(char.IsControl)) throw new ArgumentException("共享服务名称或编号无效。");
        if (!Enum.IsDefined(Kind)) throw new ArgumentException("共享服务类型无效。");
        if (Kind == SharedServiceKind.Serial && (PortName is not { Length: > 3 and <= 32 } || !PortName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || !PortName[3..].All(char.IsAsciiDigit) || !int.TryParse(PortName[3..], out var number) || number < 1)) throw new ArgumentException("请选择本地 COM。");
        if (Kind is SharedServiceKind.Tcp or SharedServiceKind.Udp && (string.IsNullOrWhiteSpace(TargetHost) || TargetHost.Length > 255 || TargetHost.Any(char.IsControl) || TargetPort is < 1 or > 65535)) throw new ArgumentException("目标地址或端口无效。");
    }
}

/// <summary>Runtime options. Secrets in this type must never be serialized to normal settings.</summary>
public sealed record DeviceNodeOptions
{
    public string ServerUrl { get; init; } = string.Empty;
    public string AdmissionKey { get; init; } = string.Empty;
    public string Credential { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public bool ManualApprovalOnly { get; init; }
    public bool PreferDirect { get; init; } = true;
    public int TimeoutSeconds { get; init; } = 10;
    public int RetrySeconds { get; init; } = 5;
    public string ProxyMode { get; init; } = "Direct";
    public string ProxyUrl { get; init; } = string.Empty;
    public string ProxyUser { get; init; } = string.Empty;
    public string ProxyPassword { get; init; } = string.Empty;
    public string TrustedRootFile { get; init; } = string.Empty;
}

public sealed record DevicePeerInfo(string SessionId, string DeviceId, bool IsOwner, string State, string Path, long SentBytes, long ReceivedBytes)
{
    public string Detail => $"{(IsOwner ? "访问本机" : "连接对方")} · {State} · {Path} · TX {SentBytes / 1024:N0} / RX {ReceivedBytes / 1024:N0} KiB";
}
public sealed record DeviceApproval(string SessionId, string DeviceId);
public sealed record DeviceRemoteService(string SessionId, string DeviceId, SharedDeviceService Service)
{
    public string DisplayName => $"{DeviceId} · {Service.ProtocolLabel} · {Service.Name}";
    public string PortName => $"NODE:{SessionId}:{Service.Id}";
}
public sealed record DevicePortMapping(string Id, string SessionId, string ServiceName, int LocalPort, long SentBytes = 0, long ReceivedBytes = 0, string Error = "", SharedServiceKind Kind = SharedServiceKind.Tcp, long DroppedDatagrams = 0)
{
    public string DisplayName => $"{(Kind == SharedServiceKind.Udp ? "UDP" : "TCP")} · 127.0.0.1:{LocalPort} → {ServiceName}";
}

internal sealed record NodeMessage(string Type, string? Session = null, string? From = null, string? To = null, JsonElement? Body = null, string? Error = null);
internal sealed record NodeHello(string RequestId, string Mode, byte[] PublicKey, byte[] Nonce);
internal sealed record NodeChallenge(string RequestId, byte[] PublicKey, byte[] Nonce, byte[] Proof, byte[]? ManualSecret);
internal sealed record NodeAccept(string[] Endpoints);
internal sealed record NodeRpc(string Type, string Channel = "", string Service = "", byte[]? Data = null,
    SerialPortSettings? Settings = null, SharedDeviceService[]? Services = null, string? Error = null, int DatagramLength = 0, int DatagramOffset = 0, string? Request = null);
