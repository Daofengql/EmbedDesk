using CommunityToolkit.Mvvm.ComponentModel;
using EmbedDesk.Core.Models;

namespace EmbedDesk.App.ViewModels;

public sealed class RemoteProfileEditorViewModel : ObservableObject
{
    public RemoteProfileEditorViewModel(RemoteConnectionProfile profile, IReadOnlyList<string>? keyPaths = null)
    {
        Profile = profile;
        KeyPaths = keyPaths ?? [];
    }

    public RemoteConnectionProfile Profile { get; }
    public IReadOnlyList<string> KeyPaths { get; }
    public string Name { get => Profile.Name; set { if (Profile.Name == value) return; Profile.Name = value; OnPropertyChanged(); } }
    public RemoteProtocol Protocol
    {
        get => Profile.Protocol;
        set
        {
            if (Profile.Protocol == value) return;
            var oldDefaultPort = DefaultPort(Profile.Protocol);
            Profile.Protocol = value;
            if (value == RemoteProtocol.S3) Profile.Host = string.Empty;
            else if (string.IsNullOrWhiteSpace(Profile.Host)) Profile.Host = "127.0.0.1";
            OnPropertyChanged(nameof(Host));
            if (Profile.Port == oldDefaultPort)
            {
                Profile.Port = DefaultPort(value);
                OnPropertyChanged(nameof(Port));
                OnPropertyChanged(nameof(Endpoint));
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProtocolLabel));
            OnPropertyChanged(nameof(IsSsh));
            OnPropertyChanged(nameof(IsVnc));
            OnPropertyChanged(nameof(IsS3));
            OnPropertyChanged(nameof(EndpointLabel));
            OnPropertyChanged(nameof(HasUsername));
            OnPropertyChanged(nameof(IsS3));
        }
    }
    public string Host { get => Profile.Host; set { if (Profile.Host == value) return; Profile.Host = value; OnPropertyChanged(); OnPropertyChanged(nameof(Endpoint)); } }
    public int Port { get => Profile.Port; set { if (Profile.Port == value) return; Profile.Port = value; OnPropertyChanged(); OnPropertyChanged(nameof(Endpoint)); } }
    public string Username { get => Profile.Username; set { if (Profile.Username == value) return; Profile.Username = value; OnPropertyChanged(); } }
    public string Password { get => Profile.Password; set { if (Profile.Password == value) return; Profile.Password = value; OnPropertyChanged(); } }
    public bool AcceptUnknownHostKey { get => Profile.AcceptUnknownHostKey; set { if (Profile.AcceptUnknownHostKey == value) return; Profile.AcceptUnknownHostKey = value; OnPropertyChanged(); } }
    public RemoteAuthenticationMode AuthenticationMode { get => Profile.AuthenticationMode; set { if (Profile.AuthenticationMode == value) return; Profile.AuthenticationMode = value; OnPropertyChanged(); } }
    public string PrivateKeyPath { get => Profile.PrivateKeyPath; set { if (Profile.PrivateKeyPath == value) return; Profile.PrivateKeyPath = value; OnPropertyChanged(); } }
    public string PrivateKeyPassphrase { get => Profile.PrivateKeyPassphrase; set { if (Profile.PrivateKeyPassphrase == value) return; Profile.PrivateKeyPassphrase = value; OnPropertyChanged(); } }
    public string S3Region { get => Profile.S3Region; set { if (Profile.S3Region == value) return; Profile.S3Region = value; OnPropertyChanged(); } }
    public string S3Bucket { get => Profile.S3Bucket; set { if (Profile.S3Bucket == value) return; Profile.S3Bucket = value; OnPropertyChanged(); } }
    public string S3AccessKeyId { get => Profile.S3AccessKeyId; set { if (Profile.S3AccessKeyId == value) return; Profile.S3AccessKeyId = value; OnPropertyChanged(); } }
    public string S3SecretKey { get => Profile.S3SecretAccessKey; set { if (Profile.S3SecretAccessKey == value) return; Profile.S3SecretAccessKey = value; OnPropertyChanged(); } }
    public bool RememberS3Credentials { get => Profile.RememberS3Credentials; set { if (Profile.RememberS3Credentials == value) return; Profile.RememberS3Credentials = value; OnPropertyChanged(); } }
    public string Endpoint => IsS3 ? (string.IsNullOrWhiteSpace(Host) ? $"AWS S3 · {S3Region}" : $"{Host} · {S3Region}") : $"{Host}:{Port}";
    public string EndpointLabel => IsS3 ? "服务端点" : "主机地址";
    public bool IsSsh => Protocol == RemoteProtocol.Ssh;
    public bool IsVnc => Protocol == RemoteProtocol.Vnc;
    public bool IsS3 => Protocol == RemoteProtocol.S3;
    public bool HasUsername => !IsVnc && !IsS3;
    public string ProtocolLabel => Protocol switch { RemoteProtocol.Ssh => "SSH", RemoteProtocol.Rdp => "RDP", RemoteProtocol.Vnc => "VNC", RemoteProtocol.S3 => "S3", _ => "Telnet" };
    public static int DefaultPort(RemoteProtocol protocol) => protocol switch { RemoteProtocol.Ssh => 22, RemoteProtocol.Rdp => 3389, RemoteProtocol.Vnc => 5900, RemoteProtocol.S3 => 443, _ => 23 };

    public void NotifyEndpointChanged()
    {
        OnPropertyChanged(nameof(Endpoint));
        OnPropertyChanged(nameof(ProtocolLabel));
            OnPropertyChanged(nameof(IsSsh));
            OnPropertyChanged(nameof(IsVnc));
        OnPropertyChanged(nameof(IsS3));
        OnPropertyChanged(nameof(EndpointLabel));
        OnPropertyChanged(nameof(HasUsername));
    }
}
