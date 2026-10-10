using System.Windows;
using System.ComponentModel;
using Microsoft.Win32;
using EmbeddedDebugger.App.ViewModels;
using EmbeddedDebugger.Core.Models;

namespace EmbeddedDebugger.App;

public partial class ConnectionProfileWindow : Window, INotifyPropertyChanged
{
    private readonly RemoteDebugWorkspaceViewModel _workspace;
    private readonly RemoteConnectionProfile _profile;
    private readonly bool _isNew;
    private readonly RemoteProfileEditorViewModel _editor;
    private bool _committed;
    public event PropertyChangedEventHandler? PropertyChanged;
    public RemoteProfileEditorViewModel Editor => _editor;
    public IReadOnlyList<RemoteProtocol> ProtocolOptions => _workspace.ProtocolOptions;
    public IReadOnlyList<RemoteAuthenticationMode> AuthenticationOptions => _workspace.AuthenticationOptions;
    public bool IsPasswordAuthentication => Editor.AuthenticationMode != RemoteAuthenticationMode.PrivateKey;
    public bool IsKeyAuthentication => Editor.AuthenticationMode == RemoteAuthenticationMode.PrivateKey;
    public string Status => _workspace.Status;

    public ConnectionProfileWindow(RemoteDebugWorkspaceViewModel workspace, RemoteConnectionProfile profile, bool isNew)
    {
        InitializeComponent();
        _workspace = workspace;
        _profile = profile;
        _isNew = isNew;
        _editor = new RemoteProfileEditorViewModel(Clone(profile), workspace.SshKeys);
        DataContext = this;
        _workspace.PropertyChanged += Workspace_PropertyChanged;
        Editor.Profile.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(RemoteConnectionProfile.AuthenticationMode))
            {
                OnPropertyChanged(nameof(IsPasswordAuthentication));
                OnPropertyChanged(nameof(IsKeyAuthentication));
            }
        };
        PasswordEditor.Password = Editor.Password;
        KeyPassphraseEditor.Password = Editor.PrivateKeyPassphrase;
    }

    private void PasswordEditor_Changed(object sender, RoutedEventArgs e) { if (Editor is not null) Editor.Password = PasswordEditor.Password; }
    private void KeyPassphraseEditor_Changed(object sender, RoutedEventArgs e) { if (Editor is not null) Editor.PrivateKeyPassphrase = KeyPassphraseEditor.Password; }
    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "SSH 私钥|*.pem;*.key;id_*|所有文件|*.*" };
        if (dialog.ShowDialog(this) == true)
        {
            _workspace.AddSshKey(dialog.FileName);
            if (Editor is not null) Editor.PrivateKeyPath = dialog.FileName;
        }
    }
    private void Save_Click(object sender, RoutedEventArgs e) { Apply(); _workspace.SaveProfilesCommand.Execute(null); _committed = true; DialogResult = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) { if (_isNew) _workspace.RemoveProfile(_profile); DialogResult = false; }
    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        Apply(); _workspace.SaveProfilesCommand.Execute(null);
        var before = _workspace.Sessions.Count;
        await _workspace.OpenProfileAsync(_profile);
        if (_workspace.Sessions.Count > before) { _committed = true; DialogResult = true; }
    }

    private void Apply()
    {
        _profile.Name = Editor.Name;
        _profile.Protocol = Editor.Protocol;
        _profile.Host = Editor.Host;
        _profile.Port = Editor.Port;
        _profile.Username = Editor.Username;
        _profile.Password = Editor.Password;
        _profile.AcceptUnknownHostKey = Editor.AcceptUnknownHostKey;
        _profile.AuthenticationMode = Editor.AuthenticationMode;
        _profile.PrivateKeyPath = Editor.PrivateKeyPath;
        _profile.PrivateKeyPassphrase = Editor.PrivateKeyPassphrase;
    }

    private static RemoteConnectionProfile Clone(RemoteConnectionProfile profile) => new()
    {
        Name = profile.Name, Protocol = profile.Protocol, Host = profile.Host, Port = profile.Port,
        Username = profile.Username, Password = profile.Password, AcceptUnknownHostKey = profile.AcceptUnknownHostKey,
        AuthenticationMode = profile.AuthenticationMode, PrivateKeyPath = profile.PrivateKeyPath,
        PrivateKeyPassphrase = profile.PrivateKeyPassphrase
    };
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private void Workspace_PropertyChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(RemoteDebugWorkspaceViewModel.Status)) OnPropertyChanged(nameof(Status)); }
    protected override void OnClosed(EventArgs e)
    {
        _workspace.PropertyChanged -= Workspace_PropertyChanged;
        if (_isNew && !_committed) _workspace.RemoveProfile(_profile);
        base.OnClosed(e);
    }
}
