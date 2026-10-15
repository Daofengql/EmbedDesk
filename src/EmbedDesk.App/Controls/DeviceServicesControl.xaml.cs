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
    private async void Map_Click(object sender, RoutedEventArgs e)
    { if (Model is { } model) { if (!int.TryParse(LocalPort.Text, out var port)) { Error("请输入有效端口。"); return; } await model.MapAsync(port); } }
    private async void UseSerial_Click(object sender, RoutedEventArgs e)
    {
        if (Model?.SelectedRemoteService is not { } service || !Model.IsSerialServiceSelected) return;
        var dialog = new RemoteSerialUseWindow(service) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        if (dialog.UseVirtualPort && dialog.SelectedPair is { } pair) await Model.MapVirtualSerialAsync(pair);
        else
        {
            var main = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault(w => w.DataContext is MainWindowViewModel model && ReferenceEquals(model.Connections, Model));
            if (main is not null) Try(() => main.ShowRemoteSerial(service));
        }
    }
    private async void Unmap_Click(object sender, RoutedEventArgs e)
    { if (Model?.SelectedRule is { } rule && new ConfirmationWindow("停用映射", "停止此映射并关闭转发通道？", "停用映射") { Owner = Window.GetWindow(this) }.ShowDialog() == true) await Model.DisableRuleAsync(rule); }
    private async void EnableRule_Click(object sender, RoutedEventArgs e) { if (Model?.SelectedRule is { } rule) await Model.EnableRuleAsync(rule); }
    private async void DeleteRule_Click(object sender, RoutedEventArgs e)
    { if (Model?.SelectedRule is { } rule && new ConfirmationWindow("删除映射规则", "停止映射并删除已保存的规则？", "删除规则") { Owner = Window.GetWindow(this) }.ShowDialog() == true) await Model.DeleteRuleAsync(rule); }
    private void Try(Action action) { try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { Error(ex.Message); } }
    private void Error(string text) => MessageBox.Show(Window.GetWindow(this), text, "映射设置", MessageBoxButton.OK, MessageBoxImage.Information);
}
