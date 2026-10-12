using System.Windows;
using System.Windows.Controls;

namespace EmbedDesk.App.Controls;

public partial class NetworkToolsWorkspace : UserControl
{
    public NetworkToolsWorkspace() => InitializeComponent();

    private void OpenTool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tool }) return;
        Window window = tool switch
        {
            "Cidr" => new CidrCalculatorWindow(),
            "Ping" => new NetworkProbeToolWindow(),
            "Iperf3" => new Iperf3Window(),
            "Discovery" => new NetworkDiscoveryWindow(),
            "Nat" => new NatDetectionWindow(),
            "Upnp" => new UpnpDiscoveryWindow(),
            "Dns" => new DnsQueryWindow(),
            "Checksum" => new ByteToolWindow(checksum: true),
            "Codec" => new ByteToolWindow(),
            "Ports" => new TcpPortCheckWindow(),
            "Tls" => new TlsCertificateWindow(),
            _ => throw new InvalidOperationException($"Unknown network tool: {tool}")
        };
        window.Owner = Window.GetWindow(this);
        window.Show();
    }
}
