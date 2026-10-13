using System.Windows;
using System.Windows.Controls;
using EmbedDesk.App.ViewModels;

namespace EmbedDesk.App;

public partial class BridgeWindow : Window
{
    private bool _refreshingPresets;

    public BridgeWindow()
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        Loaded += (_, _) => RefreshPresets();
    }

    private MainWindowViewModel? Workspace => DataContext as MainWindowViewModel;
    private void RefreshPresets(Guid? selectedId = null)
    {
        if (Workspace is not { } workspace) return;
        _refreshingPresets = true;
        try
        {
            BridgePresetBox.ItemsSource = workspace.GetSerialBridgePresets();
            BridgePresetBox.SelectedItem = selectedId is { } id
                ? BridgePresetBox.Items.Cast<SerialBridgeClientPreset>().FirstOrDefault(preset => preset.Id == id)
                : null;
        }
        finally { _refreshingPresets = false; }
    }
    private void BridgePreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingPresets || BridgePresetBox.SelectedItem is not SerialBridgeClientPreset preset) return;
        BridgePresetNameBox.Text = preset.Name;
        ApplyBridgePreset(preset);
    }
    private void LoadBridgePreset_Click(object sender, RoutedEventArgs e)
    {
        if (BridgePresetBox.SelectedItem is not SerialBridgeClientPreset preset) return;
        BridgePresetNameBox.Text = preset.Name;
        ApplyBridgePreset(preset);
    }
    private void ApplyBridgePreset(SerialBridgeClientPreset preset)
    {
        if (Workspace is not { } workspace) return;
        workspace.BridgeMode = "客户端";
        workspace.BridgeRemoteHost = preset.Host;
        workspace.BridgeRemotePort = preset.Port.ToString();
    }
    private void SaveBridgePreset_Click(object sender, RoutedEventArgs e)
    {
        SaveBridgePreset(Guid.NewGuid());
    }
    private void UpdateBridgePreset_Click(object sender, RoutedEventArgs e)
    {
        if (BridgePresetBox.SelectedItem is not SerialBridgeClientPreset selected)
        { MessageBox.Show(this, "请先选择要更新的预设，或使用“另存”。", "更新预设", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        SaveBridgePreset(selected.Id);
    }
    private void SaveBridgePreset(Guid id)
    {
        if (Workspace is not { } workspace) return;
        if (!string.Equals(workspace.BridgeMode, "客户端", StringComparison.Ordinal))
        { MessageBox.Show(this, "网络串口预设只保存客户端地址，请先切换到客户端模式。", "保存预设", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        try
        {
            workspace.SaveSerialBridgePreset(new SerialBridgeClientPreset
            {
                Id = id,
                Name = BridgePresetNameBox.Text,
                Host = workspace.BridgeRemoteHost,
                Port = int.TryParse(workspace.BridgeRemotePort, out var port) ? port : 0
            });
            RefreshPresets(id);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        { MessageBox.Show(this, exception.Message, "保存预设失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void DeleteBridgePreset_Click(object sender, RoutedEventArgs e)
    {
        if (Workspace is not { } workspace || BridgePresetBox.SelectedItem is not SerialBridgeClientPreset selected) return;
        if (MessageBox.Show(this, $"确定删除网络串口预设“{selected.Name}”吗？", "删除预设", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        workspace.DeleteSerialBridgePreset(selected.Id);
        RefreshPresets();
    }
}
