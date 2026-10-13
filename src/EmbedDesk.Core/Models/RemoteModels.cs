using System.ComponentModel;

namespace EmbedDesk.Core.Models;

public enum RemoteProtocol
{
    Ssh,
    Telnet,
    Rdp,
    Vnc,
    S3
}

public enum RemoteAuthenticationMode
{
    Password,
    PrivateKey
}

public enum RemoteSessionState
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting,
    Faulted
}

public sealed class RemoteConnectionProfile : INotifyPropertyChanged
{
    private string _name = "新建远程连接";
    private RemoteProtocol _protocol = RemoteProtocol.Ssh;
    private string _host = "127.0.0.1";
    private int _port = 22;
    private string _username = string.Empty;
    private string _password = string.Empty;
    private bool _acceptUnknownHostKey;
    private RemoteAuthenticationMode _authenticationMode = RemoteAuthenticationMode.Password;
    private string _privateKeyPath = string.Empty;
    private string _privateKeyPassphrase = string.Empty;
    private string _s3CredentialTarget = string.Empty;
    private string _s3Region = "us-east-1";
    private string _s3Bucket = string.Empty;
    private bool _rememberS3Credentials;
    private string _s3AccessKeyId = string.Empty;
    private string _s3SecretAccessKey = string.Empty;

    public string Name { get => _name; set => Set(ref _name, value); }
    public RemoteProtocol Protocol { get => _protocol; set => Set(ref _protocol, value); }
    public string Host { get => _host; set => Set(ref _host, value); }
    public int Port { get => _port; set => Set(ref _port, value); }
    public string Username { get => _username; set => Set(ref _username, value); }
    public string Password { get => _password; set => Set(ref _password, value); }
    public bool AcceptUnknownHostKey { get => _acceptUnknownHostKey; set => Set(ref _acceptUnknownHostKey, value); }
    public RemoteAuthenticationMode AuthenticationMode { get => _authenticationMode; set => Set(ref _authenticationMode, value); }
    public string PrivateKeyPath { get => _privateKeyPath; set => Set(ref _privateKeyPath, value); }
    public string S3CredentialTarget { get => _s3CredentialTarget; set => Set(ref _s3CredentialTarget, value); }
    public string S3Region { get => _s3Region; set => Set(ref _s3Region, value); }
    public string S3Bucket { get => _s3Bucket; set => Set(ref _s3Bucket, value); }
    [System.Text.Json.Serialization.JsonIgnore]
    public string EndpointSummary => Protocol == RemoteProtocol.S3
        ? string.IsNullOrWhiteSpace(Host) ? $"AWS S3 · {S3Region}" : $"{Host} · {S3Region}"
        : $"{Host}:{Port}";
    public bool RememberS3Credentials { get => _rememberS3Credentials; set => Set(ref _rememberS3Credentials, value); }
    [System.Text.Json.Serialization.JsonIgnore]
    public string S3AccessKeyId { get => _s3AccessKeyId; set => Set(ref _s3AccessKeyId, value); }
    [System.Text.Json.Serialization.JsonIgnore]
    public string S3SecretAccessKey { get => _s3SecretAccessKey; set => Set(ref _s3SecretAccessKey, value); }
    [System.Text.Json.Serialization.JsonIgnore]
    public string PrivateKeyPassphrase { get => _privateKeyPassphrase; set => Set(ref _privateKeyPassphrase, value); }
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName is nameof(Protocol) or nameof(Host) or nameof(Port) or nameof(S3Region))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EndpointSummary)));
    }
}

public sealed record RemoteDirectoryEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Size,
    DateTimeOffset? Modified,
    string Permissions);

public sealed record RemotePerformanceSnapshot(
    DateTimeOffset Timestamp,
    double? CpuPercent,
    double? MemoryPercent,
    double? LoadAverage,
    long? ReceiveBytes,
    long? TransmitBytes,
    string? Uptime,
    string? Source)
{
    public int? ProcessorCount { get; init; }
}
