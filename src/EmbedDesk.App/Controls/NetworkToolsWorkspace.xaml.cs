using System.Windows;
using System.Windows.Controls;
using EmbedDesk.App.ViewModels;

namespace EmbedDesk.App.Controls;

public partial class NetworkToolsWorkspace : UserControl
{
    public NetworkToolsWorkspace() => InitializeComponent();

    private void OpenTool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tool }) return;
        if (tool == "LanFiles")
        {
            var existing = Window.GetWindow(this)?.OwnedWindows.OfType<LanFileShareWindow>().FirstOrDefault();
            if (existing is not null) { existing.RestoreFromBackground(); return; }
        }
        var workspace = DataContext as MainWindowViewModel;
        var toolPreferences = workspace?.AppSettings.GetNetworkToolPreferences();
        var settings = workspace?.AppSettings;
        Window window = tool switch
        {
            "Cidr" => new CidrCalculatorWindow(toolPreferences?.CidrCalculator, settings is null ? null : new Action<CidrCalculatorPreferences>(settings.SaveCidrCalculatorPreferences)),
            "Ping" => workspace is null
                ? new NetworkProbeToolWindow()
                : new NetworkProbeToolWindow(preferences: workspace.AppSettings.GetNetworkProbePreferences(),
                    savePreferences: workspace.AppSettings.SaveNetworkProbePreferences),
            "Iperf3" => new Iperf3Window(toolPreferences?.Iperf3, settings is null ? null : new Action<Iperf3ToolPreferences>(settings.SaveIperf3ToolPreferences)),
            "Discovery" => new NetworkDiscoveryWindow(toolPreferences?.NetworkDiscovery, settings is null ? null : new Action<NetworkDiscoveryPreferences>(settings.SaveNetworkDiscoveryPreferences)),
            "Nat" => new NatDetectionWindow(toolPreferences?.NatDetection, settings is null ? null : new Action<NatDetectionPreferences>(settings.SaveNatDetectionPreferences)),
            "Upnp" => new UpnpDiscoveryWindow(toolPreferences?.UpnpDiscovery, settings is null ? null : new Action<UpnpDiscoveryPreferences>(settings.SaveUpnpDiscoveryPreferences)),
            "Dns" => new DnsQueryWindow(toolPreferences?.DnsQuery, settings is null ? null : new Action<DnsQueryPreferences>(settings.SaveDnsQueryPreferences)),
            "Checksum" => new ByteToolWindow(checksum: true),
            "Codec" => new ByteToolWindow(),
            "Ports" => new TcpPortCheckWindow(toolPreferences?.TcpPortScan, settings is null ? null : new Action<TcpPortScanPreferences>(settings.SaveTcpPortScanPreferences)),
            "Tls" => new TlsCertificateWindow(toolPreferences?.TlsDiagnostic, settings is null ? null : new Action<TlsDiagnosticPreferences>(settings.SaveTlsDiagnosticPreferences)),
            "LocalInfo" => new LocalInfoWindow(),
            "Hosts" => new HostsManagerWindow(settings is null ? AppDataPaths.SettingsDirectory : System.IO.Path.GetDirectoryName(settings.FilePath)!),
            "Stcp" when workspace is not null => new DeviceServicesWindow(workspace.Connections),
            "LanFiles" => new LanFileShareWindow(),
            _ => throw new InvalidOperationException($"Unknown network tool: {tool}")
        };
        window.Owner = Window.GetWindow(this);
        window.Show();
    }
}
