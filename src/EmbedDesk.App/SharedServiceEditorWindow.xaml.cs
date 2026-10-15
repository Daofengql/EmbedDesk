using System.Windows;
using EmbedDesk.App.ViewModels;
using EmbedDesk.Infrastructure.Connections;

namespace EmbedDesk.App;
public partial class SharedServiceEditorWindow : Window
{
    private readonly string _id;
    private readonly IReadOnlyList<SerialPortRowViewModel> _devices;
    private readonly bool _isSerial;
    public SharedDeviceService? Result { get; private set; }
    public SharedServiceEditorWindow(SharedDeviceService? service = null, SharedServiceKind? defaultKind = null, IEnumerable<SerialPortRowViewModel>? devices = null)
    {
        InitializeComponent(); WindowTheme.ApplyDarkTitleBar(this); _id = service?.Id ?? Guid.NewGuid().ToString("N");
        var kind = service?.Kind ?? defaultKind ?? SharedServiceKind.Tcp;
        _isSerial = kind == SharedServiceKind.Serial;
        var available = devices?.ToList() ?? [];
        if (_isSerial && service is not null && available.All(p => !string.Equals(p.PortName, service.PortName, StringComparison.OrdinalIgnoreCase)))
            available.Add(new(new(service.PortName, "已配置的串口", "设备当前不在线", null)) { IsAvailable = false });
        _devices = available;
        PortName.ItemsSource = _devices;
        ProtocolBox.SelectedIndex = kind == SharedServiceKind.Udp ? 1 : 0;
        NetworkFields.Visibility = _isSerial ? Visibility.Collapsed : Visibility.Visible;
        SerialFields.Visibility = _isSerial ? Visibility.Visible : Visibility.Collapsed;
        if (service is null) { if (_isSerial) PortName.SelectedItem = _devices.FirstOrDefault(); UpdatePortDescription(); return; }
        ServiceName.Text = service.Name;
        TargetHost.Text = service.TargetHost; TargetPort.Text = service.TargetPort.ToString(); PortName.SelectedItem = _devices.FirstOrDefault(p => string.Equals(p.PortName, service.PortName, StringComparison.OrdinalIgnoreCase)); Enabled.IsChecked = service.Enabled;
        PortDescription.Text = service.Description;
        UpdatePortDescription();
    }
    private void PortName_Changed(object sender, RoutedEventArgs e)
    { if (PortDescription is not null) Dispatcher.BeginInvoke(new Action(UpdatePortDescription)); }
    private void UpdatePortDescription()
    {
        if (!_isSerial || PortDescription is null) return;
        var device = PortName.SelectedItem as SerialPortRowViewModel;
        PortDescription.Text = device is null ? "未发现串口设备" : $"{device.Description} · {device.Manufacturer}";
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var kind = _isSerial ? SharedServiceKind.Serial : ProtocolBox.SelectedIndex == 1 ? SharedServiceKind.Udp : SharedServiceKind.Tcp;
            Result = new SharedDeviceService { Id = _id, Name = ServiceName.Text.Trim(), Kind = kind, TargetHost = TargetHost.Text.Trim(), TargetPort = int.TryParse(TargetPort.Text, out var port) ? port : 0, PortName = (PortName.SelectedItem as SerialPortRowViewModel)?.PortName ?? string.Empty, Enabled = Enabled.IsChecked == true };
            Result.Validate(); DialogResult = true;
        }
        catch (ArgumentException ex) { ErrorText.Text = ex.Message; }
    }
}
