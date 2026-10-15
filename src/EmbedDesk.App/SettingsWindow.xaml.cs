using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
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
            RelayStatuses.Add(new RelayServerStatus(profile) { IsDefault = profile.Id == p.DefaultRelayServerId });
        RelayServers.ItemsSource = RelayStatuses;
        RelayServers.SelectedItem = RelayStatuses.FirstOrDefault(status => status.IsDefault);
        Loaded += (_, _) => ProbeTask = RefreshRelayStatusesAsync();
        Closing += (_, _) => _lifetime.Cancel();
        Closed += async (_, _) => { await Task.WhenAll(_probes); _lifetime.Dispose(); };
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
            if (RelayStatuses.Any(status => status.Profile.Id != _editingId && string.Equals(status.Profile.Url, url, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("该中继地址已经存在。");
            if (_editingId is null && RelayStatuses.Count >= 64) throw new ArgumentException("最多保存 64 个中继服务器。");
            var existing = RelayStatuses.FirstOrDefault(status => status.Profile.Id == _editingId);
            var profile = new RelayServerProfile { Id = existing?.Profile.Id ?? Guid.NewGuid().ToString("N"), Name = name, Url = url };
            var next = new RelayServerStatus(profile) { IsDefault = existing?.IsDefault ?? RelayStatuses.Count == 0 };
            if (existing is not null) RelayStatuses[RelayStatuses.IndexOf(existing)] = next; else RelayStatuses.Add(next);
            RelayServers.SelectedItem = next; ResetEditor(); ErrorText.Text = string.Empty;
            ProbeTask = QueueProbe(next);
        }
        catch (ArgumentException ex) { ErrorText.Text = ex.Message; }
    }

    private void EditRelay_Click(object sender, RoutedEventArgs e)
    {
        if (RelayServers.SelectedItem is not RelayServerStatus { CanDelete: true } status) return;
        _editingId = status.Profile.Id; RelayNameInput.Text = status.Profile.Name; RelayUrlInput.Text = status.Profile.Url;
        AddRelayButton.Content = "更新"; RelayNameInput.Focus();
    }

    private void ResetEditor()
    { _editingId = null; RelayNameInput.Clear(); RelayUrlInput.Clear(); AddRelayButton.Content = "添加"; }

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
        if (status.IsDefault && RelayStatuses.Count > 0) RelayStatuses[0].IsDefault = true;
        if (_editingId == status.Profile.Id) ResetEditor();
        RelayServers.SelectedItem = RelayStatuses.FirstOrDefault(item => item.IsDefault);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RelayNameInput.Text.Trim().Length > 0 || RelayUrlInput.Text.Trim().Length > 0)
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
            if (ConnectionPassword.Password.Length > 0) _model.Connections.SaveSecret("password", ConnectionPassword.Password);
            if (ClearAdmission.IsChecked == true || AdmissionKey.Password.Length > 0)
                _model.Connections.SaveSecret("admission", ClearAdmission.IsChecked == true ? "" : AdmissionKey.Password);
            if (ClearProxy.IsChecked == true || ProxyPassword.Password.Length > 0)
                _model.Connections.SaveSecret("proxy", ClearProxy.IsChecked == true ? "" : ProxyPassword.Password);
            var defaultStatus = RelayStatuses.FirstOrDefault(status => status.IsDefault) ?? RelayStatuses.FirstOrDefault();
            _model.AppSettings.Update(s => s.ConnectionPreferences = new ConnectionPreferences
            {
                ServerUrl = defaultStatus?.Profile.Url ?? string.Empty,
                RelayServers = RelayStatuses.Select(status => status.Profile).ToList(),
                DefaultRelayServerId = defaultStatus?.Profile.Id ?? string.Empty,
                PreferDirect = PreferDirect.IsChecked == true, TimeoutSeconds = timeout, RetrySeconds = retry,
                ProxyMode = ProxyMode.SelectedIndex == 1 ? "System" : ProxyMode.SelectedIndex == 2 ? "Custom" : "Direct",
                ProxyUrl = ProxyUrl.Text.Trim(), ProxyUser = ProxyUser.Text.Trim(),
                StaticPassword = StaticPassword.IsChecked == true, ManualApprovalOnly = ManualOnly.IsChecked == true,
                TrustedRootFile = TrustedRoot.Text.Trim(), CloseAction = (MainWindowCloseAction)Math.Clamp(CloseAction.SelectedIndex, 0, 2)
            });
            _model.AppSettings.SaveNow(); DialogResult = true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or Win32Exception or System.Security.Cryptography.CryptographicException)
        { ErrorText.Text = ex.Message; }
    }
}
