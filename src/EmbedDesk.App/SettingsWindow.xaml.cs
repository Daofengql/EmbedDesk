using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using EmbedDesk.App.ViewModels;
using EmbedDesk.Infrastructure.Connections;

namespace EmbedDesk.App;

public partial class SettingsWindow : Window
{
    private readonly MainWindowViewModel _model;
    internal ObservableCollection<RelayServerStatus> RelayStatuses { get; } = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Task> _probes = [];
    private readonly Dictionary<string, string> _relayTokens = new(StringComparer.Ordinal);
    private string? _editingId;
    private bool _refreshing;
    internal Task ProbeTask { get; private set; } = Task.CompletedTask;

    public SettingsWindow(MainWindowViewModel model)
    {
        InitializeComponent();
        _model = model;
        WindowTheme.ApplyDarkTitleBar(this);
        var p = model.Connections.Preferences;
        PreferDirect.IsChecked = p.PreferDirect;
        TimeoutSeconds.Text = p.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        RetrySeconds.Text = p.RetrySeconds.ToString(CultureInfo.InvariantCulture);
        ProxyMode.SelectedIndex = p.ProxyMode == "System" ? 1 : p.ProxyMode == "Custom" ? 2 : 0;
        ProxyUrl.Text = p.ProxyUrl; ProxyUser.Text = p.ProxyUser;
        ManualOnly.IsChecked = p.ManualApprovalOnly; StaticPassword.IsChecked = p.StaticPassword;
        TrustedRoot.Text = p.TrustedRootFile; CloseAction.SelectedIndex = (int)p.CloseAction;
        foreach (var profile in p.RelayServers)
        {
            var token = model.Connections.ReadRelayAdmissionToken(profile);
            _relayTokens[profile.Id] = token;
            RelayStatuses.Add(new RelayServerStatus(profile, token.Length > 0) { IsDefault = profile.Id == p.DefaultRelayServerId });
        }
        RelayServers.ItemsSource = RelayStatuses;
        RelayServers.SelectedItem = RelayStatuses.FirstOrDefault(status => status.IsDefault);
        Loaded += (_, _) => ProbeTask = RefreshRelayStatusesAsync();
        Closing += (_, _) => _lifetime.Cancel();
        Closed += async (_, _) => { _relayTokens.Clear(); RelayTokenInput.Clear(); await Task.WhenAll(_probes); _lifetime.Dispose(); };
    }

    private DeviceNodeOptions ProbeOptions(string url)
    {
        var p = _model.Connections.Preferences;
        return new DeviceNodeOptions
        {
            ServerUrl = url, ProxyMode = p.ProxyMode, ProxyUrl = p.ProxyUrl,
            ProxyUser = p.ProxyUser, ProxyPassword = _model.Connections.ReadSecret("proxy"), TrustedRootFile = p.TrustedRootFile
        };
    }

    private Task QueueProbe(RelayServerStatus status)
    {
        _probes.RemoveAll(task => task.IsCompleted);
        var task = ProbeRelayAsync(status); _probes.Add(task); return task;
    }

    private async Task RefreshRelayStatusesAsync()
    {
        if (_refreshing || _lifetime.IsCancellationRequested) return;
        _refreshing = true; RefreshRelaysButton.IsEnabled = false;
        try { await Task.WhenAll(RelayStatuses.Select(QueueProbe).ToArray()); }
        finally { _refreshing = false; RefreshRelaysButton.IsEnabled = true; }
    }

    private async Task ProbeRelayAsync(RelayServerStatus status)
    {
        status.State = "测速中…"; status.LatencyText = "—"; status.Error = string.Empty; status.IsAvailable = false;
        try
        {
            var result = await RelayHealthProbe.CheckAsync(ProbeOptions(status.Profile.Url), _lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;
            status.IsAvailable = result.Available;
            status.LatencyText = result.Available ? result.Milliseconds.ToString(CultureInfo.InvariantCulture) + " ms" : "—";
            status.State = result.Available ? "在线" : "不可用"; status.Error = result.Error;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException or IOException or InvalidOperationException)
        { status.State = "不可用"; status.Error = ex.Message; }
    }

    private void RefreshRelays_Click(object sender, RoutedEventArgs e) => ProbeTask = RefreshRelayStatusesAsync();
    private void AddRelay_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = RelayNameInput.Text.Trim();
            if (name.Length is < 1 or > 64 || name.Any(char.IsControl)) throw new ArgumentException("服务器名称长度应为 1–64 个字符。");
            if (!RelayServerProfile.TryNormalizeUrl(RelayUrlInput.Text, out var url))
                throw new ArgumentException("中继地址须为 HTTP 或 HTTPS URL，不能包含账号、查询参数或片段。");
            var token = RelayTokenInput.Password;
            if (!RelayConnectionInfo.IsValidToken(token)) throw new ArgumentException("准入 Token 最多 2048 个 ASCII 字符，不能包含空格或控制字符。");
            if (RelayStatuses.Any(status => status.Profile.Id != _editingId && string.Equals(status.Profile.Url, url, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("该中继地址已经存在。");
            if (_editingId is null && RelayStatuses.Count >= 64) throw new ArgumentException("最多保存 64 个中继服务器。");
            var existing = RelayStatuses.FirstOrDefault(status => status.Profile.Id == _editingId);
            var profile = new RelayServerProfile { Id = existing?.Profile.Id ?? Guid.NewGuid().ToString("N"), Name = name, Url = url };
            var next = new RelayServerStatus(profile, token.Length > 0) { IsDefault = existing?.IsDefault ?? RelayStatuses.Count == 0 };
            _relayTokens[profile.Id] = token;
            if (existing is not null) RelayStatuses[RelayStatuses.IndexOf(existing)] = next; else RelayStatuses.Add(next);
            RelayServers.SelectedItem = next; ResetEditor(); ErrorText.Text = string.Empty;
            ProbeTask = QueueProbe(next);
        }
        catch (ArgumentException ex) { ErrorText.Text = ex.Message; }
    }

    private void EditRelay_Click(object sender, RoutedEventArgs e)
    {
        if (RelayServers.SelectedItem is not RelayServerStatus { CanDelete: true } status) return;
        if (HasEditorInput) { ErrorText.Text = "请先添加、更新或重置上方编辑内容。"; return; }
        _editingId = status.Profile.Id; RelayNameInput.Text = status.Profile.Name; RelayUrlInput.Text = status.Profile.Url;
        RelayTokenInput.Password = _relayTokens[status.Profile.Id];
        AddRelayButton.Content = "更新"; RelayNameInput.Focus();
        RelayHint.Text = "正在编辑所选中继；清空 Token 后点击“更新”可移除准入凭据。";
    }

    private bool HasEditorInput => _editingId is not null || RelayNameInput.Text.Length > 0 || RelayUrlInput.Text.Length > 0 || RelayTokenInput.Password.Length > 0;
    private void ResetEditor()
    {
        _editingId = null; RelayNameInput.Clear(); RelayUrlInput.Clear(); RelayTokenInput.Clear(); AddRelayButton.Content = "添加";
        RelayHint.Text = "连接代码包含准入 Token，请仅交给获准接入的人。测速只检查 HTTP 响应与延迟。";
    }
    private void ResetRelay_Click(object sender, RoutedEventArgs e) { ResetEditor(); ErrorText.Text = string.Empty; }

    internal void ImportRelayCode(string code)
    {
        if (HasEditorInput) throw new ArgumentException("请先添加、更新或重置上方编辑内容，再导入连接代码。");
        var info = RelayConnectionInfo.Parse(code);
        var existing = RelayStatuses.FirstOrDefault(status => string.Equals(status.Profile.Url, info.Url, StringComparison.OrdinalIgnoreCase));
        if (existing is { CanDelete: false }) throw new ArgumentException("不能通过连接代码覆盖内置中继。");
        if (existing is null && RelayStatuses.Count >= 64) throw new ArgumentException("最多保存 64 个中继服务器。");
        _editingId = existing?.Profile.Id;
        RelayNameInput.Text = info.Name; RelayUrlInput.Text = info.Url; RelayTokenInput.Password = info.AdmissionToken;
        AddRelayButton.Content = existing is null ? "添加" : "更新";
        if (existing is not null) RelayServers.SelectedItem = existing;
        RelayHint.Text = existing is null ? "已读取连接代码，请核对名称和地址后点击“添加”，最后保存设置。"
            : "该地址已存在；请核对后点击“更新”替换其名称和 Token，最后保存设置。";
        ErrorText.Text = string.Empty; RelayNameInput.Focus();
    }
    private void ImportRelay_Click(object sender, RoutedEventArgs e)
    {
        try { ImportRelayCode(Clipboard.GetText()); }
        catch (COMException) { ErrorText.Text = "剪贴板正被占用，请重试。"; }
        catch (Exception ex) when (ex is FormatException or ArgumentException) { ErrorText.Text = ex.Message; }
    }
    internal string ExportRelayCode(RelayServerStatus status)
        => RelayConnectionInfo.Encode(status.Profile.Name, status.Profile.Url, _relayTokens[status.Profile.Id]);
    private void CopyRelay_Click(object sender, RoutedEventArgs e)
    {
        if (HasEditorInput) { ErrorText.Text = "请先添加、更新或重置上方编辑内容，再复制连接代码。"; return; }
        if (RelayServers.SelectedItem is not RelayServerStatus status) { ErrorText.Text = "请先选择要复制的中继。"; return; }
        try
        {
            Clipboard.SetText(ExportRelayCode(status)); ErrorText.Text = string.Empty;
            RelayHint.Text = "已复制所选中继的连接代码（含准入 Token），请仅交给获准接入的人。";
        }
        catch (COMException) { ErrorText.Text = "剪贴板正被占用，请重试。"; }
        catch (FormatException ex) { ErrorText.Text = ex.Message; }
    }

    private void SetDefaultRelay_Click(object sender, RoutedEventArgs e)
    {
        if (RelayServers.SelectedItem is not RelayServerStatus selected) return;
        foreach (var status in RelayStatuses) status.IsDefault = status == selected;
    }

    private void DeleteRelay_Click(object sender, RoutedEventArgs e)
    {
        if (RelayServers.SelectedItem is not RelayServerStatus { CanDelete: true } status) return;
        if (new ConfirmationWindow("删除中继服务器", $"删除“{status.Profile.Name}”？当前连接会继续保持。", "删除") { Owner = this }.ShowDialog() != true) return;
        RelayStatuses.Remove(status);
        _relayTokens.Remove(status.Profile.Id);
        if (status.IsDefault && RelayStatuses.Count > 0) RelayStatuses[0].IsDefault = true;
        if (_editingId == status.Profile.Id) ResetEditor();
        RelayServers.SelectedItem = RelayStatuses.FirstOrDefault(item => item.IsDefault);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (HasEditorInput)
                throw new ArgumentException("请先点击“添加”或“更新”保存上方服务器条目。");
            if (!int.TryParse(TimeoutSeconds.Text, out var timeout) || timeout is < 2 or > 120
                || !int.TryParse(RetrySeconds.Text, out var retry) || retry is < 1 or > 300)
                throw new ArgumentException("超时范围 2–120 秒，首次重连等待范围 1–300 秒。");
            if (ProxyMode.SelectedIndex == 2 && (!Uri.TryCreate(ProxyUrl.Text.Trim(), UriKind.Absolute, out var proxy)
                || proxy.UserInfo.Length > 0 || proxy.Scheme is not ("http" or "https" or "socks5")))
                throw new ArgumentException("请输入有效的代理 URL，口令请单独填写。");
            if (StaticPassword.IsChecked == true && ManualOnly.IsChecked != true
                && (ConnectionPassword.Password.Length > 0 ? ConnectionPassword.Password : _model.Connections.ReadSecret("password")).Length < 8)
                throw new ArgumentException("静态连接密码至少 8 个字符。");
            if (ConnectionPassword.Password.Length > 128) throw new ArgumentException("连接密码不能超过 128 个字符。");
            if (TrustedRoot.Text.Trim().Length > 0)
            { using var cert = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(File.ReadAllText(TrustedRoot.Text.Trim())); }
            var defaultStatus = RelayStatuses.FirstOrDefault(status => status.IsDefault) ?? RelayStatuses.FirstOrDefault();
            _model.Connections.SaveConnectionPreferences(new ConnectionPreferences
            {
                ServerUrl = defaultStatus?.Profile.Url ?? string.Empty,
                RelayServers = RelayStatuses.Select(status => status.Profile).ToList(),
                DefaultRelayServerId = defaultStatus?.Profile.Id ?? string.Empty,
                RelayTokensMigrated = true,
                PreferDirect = PreferDirect.IsChecked == true, TimeoutSeconds = timeout, RetrySeconds = retry,
                ProxyMode = ProxyMode.SelectedIndex == 1 ? "System" : ProxyMode.SelectedIndex == 2 ? "Custom" : "Direct",
                ProxyUrl = ProxyUrl.Text.Trim(), ProxyUser = ProxyUser.Text.Trim(),
                StaticPassword = StaticPassword.IsChecked == true, ManualApprovalOnly = ManualOnly.IsChecked == true,
                TrustedRootFile = TrustedRoot.Text.Trim(), CloseAction = (MainWindowCloseAction)Math.Clamp(CloseAction.SelectedIndex, 0, 2)
            }, _relayTokens, ConnectionPassword.Password.Length > 0 ? ConnectionPassword.Password : null,
                ClearProxy.IsChecked == true ? string.Empty : ProxyPassword.Password.Length > 0 ? ProxyPassword.Password : null);
            DialogResult = true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        { ErrorText.Text = ex.Message; }
    }
}
