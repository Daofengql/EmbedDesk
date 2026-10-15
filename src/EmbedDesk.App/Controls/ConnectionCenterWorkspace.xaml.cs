using System.Windows;
using System.Windows.Controls;
using EmbedDesk.App.ViewModels;
using EmbedDesk.Infrastructure.Connections;

namespace EmbedDesk.App.Controls;
public partial class ConnectionCenterWorkspace : UserControl
{
    public ConnectionCenterWorkspace() => InitializeComponent();
    private ConnectionCenterViewModel? Model => DataContext as ConnectionCenterViewModel;
    private bool Confirm(string title, string message) => new ConfirmationWindow(title, message, "继续") { Owner = Window.GetWindow(this) }.ShowDialog() == true;
    private void CopyDeviceId_Click(object sender, RoutedEventArgs e)
    { if (Model is { Node.DeviceId.Length: > 0 } model) Copy(model.Node.DeviceId); }
    private void CopyPassword_Click(object sender, RoutedEventArgs e)
    { if (Model is { Password.Length: > 0 } model && model.Password != "仅人工授权") Copy(model.Password); }
    private void Copy(string value)
    {
        try { Clipboard.SetText(value); }
        catch (System.Runtime.InteropServices.ExternalException)
        { MessageBox.Show(Window.GetWindow(this), "剪贴板正在被其他程序使用，请稍后重试。", "复制", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (Model is { } model && (model.Node.Peers.Count == 0 || Confirm("重新连接", "重新上线会结束当前设备连接和映射，是否继续？"))) await model.StartAsync();
    }
    private async void Stop_Click(object sender, RoutedEventArgs e)
    { if (Model is { } model && Confirm("停止上线", "停止中继连接并结束当前所有设备会话和映射？共享配置会保留。")) await model.StopAsync(); }
    private async void Connect_Click(object sender, RoutedEventArgs e)
    { if (Model is { } model) { var password = RemotePassword.Password; RemotePassword.Clear(); await model.ConnectAsync(password); } }
    private void CancelConnect_Click(object sender, RoutedEventArgs e) => Model?.CancelConnect();
    private async void Approve_Click(object sender, RoutedEventArgs e)
    { if (Model is { } model && sender is FrameworkElement { DataContext: DeviceApproval approval }) await model.ApproveAsync(approval, true); }
    private async void Deny_Click(object sender, RoutedEventArgs e)
    { if (Model is { } model && sender is FrameworkElement { DataContext: DeviceApproval approval }) await model.ApproveAsync(approval, false); }
    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    { if (Model is { } model && sender is FrameworkElement { DataContext: DevicePeerInfo peer } && Confirm("结束设备连接", "断开此设备并撤销本次授权，关闭其串口和映射？")) await model.DisconnectAsync(peer); }
}
