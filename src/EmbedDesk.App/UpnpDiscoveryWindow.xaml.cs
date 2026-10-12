using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace EmbedDesk.App;

public sealed record UpnpDevice(string Type, string Location, string Server, string Endpoint);

public partial class UpnpDiscoveryWindow : Window, IDisposable
{
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("239.255.255.250");
    private CancellationTokenSource? _lifetime;

    public UpnpDiscoveryWindow()
    {
        InitializeComponent();
        DataContext = this;
        SearchTypeBox.SelectedIndex = 0;
        WindowTheme.ApplyDarkTitleBar(this);
        Closed += (_, _) => Dispose();
    }

    public ObservableCollection<UpnpDevice> Devices { get; } = [];

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        await StopAsync();
        Devices.Clear(); StatusText.Text = "正在发送 SSDP 搜索…"; StopButton.IsEnabled = true; SearchTypeBox.IsEnabled = false;
        var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        _lifetime = lifetime;
        try
        {
            var searchTarget = SearchTypeBox.SelectedIndex switch
            {
                1 => "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
                2 => "upnp:rootdevice",
                _ => "ssdp:all"
            };
            await DiscoverAsync(searchTarget, lifetime.Token);
            StatusText.Text = $"发现完成，共 {Devices.Count} 个设备响应";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { StatusText.Text = "已停止"; }
        catch (SocketException ex) { StatusText.Text = $"SSDP 失败：{ex.Message}"; }
        finally
        {
            if (ReferenceEquals(_lifetime, lifetime)) _lifetime = null;
            lifetime.Dispose(); StopButton.IsEnabled = false; SearchTypeBox.IsEnabled = true;
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopAsync();

    private async Task StopAsync()
    {
        var lifetime = _lifetime;
        if (lifetime is null) return;
        _lifetime = null; lifetime.Cancel(); lifetime.Dispose(); await Task.Yield();
        StopButton.IsEnabled = false; SearchTypeBox.IsEnabled = true; StatusText.Text = "已停止";
    }

    private async Task DiscoverAsync(string searchTarget, CancellationToken cancellationToken)
    {
        using var client = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        var request = Encoding.ASCII.GetBytes($"M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: {searchTarget}\r\n\r\n");
        await client.SendAsync(request, request.Length, new IPEndPoint(MulticastAddress, 1900));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult response;
            try { response = await client.ReceiveAsync(cancellationToken); }
            catch (OperationCanceledException) { break; }
            var headers = ParseHeaders(Encoding.ASCII.GetString(response.Buffer));
            headers.TryGetValue("location", out var location);
            headers.TryGetValue("server", out var server);
            headers.TryGetValue("st", out var type);
            var key = $"{location}|{response.RemoteEndPoint}";
            if (!seen.Add(key)) continue;
            Devices.Add(new UpnpDevice(type ?? "UPnP 设备", location ?? "—", server ?? "—", response.RemoteEndPoint.ToString()));
        }
    }

    private static Dictionary<string, string> ParseHeaders(string response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in response.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;
            headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return headers;
    }

    public void Dispose()
    {
        _lifetime?.Cancel(); _lifetime?.Dispose(); _lifetime = null; GC.SuppressFinalize(this);
    }
}
