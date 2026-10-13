using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using EmbedDesk.App.ViewModels;
using EmbedDesk.Core.Models;
using EmbedDesk.Infrastructure.Networking;
using Microsoft.Win32;

namespace EmbedDesk.App;

public partial class HostsManagerWindow : Window
{
    private readonly HostsManagerSettingsStore _store;
    private readonly HostsManagerViewModel _viewModel = new();
    private readonly HostsFileService _hosts;
    private HostsFileSnapshot? _snapshot;
    private bool _settingsLoaded;
    private const string LegacyNotice = "旧方案中的子域名匹配规则已保留并停用，请核对具体域名后再启用；每个子域名需单独添加。";

    public HostsManagerWindow() : this(AppDataPaths.SettingsDirectory) { }

    internal HostsManagerWindow(string settingsDirectory, string? hostsPath = null)
    {
        InitializeComponent(); DataContext = _viewModel; WindowTheme.ApplyDarkTitleBar(this);
        _store = new HostsManagerSettingsStore(settingsDirectory);
        _hosts = new HostsFileService(hostsPath);
        try
        {
            _viewModel.Load(_store.Load()); _settingsLoaded = true;
            if (_store.DisabledLegacySubdomains) SetStatus(LegacyNotice);
        }
        catch (Exception ex) { _viewModel.Load(new HostsManagerSettings()); SetStatus("配置读取失败，原文件将保留，请检查配置文件后重新打开：" + ex.Message); }
        ReloadHosts();
        Closing += OnClosing;
    }

    private void ReloadHosts()
    {
        try
        {
            _snapshot = _hosts.Read(); SystemHostsBox.Text = _snapshot.Text;
            HostsPathText.Text = $"系统 Hosts：{_hosts.HostsPath}";
        }
        catch (Exception ex) { _snapshot = null; SystemHostsBox.Text = "读取失败：" + ex.Message; SetStatus("无法读取系统 Hosts：" + ex.Message); }
    }

    private void SetStatus(string text) => _viewModel.Status = text;

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = "方案 " + (_viewModel.Profiles.Count + 1).ToString(CultureInfo.InvariantCulture);
        var profile = new HostsProfileViewModel { Name = name }; _viewModel.Profiles.Add(profile); _viewModel.SelectedProfile = profile;
    }
    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Profiles.Count <= 1) { SetStatus("至少保留一个方案。"); return; }
        if (!Confirm("删除方案", "删除当前方案及其草稿规则？", "删除")) return;
        var index = _viewModel.Profiles.IndexOf(_viewModel.SelectedProfile!); _viewModel.Profiles.Remove(_viewModel.SelectedProfile!); _viewModel.SelectedProfile = _viewModel.Profiles[Math.Clamp(index, 0, _viewModel.Profiles.Count - 1)];
    }
    private void AddRule_Click(object sender, RoutedEventArgs e) => _viewModel.SelectedProfile?.Rules.Add(new HostsRuleViewModel());
    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        var rows = RulesGrid.SelectedItems.OfType<HostsRuleViewModel>().ToArray(); foreach (var row in rows) _viewModel.SelectedProfile?.Rules.Remove(row);
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try { SaveDraft(); SetStatus("方案草稿已保存；尚未修改系统 Hosts。" ); }
        catch (Exception ex) { SetStatus("保存失败：" + ex.Message); }
    }

    private void ApplyHosts_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var mappings = _viewModel.SelectedProfile?.Rules.Select(r => r.ToMapping()).ToArray() ?? [];
            var valid = HostsMapping.Validate(mappings);
            var enabled = valid.Count(r => r.Enabled);
            if (_snapshot is null) throw new InvalidOperationException("尚未读取系统 Hosts。");
            var replacement = HostsFileService.CreateManagedContent(_snapshot, valid);
            if (!Confirm("应用 Hosts", $"将备份当前文件，并用本方案的 {enabled} 条启用规则替换 EmbedDesk 管理区块。其他区块保持原样。", "应用")) return;
            var backup = _hosts.Apply(_snapshot, replacement, _store.BackupDirectory);
            HostsFileService.FlushDnsCache(); ReloadHosts(); SetStatus($"Hosts 已应用，完整备份：{backup}；请按需刷新目标应用缓存。" );
        }
        catch (UnauthorizedAccessException) { SetStatus("无法写入 Hosts，请检查文件访问权限、只读属性或安全软件拦截。" ); }
        catch (Exception ex) { SetStatus("应用失败：" + ex.Message); }
    }

    private void RemoveHosts_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm("移除映射", "移除 EmbedDesk 管理区块？写入前会先备份，其余 Hosts 内容保持原样。", "移除")) return;
        try
        {
            if (_snapshot is null) throw new InvalidOperationException("尚未读取系统 Hosts。");
            var replacement = HostsFileService.CreateManagedContent(_snapshot, []);
            var backup = _hosts.Apply(_snapshot, replacement, _store.BackupDirectory); HostsFileService.FlushDnsCache(); ReloadHosts(); SetStatus("EmbedDesk 管理区块已移除，备份：" + backup);
        }
        catch (UnauthorizedAccessException) { SetStatus("无法写入 Hosts，请检查文件访问权限、只读属性或安全软件拦截。" ); }
        catch (Exception ex) { SetStatus("移除失败：" + ex.Message); }
    }
    private void Flush_Click(object sender, RoutedEventArgs e) => SetStatus(HostsFileService.FlushDnsCache() ? "已请求 Windows 刷新 DNS 缓存。" : "DNS 缓存刷新不可用或请求失败。" );
    private void ReloadHosts_Click(object sender, RoutedEventArgs e) => ReloadHosts();

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "JSON 配置|*.json", FileName = "embeddesk-hosts.json" }; if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllBytes(dialog.FileName, HostsManagerSettingsStore.Serialize(_viewModel.Capture())); SetStatus("Hosts 方案已导出（不包含系统 Hosts 内容）。" ); } catch (Exception ex) { SetStatus("导出失败：" + ex.Message); }
    }
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "JSON 配置|*.json" }; if (dialog.ShowDialog(this) != true) return;
        try
        {
            var imported = HostsManagerSettingsStore.Parse(HostsManagerSettingsStore.ReadBounded(dialog.FileName), out var disabled);
            if (!Confirm("导入方案", "使用导入的方案替换当前草稿？导入不会修改系统 Hosts。", "导入")) return;
            _viewModel.Load(imported);
            SetStatus(disabled ? LegacyNotice : "已导入草稿；保存方案或关闭窗口时保存到本机配置。");
        }
        catch (Exception ex) { SetStatus("导入失败：" + ex.Message); }
    }
    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Hosts 备份|*.bak", InitialDirectory = _store.BackupDirectory }; if (dialog.ShowDialog(this) != true) return;
        if (!Confirm("恢复 Hosts", "恢复备份将替换当前系统 Hosts，恢复前会再次备份。", "恢复")) return;
        try
        {
            var backup = HostsManagerSettingsStore.ReadBounded(dialog.FileName); _ = HostsFileService.Decode(backup); if (_snapshot is null) throw new InvalidOperationException("尚未读取系统 Hosts。");
            var currentBackup = _hosts.Apply(_snapshot, backup, _store.BackupDirectory); HostsFileService.FlushDnsCache(); ReloadHosts(); SetStatus("已恢复备份；恢复前的文件已备份：" + currentBackup);
        }
        catch (UnauthorizedAccessException) { SetStatus("无法写入 Hosts，请检查文件访问权限、只读属性或安全软件拦截。" ); } catch (Exception ex) { SetStatus("恢复失败：" + ex.Message); }
    }

    private void SaveDraft()
    {
        if (!_settingsLoaded) throw new InvalidOperationException("原配置未能读取，已禁止覆盖；可先导出草稿，再检查配置文件并重新打开。");
        RulesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        RulesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        _store.Save(_viewModel.Capture());
    }

    private bool Confirm(string title, string message, string label) => new ConfirmationWindow(title, message, label) { Owner = this }.ShowDialog() == true;

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_settingsLoaded) return;
        try { SaveDraft(); }
        catch (Exception ex)
        {
            SetStatus("保存失败：" + ex.Message);
            e.Cancel = !Confirm("草稿未保存", ex.Message + "\n仍然关闭并放弃未保存修改？", "放弃并关闭");
        }
    }
}
