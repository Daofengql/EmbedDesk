using System.Windows;
using System.ComponentModel;
using System.IO;
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
    public bool IsPasswordAuthentication => !Editor.IsS3 && (!Editor.IsSsh || Editor.AuthenticationMode != RemoteAuthenticationMode.PrivateKey);
    public bool IsKeyAuthentication => Editor.IsSsh && Editor.AuthenticationMode == RemoteAuthenticationMode.PrivateKey;
    public bool IsSsh => Editor.IsSsh;
    public bool IsS3 => Editor.IsS3;
    public bool HasUsername => Editor.HasUsername;
    public string ConnectionHint => Editor.IsS3 ? "S3 凭据保存在当前 Windows 用户的凭据管理器中，不会写入连接配置。支持 AWS S3 与启用 HTTPS 的 S3 兼容端点。" : Editor.IsVnc ? "标准 VNC 密码最多使用前 8 个字符；用户名不参与认证。" : Editor.Protocol == RemoteProtocol.Rdp ? "用户名可填写 域\\用户名；留空密码时使用 Windows 登录提示。" : "私钥口令和密码只用于当前会话，不会写入配置文件。";
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
                OnPropertyChanged(nameof(IsS3));
                OnPropertyChanged(nameof(HasUsername));
                OnPropertyChanged(nameof(ConnectionHint));
            }
        };
        PasswordEditor.Password = Editor.Password;
        KeyPassphraseEditor.Password = Editor.PrivateKeyPassphrase;
        S3SecretEditor.Password = Editor.S3SecretKey;
        if (profile.RememberS3Credentials && !string.IsNullOrWhiteSpace(profile.S3CredentialTarget))
        {
            try { Editor.S3AccessKeyId = S3CredentialStore.Read(profile.S3CredentialTarget)?.AccessKeyId ?? string.Empty; }
            catch (System.ComponentModel.Win32Exception) { }
        }
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
    private void S3SecretEditor_Changed(object sender, RoutedEventArgs e) { if (Editor is not null) Editor.S3SecretKey = S3SecretEditor.Password; }
    private void ClearS3Credential_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_profile.S3CredentialTarget))
        {
            Editor.RememberS3Credentials = false;
            Editor.S3AccessKeyId = string.Empty;
            Editor.S3SecretKey = string.Empty;
            S3SecretEditor.Clear();
            return;
        }
        var confirmation = new ConfirmationWindow("清除 S3 凭据", "确定从 Windows 凭据管理器中删除此连接的 Access Key 和 Secret Key 吗？之后需要重新输入凭据才能连接。", "清除凭据") { Owner = this };
        if (confirmation.ShowDialog() != true) return;
        S3CredentialStore.Delete(_profile.S3CredentialTarget);
        _profile.S3CredentialTarget = string.Empty;
        Editor.RememberS3Credentials = false;
        Editor.S3AccessKeyId = string.Empty;
        Editor.S3SecretKey = string.Empty;
        _profile.S3AccessKeyId = string.Empty;
        _profile.S3SecretAccessKey = string.Empty;
        S3SecretEditor.Clear();
    }
    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "SSH 私钥|*.pem;*.key;id_*|所有文件|*.*" };
        if (dialog.ShowDialog(this) == true)
        {
            _workspace.AddSshKey(dialog.FileName);
            if (Editor is not null) Editor.PrivateKeyPath = dialog.FileName;
        }
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!Validate()) return;
        try { Apply(); _workspace.SaveProfilesCommand.Execute(null); _committed = true; DialogResult = true; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException or IOException) { ValidationMessage.Text = ex.Message; }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) { if (_isNew) _workspace.RemoveProfile(_profile); DialogResult = false; }
    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!Validate()) return;
        try { Apply(); _workspace.SaveProfilesCommand.Execute(null); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException or IOException) { ValidationMessage.Text = ex.Message; return; }
        _committed = true; DialogResult = true;
        await _workspace.OpenProfileInNewTabAsync(_profile);
    }

    private bool Validate()
    {
        if (Editor.IsS3)
        {
            if (string.IsNullOrWhiteSpace(Editor.S3Region) || string.IsNullOrWhiteSpace(Editor.Name))
            {
                ValidationMessage.Text = "请填写连接名称和 S3 区域。";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(Editor.Host)
                && (!Uri.TryCreate(Editor.Host.Trim(), UriKind.Absolute, out var endpoint)
                    || endpoint.Scheme is not ("https" or "http")
                    || endpoint.Scheme == "http" && !endpoint.IsLoopback))
            {
                ValidationMessage.Text = "S3 自定义端点必须是有效的 HTTP 或 HTTPS URL。";
                return false;
            }
            if (string.IsNullOrWhiteSpace(Editor.S3AccessKeyId))
            {
                ValidationMessage.Text = "请输入完整的 Access Key ID 和 Secret Key，或保留已保存的凭据。";
                return false;
            }
            if (string.IsNullOrWhiteSpace(Editor.S3SecretKey))
            {
                if (!Editor.RememberS3Credentials && string.IsNullOrEmpty(_profile.S3SecretAccessKey))
                {
                    try
                    {
                        var stored = string.IsNullOrWhiteSpace(_profile.S3CredentialTarget) ? null : S3CredentialStore.Read(_profile.S3CredentialTarget);
                        if (!string.Equals(stored?.AccessKeyId, Editor.S3AccessKeyId.Trim(), StringComparison.Ordinal))
                        {
                            ValidationMessage.Text = "当前未启用密钥保存，请输入 Secret Key。";
                            return false;
                        }
                    }
                    catch (System.ComponentModel.Win32Exception ex) { ValidationMessage.Text = ex.Message; return false; }
                }
                if (Editor.RememberS3Credentials)
                {
                    try
                    {
                        var credential = string.IsNullOrWhiteSpace(_profile.S3CredentialTarget) ? null : S3CredentialStore.Read(_profile.S3CredentialTarget);
                        if (!string.Equals(credential?.AccessKeyId, Editor.S3AccessKeyId.Trim(), StringComparison.Ordinal))
                        {
                            ValidationMessage.Text = "Access Key ID 已更改，请输入对应的 Secret Key。";
                            return false;
                        }
                    }
                    catch (System.ComponentModel.Win32Exception ex) { ValidationMessage.Text = ex.Message; return false; }
                }
            }
            return true;
        }
        if (!string.IsNullOrWhiteSpace(Editor.Host) && int.TryParse(PortEditor.Text, out var port) && port is > 0 and <= 65535 && !string.IsNullOrWhiteSpace(Editor.Name)) { Editor.Port = port; return true; }
        ValidationMessage.Text = "请填写连接名称、主机地址和有效端口（1–65535）。";
        Dispatcher.BeginInvoke(new Action(() => ValidationMessage.BringIntoView()));
        return false;
    }
    private void Apply()
    {
        if (Editor.IsS3)
        {
            var secret = Editor.S3SecretKey;
            if (Editor.RememberS3Credentials)
            {
                if (!string.IsNullOrWhiteSpace(secret))
                {
                    if (string.IsNullOrWhiteSpace(_profile.S3CredentialTarget)) _profile.S3CredentialTarget = $"EmbedDesk/S3/{Guid.NewGuid():N}";
                    S3CredentialStore.Save(_profile.S3CredentialTarget, Editor.S3AccessKeyId.Trim(), secret);
                }
                _profile.S3AccessKeyId = string.Empty;
                _profile.S3SecretAccessKey = string.Empty;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(secret) && !string.IsNullOrWhiteSpace(_profile.S3CredentialTarget))
                    secret = S3CredentialStore.Read(_profile.S3CredentialTarget)?.SecretAccessKey ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(_profile.S3CredentialTarget)) S3CredentialStore.Delete(_profile.S3CredentialTarget);
                _profile.S3CredentialTarget = string.Empty;
                _profile.S3AccessKeyId = Editor.S3AccessKeyId.Trim();
                _profile.S3SecretAccessKey = secret;
            }
            _profile.RememberS3Credentials = Editor.RememberS3Credentials;
            if (Editor.RememberS3Credentials) { Editor.S3SecretKey = string.Empty; S3SecretEditor.Clear(); }
        }
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
        _profile.S3Region = Editor.S3Region.Trim();
        _profile.S3Bucket = Editor.S3Bucket.Trim();
    }

    private static RemoteConnectionProfile Clone(RemoteConnectionProfile profile) => new()
    {
        Name = profile.Name, Protocol = profile.Protocol, Host = profile.Host, Port = profile.Port,
        Username = profile.Username, Password = profile.Password, AcceptUnknownHostKey = profile.AcceptUnknownHostKey,
        AuthenticationMode = profile.AuthenticationMode, PrivateKeyPath = profile.PrivateKeyPath,
        PrivateKeyPassphrase = profile.PrivateKeyPassphrase,
        S3CredentialTarget = profile.S3CredentialTarget,
        S3Region = profile.S3Region,
        S3Bucket = profile.S3Bucket,
        RememberS3Credentials = profile.RememberS3Credentials,
        S3AccessKeyId = profile.S3AccessKeyId,
        S3SecretAccessKey = profile.S3SecretAccessKey
    };
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private void Workspace_PropertyChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(RemoteDebugWorkspaceViewModel.Status)) OnPropertyChanged(nameof(Status)); }
    protected override void OnClosed(EventArgs e)
    {
        _workspace.PropertyChanged -= Workspace_PropertyChanged;
        if (!_committed) { Editor.S3SecretKey = string.Empty; Editor.Password = string.Empty; Editor.PrivateKeyPassphrase = string.Empty; }
        if (_isNew && !_committed) _workspace.RemoveProfile(_profile);
        base.OnClosed(e);
    }
}
