using System.Windows;
using System.Windows.Controls;
using EmbedDesk.App;

namespace EmbedDesk.App.SmokeTests;

internal static class NetworkToolPreferencesChecks
{
    public static void Run()
    {
        var systemDnsWindow = new DnsQueryWindow();
        var systemDns = ((ComboBox)systemDnsWindow.FindName("ServerBox")).Text;
        Close(systemDnsWindow);
        var restoredDefaultDnsWindow = new DnsQueryWindow(new DnsQueryPreferences());
        Require(((ComboBox)restoredDefaultDnsWindow.FindName("ServerBox")).Text == systemDns,
            "DNS preferences preserve the detected system resolver when no custom server is saved");
        Close(restoredDefaultDnsWindow);

        CidrCalculatorPreferences? savedCidr = null;
        var cidr = new CidrCalculatorWindow(new CidrCalculatorPreferences { Input = "10.20.0.0/16" }, preferences => savedCidr = preferences);
        Require(((TextBox)cidr.FindName("InputBox")).Text == "10.20.0.0/16", "CIDR calculator restores its last input");
        ((TextBox)cidr.FindName("InputBox")).Text = "172.16.0.0/12";
        Close(cidr);
        Require(savedCidr?.Input == "172.16.0.0/12", "CIDR calculator saves the current input when closed");

        DnsQueryPreferences? savedDns = null;
        var dns = new DnsQueryWindow(new DnsQueryPreferences
        {
            Query = "device.example.test", RecordType = "AAAA", TransportIndex = 2,
            DohEndpoint = "https://dns.example.test/query", TimeoutMilliseconds = 4000, Retries = 2
        }, preferences => savedDns = preferences);
        Require(((TextBox)dns.FindName("QueryBox")).Text == "device.example.test"
            && ((ComboBox)dns.FindName("TransportBox")).SelectedIndex == 2
            && ((ComboBox)dns.FindName("ServerBox")).Text == "https://dns.example.test/query",
            "DNS window restores the query, transport and DoH endpoint");
        ((TextBox)dns.FindName("QueryBox")).Text = "updated.example.test";
        Close(dns);
        Require(savedDns?.Query == "updated.example.test" && savedDns.TransportIndex == 2,
            "DNS window saves query settings without response records");

        TcpPortScanPreferences? savedScan = null;
        var scan = new TcpPortCheckWindow(new TcpPortScanPreferences { Host = "HOST", Ports = "22,80-82", Concurrency = 32 }, preferences => savedScan = preferences);
        Require(((TextBox)scan.FindName("HostBox")).Text == "HOST" && ((TextBox)scan.FindName("PortsBox")).Text == "22,80-82",
            "port scan window restores host and port list");
        Close(scan);
        Require(savedScan?.Host == "HOST" && savedScan.Ports == "22,80-82", "port scan saves its inputs without results");

        TlsDiagnosticPreferences? savedTls = null;
        var tls = new TlsCertificateWindow(new TlsDiagnosticPreferences { Host = "HOST", Port = 8443, ServerName = "service.example.test" }, preferences => savedTls = preferences);
        Require(((TextBox)tls.FindName("HostBox")).Text == "HOST" && ((TextBox)tls.FindName("PortBox")).Text == "8443"
            && ((TextBox)tls.FindName("SniBox")).Text == "service.example.test",
            "TLS diagnostic restores connection and SNI settings");
        Close(tls);
        Require(savedTls?.Host == "HOST" && savedTls.Port == 8443 && savedTls.ServerName == "service.example.test",
            "TLS diagnostic saves only non-secret inspection settings");

        Iperf3ToolPreferences? savedIperf = null;
        var iperf = new Iperf3Window(new Iperf3ToolPreferences { Host = "HOST", Port = 5202, ProtocolIndex = 1, BlockSizeBytes = 1200, RateUnitIndex = 4 }, preferences => savedIperf = preferences);
        Require(((TextBox)iperf.FindName("HostBox")).Text == "HOST" && ((ComboBox)iperf.FindName("ProtocolBox")).SelectedIndex == 1
            && ((TextBox)iperf.FindName("BlockSizeBox")).Text == "1200",
            "iperf3 window restores client test parameters without starting a test");
        Close(iperf);
        Require(savedIperf?.Host == "HOST" && savedIperf.ProtocolIndex == 1 && savedIperf.BlockSizeBytes == 1200,
            "iperf3 saves settings without traffic statistics");

        NetworkDiscoveryPreferences? savedDiscovery = null;
        var discovery = new NetworkDiscoveryWindow(new NetworkDiscoveryPreferences { LocalAddress = string.Empty, Concurrency = 48 }, preferences => savedDiscovery = preferences);
        Require(((TextBox)discovery.FindName("ConcurrencyBox")).Text == "48", "network discovery restores scan concurrency");
        Close(discovery);
        Require(savedDiscovery?.Concurrency == 48, "network discovery saves its interface selection and concurrency without device results");

        NatDetectionPreferences? savedNat = null;
        var nat = new NatDetectionWindow(new NatDetectionPreferences { Servers = "stun.example.test:3478", ShowServerSettings = true }, preferences => savedNat = preferences);
        Require(((TextBox)nat.FindName("ServersBox")).Text == "stun.example.test:3478", "NAT detection restores its STUN server list");
        Close(nat);
        Require(savedNat?.Servers == "stun.example.test:3478" && savedNat.ShowServerSettings,
            "NAT detection saves its settings without probe results");

        UpnpDiscoveryPreferences? savedUpnp = null;
        var upnp = new UpnpDiscoveryWindow(new UpnpDiscoveryPreferences { SearchTypeIndex = 2 }, preferences => savedUpnp = preferences);
        Require(((ComboBox)upnp.FindName("SearchTypeBox")).SelectedIndex == 2, "UPnP discovery restores its search target");
        Close(upnp);
        Require(savedUpnp?.SearchTypeIndex == 2, "UPnP discovery saves its search target without device results");
    }

    private static void Close(Window window)
    {
        window.Show();
        window.Close();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Network tool preferences check failed: {message}");
        Console.WriteLine($"PASS {message}");
    }
}
