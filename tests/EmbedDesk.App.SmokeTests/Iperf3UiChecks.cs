using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using EmbedDesk.Infrastructure.Networking;

namespace EmbedDesk.App.SmokeTests;

internal static class Iperf3UiChecks
{
    internal static async Task RunAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = Iperf3Client.RunServerAsync(IPAddress.Loopback, port, token: stop.Token);
        var window = new Iperf3Window(); window.Show();
        try
        {
            window.Width = window.MinWidth; window.Height = window.MinHeight;
            Box("NodeBox").SelectedIndex = 1;
            Require(Text("HostBox").Text == PublicIperf3Servers.All[0].Host, "Public preset fills the host without initiating a test");
            Text("HostBox").Text = "127.0.0.1"; Text("PortBox").Text = port.ToString();
            Require(Box("NodeBox").SelectedIndex == 0, "Editing the endpoint returns to custom-server mode");
            Box("ProtocolBox").SelectedIndex = 1; Text("DurationBox").Text = "1";
            Text("BlockSizeBox").Text = "200"; Text("RateBox").Text = "10"; Box("RateUnitBox").SelectedIndex = 4;
            Click("StartButton");
            Require(!((Panel)window.FindName("ParametersPanel")).IsEnabled, "Measurement locks the submitted parameters");
            await Until(() => ((Button)window.FindName("StartButton")).IsEnabled);
            Require(Label("StatusText").Text == "测试完成。" && Text("OutputBox").Text.Contains("16.00 Kbit/s") && Text("OutputBox").Text.Contains("UDP 接收端"),
                "UDP UI converts 10 packets/s at 200 bytes to 16 Kbit/s and displays receiver statistics");
            window.UpdateLayout();
            var output = Text("OutputBox"); var status = Label("StatusText"); var content = (FrameworkElement)window.Content;
            Require(output.ActualHeight >= 90 && status.TransformToAncestor(content).Transform(new Point(0, status.ActualHeight)).Y <= content.ActualHeight + 1,
                "Results and status fit the minimum window height");
            await FileWorkspaceChecks.CaptureAsync(window, "embeddesk-iperf3-udp.png");
            Box("ProtocolBox").SelectedIndex = 0;
            Require(Box("RateUnitBox").SelectedIndex != 4, "TCP switches away from UDP-only packet-rate units");
            Box("ModeBox").SelectedIndex = 1;
            Require(!((Panel)window.FindName("ClientOptionsPanel")).IsEnabled && Text("HostBox").Text == "0.0.0.0", "Server mode makes client-negotiated options explicit");
            await FileWorkspaceChecks.CaptureAsync(window, "embeddesk-iperf3-server.png");
        }
        finally { window.Close(); stop.Cancel(); try { await server; } catch (OperationCanceledException) { } }
        ComboBox Box(string name) => (ComboBox)window.FindName(name);
        TextBox Text(string name) => (TextBox)window.FindName(name);
        TextBlock Label(string name) => (TextBlock)window.FindName(name);
        void Click(string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static async Task Until(Func<bool> ready)
    { using var stop = new CancellationTokenSource(10000); while (!ready()) await Task.Delay(25, stop.Token); }
    private static void Require(bool pass, string message) { if (!pass) throw new InvalidOperationException(message); Console.WriteLine("PASS " + message); }
}
