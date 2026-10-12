using System.Net;
using System.Windows;
using System.Windows.Controls;
using EmbedDesk.Core.Models;

namespace EmbedDesk.App;

public partial class NetworkConnectionWindow : Window
{
    public NetworkConnectionWindow(TransportProtocol protocol = TransportProtocol.Tcp)
    {
        InitializeComponent();
        ProtocolBox.SelectedIndex = protocol switch { TransportProtocol.Udp => 1, TransportProtocol.Mqtt => 2, _ => 0 };
        OptionsChanged(this, null!);
        WindowTheme.ApplyDarkTitleBar(this);
    }
    public TransportOptions? Options { get; private set; }
    private void OptionsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ServerFields is null || ClientFields is null || HttpBox is null || MqttFields is null) return;
        var tcp = ProtocolBox.SelectedIndex == 0;
        var mqtt = ProtocolBox.SelectedIndex == 2;
        var server = ModeBox.SelectedIndex == 1;
        if (mqtt && server) ModeBox.SelectedIndex = 0;
        server = ModeBox.SelectedIndex == 1;
        ModeBox.IsEnabled = !mqtt;
        ServerFields.Visibility = server ? Visibility.Visible : Visibility.Collapsed;
        ClientFields.Visibility = server ? Visibility.Collapsed : Visibility.Visible;
        ReconnectBox.Visibility = mqtt || tcp && !server ? Visibility.Visible : Visibility.Collapsed;
        HttpBox.Visibility = HttpHint.Visibility = tcp ? Visibility.Visible : Visibility.Collapsed;
        MqttFields.Visibility = mqtt ? Visibility.Visible : Visibility.Collapsed;
        if (mqtt && RemotePortBox.Text == "9000") RemotePortBox.Text = "1883";
        else if (!mqtt && RemotePortBox.Text == "1883") RemotePortBox.Text = "9000";
        if (server && LocalPortBox.Text == "0") LocalPortBox.Text = "9000";
        else if (!server && LocalPortBox.Text == "9000") LocalPortBox.Text = "0";
    }
    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (!IPAddress.TryParse(LocalHostBox.Text.Trim(), out _)) { ErrorText.Text = "请输入有效的本地 IP 地址。"; return; }
        if (!int.TryParse(LocalPortBox.Text, out var localPort) || localPort is < 0 or > 65535) { ErrorText.Text = "本地端口范围为 0–65535。"; return; }
        var mqtt = ProtocolBox.SelectedIndex == 2;
        var needsRemote = mqtt || ProtocolBox.SelectedIndex == 1 || ModeBox.SelectedIndex == 0;
        if (needsRemote && string.IsNullOrWhiteSpace(RemoteHostBox.Text)) { ErrorText.Text = "请填写目标地址。"; return; }
        if (!int.TryParse(RemotePortBox.Text, out var remotePort) || remotePort is < 1 or > 65535) { ErrorText.Text = "目标端口范围为 1–65535。"; return; }
        if (mqtt && string.IsNullOrWhiteSpace(MqttTopicBox.Text)) { ErrorText.Text = "请填写 MQTT 主题。"; return; }
        Options = new TransportOptions
        {
            Protocol = mqtt ? TransportProtocol.Mqtt : ProtocolBox.SelectedIndex == 0 ? TransportProtocol.Tcp : TransportProtocol.Udp,
            Mode = mqtt || ModeBox.SelectedIndex == 0 ? TransportMode.Client : TransportMode.Server,
            LocalHost = LocalHostBox.Text.Trim(), LocalPort = localPort, RemoteHost = RemoteHostBox.Text.Trim(), RemotePort = remotePort,
            AutoReconnect = mqtt || ProtocolBox.SelectedIndex == 0 && ModeBox.SelectedIndex == 0 && ReconnectBox.IsChecked == true,
            TreatAsHttp = ProtocolBox.SelectedIndex == 0 && HttpBox.IsChecked == true,
            MqttTopic = MqttTopicBox.Text.Trim(),
            MqttUsername = string.IsNullOrWhiteSpace(MqttUsernameBox.Text) ? null : MqttUsernameBox.Text.Trim(),
            MqttPassword = string.IsNullOrEmpty(MqttPasswordBox.Password) ? null : MqttPasswordBox.Password,
            MqttUseTls = MqttTlsBox.IsChecked == true
        };
        DialogResult = true;
    }
}
