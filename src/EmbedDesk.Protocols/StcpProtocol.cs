using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmbedDesk.Protocols;

public enum StcpTunnelRole
{
    Service,
    Visitor
}

public sealed record StcpTunnelDefinition
{
    public string Name { get; init; } = string.Empty;
    public StcpTunnelRole Role { get; init; }
    public string ServerAddress { get; init; } = string.Empty;
    public int ServerPort { get; init; } = 7000;
    public string Secret { get; init; } = string.Empty;
    /// <summary>Name of the service proxy this visitor is authorized to reach.</summary>
    public string ServiceName { get; init; } = string.Empty;
    public string TargetHost { get; init; } = "127.0.0.1";
    public int TargetPort { get; init; } = 22;
    public int LocalPort { get; init; } = 0;
    public int MaxBytesPerSecond { get; init; }
    public bool Enabled { get; init; } = true;
}

public sealed record StcpTunnelStatus(string Name, StcpTunnelRole Role, string State, string Detail, DateTimeOffset ChangedAt,
    long BytesSent, long BytesReceived, int? LocalPort);

public static class StcpProtocol
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Validate(StcpTunnelDefinition definition)
    {
        ValidateProfile(definition);
        if (definition.Secret.Length is < 16 or > 256) throw new ArgumentException("STCP 密钥长度应为 16–256 个字符。", nameof(definition));
    }

    /// <summary>Validates persisted, non-secret settings; a secret can be supplied later from secure storage.</summary>
    public static void ValidateProfile(StcpTunnelDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!Enum.IsDefined(definition.Role)) throw new ArgumentException("STCP 角色无效。", nameof(definition));
        if (definition.Name.Length is < 1 or > 64 || definition.Name.Any(char.IsControl)) throw new ArgumentException("STCP 名称长度应为 1–64 个可见字符。", nameof(definition));
        if (string.IsNullOrWhiteSpace(definition.ServerAddress) || definition.ServerAddress.Length > 255 || definition.ServerAddress.Any(char.IsControl)
            || definition.ServerPort is < 1 or > 65535) throw new ArgumentException("STCP 服务端地址或端口无效。", nameof(definition));
        if (definition.Secret.Length > 0 && definition.Secret.Length is < 16 or > 256) throw new ArgumentException("STCP 密钥长度应为 16–256 个字符。", nameof(definition));
        if (definition.LocalPort is < 0 or > 65535 || definition.TargetPort is < 1 or > 65535) throw new ArgumentException("STCP 目标或本地端口无效。", nameof(definition));
        if (definition.ServiceName.Length is < 1 or > 64 || definition.ServiceName.Any(char.IsControl))
            throw new ArgumentException("STCP 服务名称长度应为 1–64 个可见字符。", nameof(definition));
        if (definition.MaxBytesPerSecond is < 0 or > 1_000_000_000) throw new ArgumentException("STCP 限速必须在 0–1,000,000,000 字节 / 秒之间。", nameof(definition));
        if (definition.Role == StcpTunnelRole.Service)
        {
            if (string.IsNullOrWhiteSpace(definition.TargetHost) || definition.TargetHost.Length > 255 || definition.TargetHost.Any(char.IsControl))
                throw new ArgumentException("STCP 服务端必须指定有效的目标设备地址。", nameof(definition));
        }
        else if (definition.Role == StcpTunnelRole.Visitor)
        {
            if (definition.LocalPort is < 1 or > 65535) throw new ArgumentException("STCP 访问端本地监听端口必须为 1–65535。", nameof(definition));
        }
    }

    public static string Serialize(StcpTunnelDefinition definition)
    {
        Validate(definition);
        return JsonSerializer.Serialize(definition, JsonOptions);
    }

    public static StcpTunnelDefinition Deserialize(string json)
    {
        var definition = JsonSerializer.Deserialize<StcpTunnelDefinition>(json, JsonOptions) ?? throw new InvalidDataException("STCP 配置为空。");
        Validate(definition); return definition;
    }

    public static string DescribeBoundary() => "STCP 只为指定服务建立受授权的 visitor/service 转发，不提供网段路由或完整 VPN；服务端应验证密钥、租约、限速和撤销状态。";
}
