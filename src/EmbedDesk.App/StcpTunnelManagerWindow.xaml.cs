using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using EmbedDesk.Protocols;

namespace EmbedDesk.App;

public partial class StcpTunnelManagerWindow : Window
{
    private readonly AppSettingsStore _settings;
    private readonly ObservableCollection<TunnelListItem> _items = [];
    private bool _loading;

    internal StcpTunnelManagerWindow(AppSettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        _settings = settings;
        TunnelList.ItemsSource = _items;
        foreach (var profile in _settings.GetStcpTunnels()) _items.Add(CreateListItem(profile));
        RoleBox.SelectedIndex = 0;
        if (_items.Count > 0) TunnelList.SelectedIndex = 0;
        else New_Click(this, new RoutedEventArgs());
        UpdateRoleFields();
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var profile = new StcpTunnelProfile
        {
            Name = $"新建隧道 {_items.Count + 1}",
            Role = StcpTunnelRole.Service,
            ServerAddress = "HOST",
            ServerPort = 7000,
            ServiceName = "ssh",
            TargetHost = "127.0.0.1",
            TargetPort = 22,
            LocalPort = 0,
            Enabled = true
        };
        _items.Add(CreateListItem(profile));
        TunnelList.SelectedIndex = _items.Count - 1;
        StatusText.Text = "新配置尚未保存。Service 与 Visitor 需要使用相同的服务名称和共享密钥。";
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (TunnelList.SelectedItem is not TunnelListItem item) return;
        if (new ConfirmationWindow("删除 STCP 配置",
                $"确定删除“{item.Profile.Name}”配置及其本机保存的共享密钥吗？此操作无法撤销。", "删除配置") { Owner = this }.ShowDialog() != true) return;

        if (!string.IsNullOrWhiteSpace(item.Profile.CredentialTarget))
        {
            try { StcpCredentialStore.Delete(item.Profile.CredentialTarget); }
            catch (Win32Exception exception) { StatusText.Text = $"删除本地密钥失败：{exception.Message}"; return; }
        }

        _settings.DeleteStcpTunnel(item.Profile.Id);
        _items.Remove(item);
        TunnelList.SelectedIndex = _items.Count == 0 ? -1 : Math.Min(TunnelList.SelectedIndex, _items.Count - 1);
        if (TunnelList.SelectedItem is null && _items.Count > 0) TunnelList.SelectedIndex = 0;
        if (_items.Count == 0) ClearEditor();
        StatusText.Text = $"已删除“{item.Profile.Name}”配置及其本机保存的密钥。";
    }

    private void TunnelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TunnelList.SelectedItem is not TunnelListItem item) return;
        LoadProfile(item.Profile);
    }

    private void LoadProfile(StcpTunnelProfile profile)
    {
        _loading = true;
        NameBox.Text = profile.Name;
        RoleBox.SelectedIndex = profile.Role == StcpTunnelRole.Service ? 0 : 1;
        ServiceNameBox.Text = profile.ServiceName;
        ServerAddressBox.Text = profile.ServerAddress;
        ServerPortBox.Text = profile.ServerPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        TargetHostBox.Text = profile.TargetHost;
        TargetPortBox.Text = profile.TargetPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        LocalPortBox.Text = profile.LocalPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RateBox.Text = profile.MaxBytesPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture);
        EnabledBox.IsChecked = profile.Enabled;
        SecretBox.Clear();
        RememberSecretBox.IsChecked = false;
        if (!string.IsNullOrWhiteSpace(profile.CredentialTarget))
        {
            try
            {
                var secret = StcpCredentialStore.Read(profile.CredentialTarget);
                if (!string.IsNullOrEmpty(secret))
                {
                    SecretBox.Password = secret;
                    RememberSecretBox.IsChecked = true;
                }
            }
            catch (Win32Exception exception) { StatusText.Text = $"读取本机密钥失败：{exception.Message}"; }
        }
        _loading = false;
        UpdateRoleFields();
        StatusText.Text = GetReadiness(profile);
    }

    private void RoleBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && RoleBox is not null && ServiceFields is not null) UpdateRoleFields();
    }

    private void UpdateRoleFields()
    {
        var isService = RoleBox.SelectedIndex != 1;
        ServiceFields.Visibility = isService ? Visibility.Visible : Visibility.Collapsed;
        VisitorFields.Visibility = isService ? Visibility.Collapsed : Visibility.Visible;
    }

    private void GenerateSecret_Click(object sender, RoutedEventArgs e)
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        try { SecretBox.Password = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        RememberSecretBox.IsChecked = true;
        StatusText.Text = "已生成 256 位随机密钥。勾选凭据管理器保存前，关闭窗口会丢弃此密钥。";
    }

    private void CopySecret_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(SecretBox.Password))
        {
            StatusText.Text = "先输入或生成共享密钥，再复制。";
            return;
        }

        try
        {
            Clipboard.SetText(SecretBox.Password);
            StatusText.Text = "共享密钥已复制到剪贴板；请只通过可信渠道传递，并在使用后清除剪贴板。";
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            StatusText.Text = $"复制共享密钥失败：{exception.Message}";
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (TunnelList.SelectedItem is not TunnelListItem selected) return;
        try
        {
            var role = RoleBox.SelectedIndex == 1 ? StcpTunnelRole.Visitor : StcpTunnelRole.Service;
            if (!int.TryParse(ServerPortBox.Text, out var serverPort)
                || !int.TryParse(TargetPortBox.Text, out var targetPort)
                || !int.TryParse(LocalPortBox.Text, out var localPort)
                || !int.TryParse(RateBox.Text, out var rate))
                throw new ArgumentException("端口和限速必须是有效整数。");

            var profile = selected.Profile with
            {
                Name = NameBox.Text.Trim(),
                Role = role,
                ServiceName = ServiceNameBox.Text.Trim(),
                ServerAddress = ServerAddressBox.Text.Trim(),
                ServerPort = serverPort,
                TargetHost = TargetHostBox.Text.Trim(),
                TargetPort = targetPort,
                LocalPort = localPort,
                MaxBytesPerSecond = rate,
                Enabled = EnabledBox.IsChecked == true
            };

            if (_items.Any(item => item.Profile.Id != profile.Id && string.Equals(item.Profile.Name.Trim(), profile.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("已存在同名 STCP 配置，请更换名称。");

            var secret = SecretBox.Password;
            var existingCredentialTarget = profile.CredentialTarget;
            if (string.IsNullOrEmpty(secret) && !string.IsNullOrEmpty(existingCredentialTarget))
                secret = StcpCredentialStore.Read(existingCredentialTarget) ?? string.Empty;

            var rememberSecret = RememberSecretBox.IsChecked == true;
            if (secret.Length > 0) StcpProtocol.Validate(profile.ToDefinition(secret));
            else profile.Validate();

            if (rememberSecret)
            {
                if (secret.Length == 0) throw new ArgumentException("要保存共享密钥，请先输入或生成密钥。");
                var target = string.IsNullOrEmpty(profile.CredentialTarget)
                    ? StcpTunnelProfile.CreateCredentialTarget(profile.Id)
                    : profile.CredentialTarget;
                StcpCredentialStore.Save(target, secret);
                profile = profile with { CredentialTarget = target };
            }
            else
            {
                profile = profile with { CredentialTarget = string.Empty };
            }

            profile.Validate();
            _settings.SaveStcpTunnel(profile);
            var index = _items.IndexOf(selected);
            _items[index] = CreateListItem(profile);
            TunnelList.SelectedIndex = index;
            if (!rememberSecret && !string.IsNullOrEmpty(existingCredentialTarget))
            {
                try { StcpCredentialStore.Delete(existingCredentialTarget); }
                catch (Win32Exception exception) { StatusText.Text = $"配置已保存，但清除旧密钥失败：{exception.Message}"; }
            }
            SecretBox.Clear();
            if (rememberSecret) RememberSecretBox.IsChecked = true;
            StatusText.Text = $"已保存“{profile.Name}”。{GetReadiness(profile)}";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            StatusText.Text = $"无法保存配置：{exception.Message}";
        }
    }

    private void ClearEditor()
    {
        _loading = true;
        NameBox.Clear(); ServiceNameBox.Clear(); ServerAddressBox.Clear(); ServerPortBox.Clear();
        TargetHostBox.Clear(); TargetPortBox.Clear(); LocalPortBox.Clear(); RateBox.Text = "0";
        SecretBox.Clear(); RememberSecretBox.IsChecked = false; EnabledBox.IsChecked = true;
        _loading = false;
    }

    private TunnelListItem CreateListItem(StcpTunnelProfile profile)
    {
        var role = profile.Role == StcpTunnelRole.Service ? "Service" : "Visitor";
        return new TunnelListItem(profile, profile.Name, role, GetReadiness(profile));
    }

    private static string GetReadiness(StcpTunnelProfile profile)
    {
        var keyStored = !string.IsNullOrWhiteSpace(profile.CredentialTarget);
        var enabled = profile.Enabled ? "配置启用" : "配置禁用";
        return $"{enabled} · {(keyStored ? "密钥已存本机" : "未保存密钥")} · 隧道未启动";
    }

    private sealed record TunnelListItem(StcpTunnelProfile Profile, string Name, string RoleLabel, string ReadinessLabel);
}
