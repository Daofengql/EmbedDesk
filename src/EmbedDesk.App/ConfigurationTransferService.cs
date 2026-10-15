using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbedDesk.Core.Models;
using EmbedDesk.Protocols;

namespace EmbedDesk.App;

/// <summary>Portable, non-secret named connection presets. Runtime state and last-used targets are excluded.</summary>
internal static class ConfigurationTransferService
{
    public const int MaximumFileBytes = 1024 * 1024;
    private const string FormatName = "EmbedDesk.Configuration";
    private const int CurrentSchemaVersion = 2;
    private const int MaximumProfilesPerKind = 100;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static byte[] Export(
        IEnumerable<RemoteConnectionProfile> remoteProfiles,
        IEnumerable<NetworkConnectionPreset> networkPresets,
        IEnumerable<SerialBridgeClientPreset> serialBridgePresets,
        IEnumerable<StcpTunnelProfile>? stcpTunnels = null,
        IEnumerable<DebugConnectionPreset>? debugPresets = null)
    {
        ArgumentNullException.ThrowIfNull(remoteProfiles);
        ArgumentNullException.ThrowIfNull(networkPresets);
        ArgumentNullException.ThrowIfNull(serialBridgePresets);
        stcpTunnels ??= [];

        var profiles = remoteProfiles.Take(MaximumProfilesPerKind + 1).ToArray();
        var network = networkPresets.Take(MaximumProfilesPerKind + 1).ToArray();
        var serial = serialBridgePresets.Take(MaximumProfilesPerKind + 1).ToArray();
        var stcp = stcpTunnels.Take(MaximumProfilesPerKind + 1).ToArray();
        var debug = (debugPresets ?? []).Take(MaximumProfilesPerKind + 1).ToArray();
        if (profiles.Length > MaximumProfilesPerKind || network.Length > MaximumProfilesPerKind
            || serial.Length > MaximumProfilesPerKind || stcp.Length > MaximumProfilesPerKind || debug.Length > MaximumProfilesPerKind)
            throw new InvalidDataException("每类最多导出 100 个连接预设。");

        var package = new ConfigurationTransferDocument
        {
            Format = FormatName,
            SchemaVersion = CurrentSchemaVersion,
            ExportedAt = DateTimeOffset.UtcNow,
            RemoteConnections = profiles.Select(ToTransferProfile).ToList(),
            NetworkConnections = network.Select(ToTransferNetworkPreset).ToList(),
            SerialBridgeConnections = serial.Select(ToTransferBridgePreset).ToList(),
            StcpTunnels = stcp.Select(ToTransferStcpTunnel).ToList(),
            DebugConnections = debug.Select(TransferDebugPreset).ToList()
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(package, JsonOptions);
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("配置包超过 1 MiB 上限。");
        return bytes;
    }

    public static ImportedConfiguration Import(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaximumFileBytes) throw new InvalidDataException("配置文件为空或超过 1 MiB 上限。");
        ConfigurationTransferDocument document;
        try { document = JsonSerializer.Deserialize<ConfigurationTransferDocument>(bytes, JsonOptions) ?? throw new InvalidDataException("配置文件为空。"); }
        catch (JsonException exception) { throw new InvalidDataException($"配置文件格式无效：{exception.Message}", exception); }

        if (!string.Equals(document.Format, FormatName, StringComparison.Ordinal) || document.SchemaVersion is < 1 or > CurrentSchemaVersion)
            throw new InvalidDataException("配置文件格式或版本不受支持。");
        var remote = document.RemoteConnections ?? [];
        var network = document.NetworkConnections ?? [];
        var serial = document.SerialBridgeConnections ?? [];
        var stcp = document.StcpTunnels ?? [];
        var debug = document.DebugConnections ?? [];
        if (remote.Count > MaximumProfilesPerKind || network.Count > MaximumProfilesPerKind
            || serial.Count > MaximumProfilesPerKind || stcp.Count > MaximumProfilesPerKind || debug.Count > MaximumProfilesPerKind)
            throw new InvalidDataException("每类最多导入 100 个连接预设。");

        return new ImportedConfiguration(
            remote.Select(FromTransferProfile).ToArray(),
            network.Select(FromTransferNetworkPreset).ToArray(),
            serial.Select(FromTransferBridgePreset).ToArray(),
            stcp.Select(FromTransferStcpTunnel).ToArray())
        { DebugPresets = debug.Select(TransferDebugPreset).ToArray() };
    }

    private static DebugConnectionPreset TransferDebugPreset(DebugConnectionPreset preset)
    {
        if (preset is null) throw new InvalidDataException("调试连接预设不能为空。");
        try { DebugConnectionPreset.Validate(preset.Endpoint); }
        catch (ArgumentException ex) { throw new InvalidDataException("调试连接预设无效：" + ex.Message, ex); }
        return preset with { Id = Guid.NewGuid(), Name = NormalizeRequiredText(preset.Name, 64, "调试连接预设名称") };
    }

    private static TransferRemoteProfile ToTransferProfile(RemoteConnectionProfile profile)
    {
        if (profile is null || !Enum.IsDefined(profile.Protocol) || !Enum.IsDefined(profile.AuthenticationMode))
            throw new InvalidDataException("远程连接配置包含无效协议或认证模式。");
        var result = new TransferRemoteProfile
        {
            Name = NormalizeRequiredText(profile.Name, 64, "远程连接名称"),
            Protocol = profile.Protocol,
            Host = NormalizeText(profile.Host, 255, "连接地址", profile.Protocol == RemoteProtocol.S3),
            Port = profile.Port,
            Username = NormalizeText(profile.Username, 128, "用户名", true),
            AcceptUnknownHostKey = profile.AcceptUnknownHostKey,
            AuthenticationMode = profile.AuthenticationMode,
            S3Region = NormalizeText(profile.S3Region, 128, "S3 区域", profile.Protocol != RemoteProtocol.S3),
            S3Bucket = NormalizeText(profile.S3Bucket, 255, "S3 默认桶", true)
        };
        ValidateRemoteProfile(result);
        return result;
    }

    private static RemoteConnectionProfile FromTransferProfile(TransferRemoteProfile profile)
    {
        ValidateRemoteProfile(profile);
        return new RemoteConnectionProfile
        {
            Name = profile.Name.Trim(),
            Protocol = profile.Protocol,
            Host = profile.Host.Trim(),
            Port = profile.Port,
            Username = profile.Username.Trim(),
            Password = string.Empty,
            AcceptUnknownHostKey = profile.AcceptUnknownHostKey,
            AuthenticationMode = profile.AuthenticationMode,
            PrivateKeyPath = string.Empty,
            PrivateKeyPassphrase = string.Empty,
            S3CredentialTarget = string.Empty,
            S3Region = profile.S3Region.Trim().Length == 0 ? "us-east-1" : profile.S3Region.Trim(),
            S3Bucket = profile.S3Bucket.Trim(),
            RememberS3Credentials = false,
            S3AccessKeyId = string.Empty,
            S3SecretAccessKey = string.Empty
        };
    }

    private static TransferNetworkPreset ToTransferNetworkPreset(NetworkConnectionPreset preset)
    {
        var result = new TransferNetworkPreset
        {
            Name = NormalizeRequiredText(preset.Name, 64, "网络连接预设名称"),
            Protocol = preset.Protocol,
            Mode = preset.Mode,
            LocalHost = NormalizeText(preset.LocalHost, 255, "本地地址", false),
            LocalPort = preset.LocalPort,
            ClientLocalAddress = NormalizeText(preset.ClientLocalAddress, 64, "本机出口地址", true),
            RemoteHost = NormalizeText(preset.RemoteHost, 255, "远程地址", preset.Mode == TransportMode.Server),
            RemotePort = preset.RemotePort,
            AutoReconnect = preset.AutoReconnect,
            TreatAsHttp = preset.TreatAsHttp,
            MqttTopic = NormalizeText(preset.MqttTopic, 512, "MQTT 主题", preset.Protocol != TransportProtocol.Mqtt),
            MqttUsername = NormalizeText(preset.MqttUsername, 128, "MQTT 用户名", true),
            MqttUseTls = preset.MqttUseTls
        };
        ValidateNetworkPreset(result);
        return result;
    }

    private static NetworkConnectionPreset FromTransferNetworkPreset(TransferNetworkPreset preset)
    {
        ValidateNetworkPreset(preset);
        return new NetworkConnectionPreset
        {
            Id = Guid.NewGuid(),
            Name = preset.Name.Trim(),
            Protocol = preset.Protocol,
            Mode = preset.Mode,
            LocalHost = preset.LocalHost.Trim(),
            LocalPort = preset.LocalPort,
            ClientLocalAddress = preset.ClientLocalAddress.Trim(),
            RemoteHost = preset.RemoteHost.Trim(),
            RemotePort = preset.RemotePort,
            AutoReconnect = preset.AutoReconnect,
            TreatAsHttp = preset.TreatAsHttp,
            MqttTopic = preset.MqttTopic.Trim(),
            MqttUsername = preset.MqttUsername.Trim(),
            MqttUseTls = preset.MqttUseTls
        };
    }

    private static TransferSerialBridgePreset ToTransferBridgePreset(SerialBridgeClientPreset preset)
    {
        var result = new TransferSerialBridgePreset
        {
            Name = NormalizeRequiredText(preset.Name, 64, "网络串口预设名称"),
            Host = NormalizeRequiredText(preset.Host, 255, "网络串口服务地址"),
            Port = preset.Port
        };
        ValidateBridgePreset(result);
        return result;
    }

    private static SerialBridgeClientPreset FromTransferBridgePreset(TransferSerialBridgePreset preset)
    {
        ValidateBridgePreset(preset);
        return new SerialBridgeClientPreset { Id = Guid.NewGuid(), Name = preset.Name.Trim(), Host = preset.Host.Trim(), Port = preset.Port };
    }

    private static TransferStcpTunnel ToTransferStcpTunnel(StcpTunnelProfile profile)
    {
        try { profile.Validate(); }
        catch (ArgumentException exception) { throw new InvalidDataException($"STCP 配置无效：{exception.Message}", exception); }
        return new TransferStcpTunnel
        {
            Name = profile.Name,
            Role = profile.Role,
            ServerAddress = profile.ServerAddress,
            ServerPort = profile.ServerPort,
            ServiceName = profile.ServiceName,
            TargetHost = profile.TargetHost,
            TargetPort = profile.TargetPort,
            LocalPort = profile.LocalPort,
            MaxBytesPerSecond = profile.MaxBytesPerSecond,
            Enabled = profile.Enabled
        };
    }

    private static StcpTunnelProfile FromTransferStcpTunnel(TransferStcpTunnel profile)
    {
        var result = new StcpTunnelProfile
        {
            Id = Guid.NewGuid(),
            Name = NormalizeRequiredText(profile.Name, 64, "STCP 配置名称"),
            Role = profile.Role,
            ServerAddress = NormalizeRequiredText(profile.ServerAddress, 255, "STCP 服务端地址"),
            ServerPort = profile.ServerPort,
            ServiceName = NormalizeText(profile.ServiceName, 64, "STCP 服务名称", true),
            TargetHost = NormalizeText(profile.TargetHost, 255, "STCP 目标地址", true),
            TargetPort = profile.TargetPort,
            LocalPort = profile.LocalPort,
            MaxBytesPerSecond = profile.MaxBytesPerSecond,
            Enabled = profile.Enabled,
            CredentialTarget = string.Empty
        };
        try { result.Validate(); }
        catch (ArgumentException exception) { throw new InvalidDataException($"STCP 配置无效：{exception.Message}", exception); }
        return result;
    }

    private static void ValidateRemoteProfile(TransferRemoteProfile profile)
    {
        ValidateRequired(profile.Name, 64, "远程连接名称");
        if (!Enum.IsDefined(profile.Protocol) || !Enum.IsDefined(profile.AuthenticationMode))
            throw new InvalidDataException("远程连接配置包含无效协议或认证模式。");
        ValidateText(profile.Host, 255, "连接地址", profile.Protocol == RemoteProtocol.S3);
        ValidateText(profile.Username, 128, "用户名", true);
        ValidateText(profile.S3Region, 128, "S3 区域", profile.Protocol != RemoteProtocol.S3);
        ValidateText(profile.S3Bucket, 255, "S3 默认桶", true);
        if (profile.Port is < 1 or > 65535) throw new InvalidDataException("远程连接端口必须为 1–65535。");
        if (profile.Protocol == RemoteProtocol.S3 && profile.Host.Length > 0 && !IsSafeS3Endpoint(profile.Host))
            throw new InvalidDataException("S3 自定义端点必须使用 HTTPS；仅允许 HTTP 回环地址用于本机测试。");
    }

    private static void ValidateNetworkPreset(TransferNetworkPreset preset)
    {
        ValidateRequired(preset.Name, 64, "网络连接预设名称");
        if (!Enum.IsDefined(preset.Protocol) || !Enum.IsDefined(preset.Mode))
            throw new InvalidDataException("网络连接预设包含无效协议或模式。");
        ValidateRequired(preset.LocalHost, 255, "本地地址");
        ValidateText(preset.RemoteHost, 255, "远程地址", preset.Mode == TransportMode.Server);
        ValidateText(preset.ClientLocalAddress, 64, "本机出口地址", true);
        ValidateText(preset.MqttTopic, 512, "MQTT 主题", preset.Protocol != TransportProtocol.Mqtt);
        ValidateText(preset.MqttUsername, 128, "MQTT 用户名", true);
        if (preset.LocalPort is < 0 or > 65535 || preset.RemotePort is < 1 or > 65535)
            throw new InvalidDataException("网络连接预设端口超出范围。");
        if (preset.ClientLocalAddress.Length > 0 && !IPAddress.TryParse(preset.ClientLocalAddress, out _))
            throw new InvalidDataException("本机出口地址必须是 IP 地址。");
        if (preset.Protocol == TransportProtocol.Mqtt && preset.MqttTopic.Length == 0)
            throw new InvalidDataException("MQTT 预设必须包含主题。");
    }

    private static void ValidateBridgePreset(TransferSerialBridgePreset preset)
    {
        ValidateRequired(preset.Name, 64, "网络串口预设名称");
        ValidateRequired(preset.Host, 255, "网络串口服务地址");
        if (preset.Port is < 1 or > 65535) throw new InvalidDataException("网络串口服务端口必须为 1–65535。");
    }

    private static bool IsSafeS3Endpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0) return false;
        return uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address);
    }

    private static string NormalizeRequiredText(string? value, int maximumLength, string label)
    {
        var normalized = value?.Trim() ?? string.Empty;
        ValidateRequired(normalized, maximumLength, label);
        return normalized;
    }

    private static string NormalizeText(string? value, int maximumLength, string label, bool allowEmpty)
    {
        var normalized = value?.Trim() ?? string.Empty;
        ValidateText(normalized, maximumLength, label, allowEmpty);
        return normalized;
    }

    private static void ValidateRequired(string value, int maximumLength, string label) => ValidateText(value, maximumLength, label, false);

    private static void ValidateText(string value, int maximumLength, string label, bool allowEmpty)
    {
        if ((!allowEmpty && value.Length == 0) || value.Length > maximumLength || value.Any(char.IsControl))
            throw new InvalidDataException($"{label}为空、过长或包含控制字符。");
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true, MaxDepth = 16 };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }
}

internal sealed record ImportedConfiguration(
    IReadOnlyList<RemoteConnectionProfile> RemoteProfiles,
    IReadOnlyList<NetworkConnectionPreset> NetworkPresets,
    IReadOnlyList<SerialBridgeClientPreset> SerialBridgePresets,
    IReadOnlyList<StcpTunnelProfile> StcpTunnels)
{
    public IReadOnlyList<DebugConnectionPreset> DebugPresets { get; init; } = [];
    public ImportedConfiguration(IReadOnlyList<RemoteConnectionProfile> remoteProfiles,
        IReadOnlyList<NetworkConnectionPreset> networkPresets,
        IReadOnlyList<SerialBridgeClientPreset> serialBridgePresets)
        : this(remoteProfiles, networkPresets, serialBridgePresets, []) { }
}

internal readonly record struct ConfigurationMergeResult(int Added, int Skipped)
{
    public ConfigurationMergeResult Add(ConfigurationMergeResult other) => new(Added + other.Added, Skipped + other.Skipped);
}

internal sealed class ConfigurationTransferDocument
{
    public string Format { get; set; } = string.Empty;
    public int SchemaVersion { get; set; }
    public DateTimeOffset ExportedAt { get; set; }
    public List<TransferRemoteProfile>? RemoteConnections { get; set; } = [];
    public List<TransferNetworkPreset>? NetworkConnections { get; set; } = [];
    public List<TransferSerialBridgePreset>? SerialBridgeConnections { get; set; } = [];
    public List<TransferStcpTunnel>? StcpTunnels { get; set; } = [];
    public List<DebugConnectionPreset>? DebugConnections { get; set; } = [];
}

internal sealed class TransferRemoteProfile
{
    public string Name { get; set; } = string.Empty;
    public RemoteProtocol Protocol { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Username { get; set; } = string.Empty;
    public bool AcceptUnknownHostKey { get; set; }
    public RemoteAuthenticationMode AuthenticationMode { get; set; }
    public string S3Region { get; set; } = "us-east-1";
    public string S3Bucket { get; set; } = string.Empty;
}

internal sealed class TransferNetworkPreset
{
    public string Name { get; set; } = string.Empty;
    public TransportProtocol Protocol { get; set; }
    public TransportMode Mode { get; set; }
    public string LocalHost { get; set; } = "0.0.0.0";
    public int LocalPort { get; set; } = 9000;
    public string ClientLocalAddress { get; set; } = string.Empty;
    public string RemoteHost { get; set; } = "127.0.0.1";
    public int RemotePort { get; set; } = 9000;
    public bool AutoReconnect { get; set; }
    public bool TreatAsHttp { get; set; }
    public string MqttTopic { get; set; } = "embeddesk";
    public string MqttUsername { get; set; } = string.Empty;
    public bool MqttUseTls { get; set; }
}

internal sealed class TransferSerialBridgePreset
{
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 9100;
}

internal sealed class TransferStcpTunnel
{
    public string Name { get; set; } = string.Empty;
    public StcpTunnelRole Role { get; set; }
    public string ServerAddress { get; set; } = string.Empty;
    public int ServerPort { get; set; } = 7000;
    public string ServiceName { get; set; } = string.Empty;
    public string TargetHost { get; set; } = "127.0.0.1";
    public int TargetPort { get; set; } = 22;
    public int LocalPort { get; set; }
    public int MaxBytesPerSecond { get; set; }
    public bool Enabled { get; set; } = true;
}
