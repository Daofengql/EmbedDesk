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
    public bool IsPasswordAuthentication => !Editor.IsSsh || Editor.AuthenticationMode != RemoteAuthenticationMode.PrivateKey;
    public bool IsKeyAuthentication => Editor.IsSsh && Editor.AuthenticationMode == RemoteAuthenticationMode.PrivateKey;
    public bool IsSsh => Editor.IsSsh;
    public bool HasUsername => !Editor.IsVnc;
    public string ConnectionHint => Editor.IsVnc ? "标准 VNC 密码最多使用前 8 个字符；用户名不参与认证。" : Editor.Protocol == RemoteProtocol.Rdp ? "用户名可填写 域\\用户名；留空密码时使用 Windows 登录提示。" : "私钥口令和密码只用于当前会话，不会写入配置文件。";
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
            if (args.PropertyName is nameof(RemoteConnectionProfile.AuthenticationMode) or nameof(RemoteConnectionProfile.Protocol))
            {
                OnPropertyChanged(nameof(IsPasswordAuthentication));
                OnPropertyChanged(nameof(IsKeyAuthentication));
                OnPropertyChanged(nameof(IsSsh));
                OnPropertyChanged(nameof(HasUsername));
                OnPropertyChanged(nameof(ConnectionHint));
                UpdateProtocolLayout();
            }
        };
        PasswordEditor.Password = Editor.Password;
        KeyPassphraseEditor.Password = Editor.PrivateKeyPassphrase;
        UpdateProtocolLayout();
    }

    private void UpdateProtocolLayout() => Height = Editor.IsSsh ? 560 : Editor.IsVnc ? 450 : 500;

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
    private void Save_Click(object sender, RoutedEventArgs e) { if (!Validate()) return; Apply(); _workspace.SaveProfilesCommand.Execute(null); _committed = true; DialogResult = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) { if (_isNew) _workspace.RemoveProfile(_profile); DialogResult = false; }
    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!Validate()) return;
        Apply(); _workspace.SaveProfilesCommand.Execute(null);
        _committed = true; DialogResult = true;
        await _workspace.OpenProfileInNewTabAsync(_profile);
    }

    private bool Validate()
    {
        if (!string.IsNullOrWhiteSpace(Editor.Host) && int.TryParse(PortEditor.Text, out var port) && port is > 0 and <= 65535 && !string.IsNullOrWhiteSpace(Editor.Name)) { Editor.Port = port; return true; }
        ValidationMessage.Text = "请填写连接名称、主机地址和有效端口（1–65535）。";
        return false;
    }
    private void Apply()
    {
        _profile.Name = Editor.Name.Trim();
        _profile.Protocol = Editor.Protocol;
        _profile.Host = Editor.Host.Trim();
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
