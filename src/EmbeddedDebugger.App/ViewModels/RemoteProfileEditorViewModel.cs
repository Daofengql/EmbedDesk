using CommunityToolkit.Mvvm.ComponentModel;
using EmbeddedDebugger.Core.Models;

namespace EmbeddedDebugger.App.ViewModels;

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
            var oldDefaultPort = Profile.Protocol == RemoteProtocol.Ssh ? 22 : 23;
            Profile.Protocol = value;
            if (Profile.Port == oldDefaultPort)
            {
                Profile.Port = value == RemoteProtocol.Ssh ? 22 : 23;
                OnPropertyChanged(nameof(Port));
                OnPropertyChanged(nameof(Endpoint));
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProtocolLabel));
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
    public string Endpoint => $"{Host}:{Port}";
    public string ProtocolLabel => Protocol == RemoteProtocol.Ssh ? "SSH" : "Telnet";

    public void NotifyEndpointChanged()
    {
        OnPropertyChanged(nameof(Endpoint));
        OnPropertyChanged(nameof(ProtocolLabel));
    }
}
