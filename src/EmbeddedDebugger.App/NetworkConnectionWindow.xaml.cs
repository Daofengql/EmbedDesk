using System.Net;
using System.Windows;
using System.Windows.Controls;
using EmbeddedDebugger.Core.Models;

namespace EmbeddedDebugger.App;

public partial class NetworkConnectionWindow : Window
{
    public NetworkConnectionWindow() { InitializeComponent(); OptionsChanged(this, null!); WindowTheme.ApplyDarkTitleBar(this); }
    public TransportOptions? Options { get; private set; }
    private void OptionsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RemoteFields is null || HttpBox is null) return;
        var tcp = ProtocolBox.SelectedIndex == 0;
        var server = ModeBox.SelectedIndex == 1;
        RemoteFields.Visibility = tcp && server ? Visibility.Collapsed : Visibility.Visible;
        RemoteLabel.Text = server ? "默认回发地址（UDP 收到数据后回复最近对端）" : "目标地址";
        ReconnectBox.Visibility = tcp && !server ? Visibility.Visible : Visibility.Collapsed;
        HttpBox.Visibility = HttpHint.Visibility = tcp ? Visibility.Visible : Visibility.Collapsed;
        if (server && LocalPortBox.Text == "0") LocalPortBox.Text = "9000";
        else if (!server && LocalPortBox.Text == "9000") LocalPortBox.Text = "0";
    }
    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (!IPAddress.TryParse(LocalHostBox.Text.Trim(), out _)) { ErrorText.Text = "请输入有效的本地 IP 地址。"; return; }
        if (!int.TryParse(LocalPortBox.Text, out var localPort) || localPort is < 0 or > 65535) { ErrorText.Text = "本地端口范围为 0–65535。"; return; }
        var needsRemote = ProtocolBox.SelectedIndex == 1 || ModeBox.SelectedIndex == 0;
        if (needsRemote && string.IsNullOrWhiteSpace(RemoteHostBox.Text)) { ErrorText.Text = "请填写目标地址。"; return; }
        if (!int.TryParse(RemotePortBox.Text, out var remotePort) || remotePort is < 1 or > 65535) { ErrorText.Text = "目标端口范围为 1–65535。"; return; }
        Options = new TransportOptions
        {
            Protocol = ProtocolBox.SelectedIndex == 0 ? TransportProtocol.Tcp : TransportProtocol.Udp,
            Mode = ModeBox.SelectedIndex == 0 ? TransportMode.Client : TransportMode.Server,
            LocalHost = LocalHostBox.Text.Trim(), LocalPort = localPort, RemoteHost = RemoteHostBox.Text.Trim(), RemotePort = remotePort,
            AutoReconnect = ProtocolBox.SelectedIndex == 0 && ModeBox.SelectedIndex == 0 && ReconnectBox.IsChecked == true,
            TreatAsHttp = ProtocolBox.SelectedIndex == 0 && HttpBox.IsChecked == true
        };
        DialogResult = true;
    }
}
