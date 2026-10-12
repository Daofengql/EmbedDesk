using System.Windows;
using System.ComponentModel;
using Microsoft.Win32;
using EmbedDesk.App.ViewModels;
using EmbedDesk.Core.Models;

namespace EmbedDesk.App;

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
        MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 32);
        Loaded += (_, _) => FitToScreen();
        SizeChanged += (_, _) => { if (IsLoaded) KeepOnScreen(); };
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
            }
        };
        PasswordEditor.Password = Editor.Password;
        KeyPassphraseEditor.Password = Editor.PrivateKeyPassphrase;
    }

    private Rect ScreenWorkArea()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var pixels = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var scale = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        return new Rect(pixels.Left / scale.DpiScaleX, pixels.Top / scale.DpiScaleY,
            pixels.Width / scale.DpiScaleX, pixels.Height / scale.DpiScaleY);
    }
    private void FitToScreen()
    {
        MaxHeight = Math.Max(MinHeight, ScreenWorkArea().Height - 32);
        KeepOnScreen();
    }
    private void KeepOnScreen()
    {
        var area = ScreenWorkArea();
        if (!double.IsNaN(Top)) Top = Math.Clamp(Top, area.Top + 16, Math.Max(area.Top + 16, area.Bottom - ActualHeight - 16));
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
        Dispatcher.BeginInvoke(new Action(() => ValidationMessage.BringIntoView()));
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
