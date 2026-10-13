using EmbedDesk.Protocols;

namespace EmbedDesk.App;

/// <summary>Persisted STCP settings. The shared key is stored separately in Windows Credential Manager.</summary>
public sealed record StcpTunnelProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public StcpTunnelRole Role { get; init; }
    public string ServerAddress { get; init; } = string.Empty;
    public int ServerPort { get; init; } = 7000;
    public string ServiceName { get; init; } = string.Empty;
    public string TargetHost { get; init; } = "127.0.0.1";
    public int TargetPort { get; init; } = 22;
    public int LocalPort { get; init; }
    public int MaxBytesPerSecond { get; init; }
    public bool Enabled { get; init; } = true;
    public string CredentialTarget { get; init; } = string.Empty;

    public StcpTunnelDefinition ToDefinition(string secret) => new()
    {
        Name = Name,
        Role = Role,
        ServerAddress = ServerAddress,
        ServerPort = ServerPort,
        Secret = secret,
        ServiceName = ServiceName,
        TargetHost = TargetHost,
        TargetPort = TargetPort,
        LocalPort = LocalPort,
        MaxBytesPerSecond = MaxBytesPerSecond,
        Enabled = Enabled
    };

    public void Validate() => StcpProtocol.ValidateProfile(ToDefinition(string.Empty));

    public static string CreateCredentialTarget(Guid id) => $"EmbedDesk/STCP/{id:N}";
}
