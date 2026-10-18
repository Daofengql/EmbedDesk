using System.Windows;
using System.Windows.Controls;
using EmbedDesk.App.ViewModels;

namespace EmbedDesk.App.Controls;
public partial class DeviceServicesControl : UserControl
{
    public DeviceServicesControl() => InitializeComponent();
    private ConnectionCenterViewModel? Model => DataContext as ConnectionCenterViewModel;
    private void AddSerial_Click(object sender, RoutedEventArgs e) => Edit(null, EmbedDesk.Infrastructure.Connections.SharedServiceKind.Serial);
    private void AddNetwork_Click(object sender, RoutedEventArgs e) => Edit(null, EmbedDesk.Infrastructure.Connections.SharedServiceKind.Tcp);
    private void EditSerial_Click(object sender, RoutedEventArgs e) => Edit(Model?.SelectedLocalSerialService, EmbedDesk.Infrastructure.Connections.SharedServiceKind.Serial);
    private void EditNetwork_Click(object sender, RoutedEventArgs e)
    { if (Model?.SelectedLocalNetworkService is { } service) Edit(service, service.Kind); }
    private void Edit(EmbedDesk.Infrastructure.Connections.SharedDeviceService? service, EmbedDesk.Infrastructure.Connections.SharedServiceKind kind)
    {
        if (Model is null) return;
        var editor = new SharedServiceEditorWindow(service, kind, Model.LocalSerialDevices) { Owner = Window.GetWindow(this) };
        if (editor.ShowDialog() == true) Try(() => Model.SaveService(editor.Result!));
    }
    private void DeleteSerial_Click(object sender, RoutedEventArgs e)
    {
        DeleteService(Model?.SelectedLocalSerialService);
    }
    private void DeleteNetwork_Click(object sender, RoutedEventArgs e) => DeleteService(Model?.SelectedLocalNetworkService);
    private void DeleteService(EmbedDesk.Infrastructure.Connections.SharedDeviceService? service)
    {
        if (service is not null && new ConfirmationWindow("删除映射服务", $"取消发布并删除“{service.Name}”？", "删除") { Owner = Window.GetWindow(this) }.ShowDialog() == true) Try(() => Model?.RemoveService(service));
    }
    private void AddSerialMapping_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        new RemoteSerialUseWindow(model.RemoteServices, model, service =>
        {
            var main = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault(w => w.DataContext is MainWindowViewModel mainModel && ReferenceEquals(mainModel.Connections, model))
                ?? throw new InvalidOperationException("未找到主窗口，无法打开远程串口调试。");
            main.ShowRemoteSerial(service);
        }) { Owner = Window.GetWindow(this) }.ShowDialog();
    }
    private void AddNetworkMapping_Click(object sender, RoutedEventArgs e)
    { if (Model is { } model) new RemotePortMappingWindow(model.RemoteServices, model) { Owner = Window.GetWindow(this) }.ShowDialog(); }
    private async void EnableSerialRule_Click(object sender, RoutedEventArgs e) => await EnableRuleAsync(SerialMappingRulesList.SelectedItem as PortMappingRuleViewModel);
    private async void EnableNetworkRule_Click(object sender, RoutedEventArgs e) => await EnableRuleAsync(NetworkMappingRulesList.SelectedItem as PortMappingRuleViewModel);
    private async void DisableSerialRule_Click(object sender, RoutedEventArgs e) => await DisableRuleAsync(SerialMappingRulesList.SelectedItem as PortMappingRuleViewModel);
    private async void DisableNetworkRule_Click(object sender, RoutedEventArgs e) => await DisableRuleAsync(NetworkMappingRulesList.SelectedItem as PortMappingRuleViewModel);
    private async void DeleteSerialRule_Click(object sender, RoutedEventArgs e) => await DeleteRuleAsync(SerialMappingRulesList.SelectedItem as PortMappingRuleViewModel);
    private async void DeleteNetworkRule_Click(object sender, RoutedEventArgs e) => await DeleteRuleAsync(NetworkMappingRulesList.SelectedItem as PortMappingRuleViewModel);
    private async Task EnableRuleAsync(PortMappingRuleViewModel? rule)
    { if (Model is { } model && rule is not null) await model.EnableRuleAsync(rule); }
    private async Task DisableRuleAsync(PortMappingRuleViewModel? rule)
    { if (Model is { } model && rule is not null && new ConfirmationWindow("停用映射", $"停止“{rule.LocalEndpoint}”并关闭转发通道？", "停用映射") { Owner = Window.GetWindow(this) }.ShowDialog() == true) await model.DisableRuleAsync(rule); }
    private async Task DeleteRuleAsync(PortMappingRuleViewModel? rule)
    { if (Model is { } model && rule is not null && new ConfirmationWindow("删除映射规则", $"停止“{rule.LocalEndpoint}”并删除已保存的规则？", "删除规则") { Owner = Window.GetWindow(this) }.ShowDialog() == true) await model.DeleteRuleAsync(rule); }
    private void Try(Action action) { try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { Error(ex.Message); } }
    private void Error(string text) => MessageBox.Show(Window.GetWindow(this), text, "映射设置", MessageBoxButton.OK, MessageBoxImage.Information);
}
