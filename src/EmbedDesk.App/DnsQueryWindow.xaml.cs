using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Input;
using EmbedDesk.Infrastructure.Networking;

namespace EmbedDesk.App;

public partial class DnsQueryWindow : Window, IDisposable
{
    private const string AutomaticType = "自动（域名 A / IP PTR）";
    private CancellationTokenSource? _queryLifetime;
    private bool _closed;
    private string[] _dnsServers = [];
    private string _dnsAddress = "";
    private string _dohAddress = "https://dns.alidns.com/dns-query";
    private bool _usingDoh;
    private readonly Action<DnsQueryPreferences>? _savePreferences;
    private static readonly string[] DohServers = ["https://dns.alidns.com/dns-query", "https://doh.pub/dns-query", "https://cloudflare-dns.com/dns-query", "https://dns.google/dns-query"];

    public DnsQueryWindow(DnsQueryPreferences? preferences = null, Action<DnsQueryPreferences>? savePreferences = null)
    {
        _savePreferences = savePreferences;
        InitializeComponent();
        DataContext = this;
        TypeBox.ItemsSource = new[] { AutomaticType }.Concat(DnsLookupClient.RecordTypes).ToArray();
        TypeBox.SelectedIndex = 0;
        ClassBox.ItemsSource = new[] { "IN", "CH", "HS" };
        ClassBox.SelectedIndex = TransportBox.SelectedIndex = 0;
        PopulateServers();
        if (preferences is not null) ApplyPreferences(preferences);
        WindowTheme.ApplyDarkTitleBar(this);
        Closed += (_, _) => Dispose();
        Closed += (_, _) => _savePreferences?.Invoke(CapturePreferences());
    }

    private void ApplyPreferences(DnsQueryPreferences preferences)
    {
        QueryBox.Text = preferences.Query;
        TypeBox.Text = preferences.RecordType;
        ClassBox.SelectedIndex = preferences.QueryClassIndex;
        PortBox.Text = preferences.Port.ToString(CultureInfo.InvariantCulture);
        TimeoutBox.Text = preferences.TimeoutMilliseconds.ToString(CultureInfo.InvariantCulture);
        RetriesBox.Text = preferences.Retries.ToString(CultureInfo.InvariantCulture);
        RecursionBox.IsChecked = preferences.RecursionDesired;
        TransportBox.SelectedIndex = preferences.TransportIndex;
        _dnsAddress = string.IsNullOrWhiteSpace(preferences.DnsServer) ? _dnsServers.FirstOrDefault() ?? string.Empty : preferences.DnsServer;
        _dohAddress = preferences.DohEndpoint;
        ServerBox.Text = _usingDoh ? _dohAddress : _dnsAddress;
    }

    private DnsQueryPreferences CapturePreferences() => new()
    {
        Query = QueryBox.Text,
        RecordType = TypeBox.Text,
        QueryClassIndex = ClassBox.SelectedIndex,
        TransportIndex = TransportBox.SelectedIndex,
        DnsServer = _usingDoh ? _dnsAddress : ServerBox.Text,
        DohEndpoint = _usingDoh ? ServerBox.Text : _dohAddress,
        Port = int.TryParse(PortBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ? port : 53,
        TimeoutMilliseconds = int.TryParse(TimeoutBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeout) ? timeout : 2000,
        Retries = int.TryParse(RetriesBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var retries) ? retries : 1,
        RecursionDesired = RecursionBox.IsChecked == true
    };

    public ObservableCollection<DnsResourceRecord> Results { get; } = [];

    private void PopulateServers()
    {
        var systemServers = new List<string>();
        try
        {
            // Windows virtual adapters can expose these obsolete fallback placeholders
            // even when no DNS server is configured on that adapter.
            var placeholders = new[] { "fec0:0:0:ffff::1", "fec0:0:0:ffff::2", "fec0:0:0:ffff::3" }.Select(IPAddress.Parse).ToHashSet();
            var interfaces = new List<IPInterfaceProperties>();
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(network => network.OperationalStatus == OperationalStatus.Up))
            {
                try { interfaces.Add(adapter.GetIPProperties()); }
                catch (NetworkInformationException) { }
            }
            foreach (var properties in interfaces.OrderByDescending(properties => properties.GatewayAddresses.Count > 0))
                systemServers.AddRange(properties.DnsAddresses.Where(address => !placeholders.Contains(address) &&
                    !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any)).Select(address => address.ToString()));
        }
        catch (NetworkInformationException) { }
        ServerBox.ItemsSource = systemServers.Concat(new[] { "223.5.5.5", "119.29.29.29", "114.114.114.114", "1.1.1.1", "8.8.8.8", "2606:4700:4700::1111" }).Distinct().ToArray();
        ServerBox.Text = systemServers.FirstOrDefault() ?? "";
        _dnsServers = ((IEnumerable<string>)ServerBox.ItemsSource).ToArray();
        if (systemServers.Count == 0) StatusText.Text = "未读取到系统 DNS，请输入或选择 DNS 服务器。";
    }

    private void Transport_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ServerBox is null || TransportHint is null) return;
        var doh = TransportBox.SelectedIndex == 2;
        if (doh == _usingDoh) return;
        if (_usingDoh) _dohAddress = ServerBox.Text; else _dnsAddress = ServerBox.Text;
        _usingDoh = doh;
        ServerBox.ItemsSource = doh ? DohServers : _dnsServers;
        ServerBox.Text = doh ? _dohAddress : _dnsAddress;
        ServerLabel.Text = doh ? "DoH 服务 · HTTPS URL，可自定义" : "DNS 服务器 · IPv4 / IPv6";
        // Let the URL use both address columns; its optional port is part of the URL.
        PortPanel.Visibility = doh ? Visibility.Collapsed : Visibility.Visible;
        System.Windows.Controls.Grid.SetColumnSpan((UIElement)ServerBox.Parent, doh ? 2 : 1);
        TransportHint.Text = doh ? "RFC 8484 · HTTPS POST · 校验服务器证书；服务域名由系统解析。" : "每次直接向所选服务器发起查询；不读取系统 DNS 缓存。";
    }

    private async void Query_Click(object sender, RoutedEventArgs e) => await QueryAsync();
    private async void Query_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await QueryAsync();
    }

    private DnsLookupOptions ReadOptions()
    {
        Uri? endpoint = null;
        var server = IPAddress.Loopback;
        var port = 53;
        if (_usingDoh)
        {
            if (!Uri.TryCreate(ServerBox.Text.Trim(), UriKind.Absolute, out endpoint) || endpoint.Scheme != Uri.UriSchemeHttps ||
                endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0)
                throw new FormatException("请输入完整的 HTTPS DoH 地址，例如 https://dns.alidns.com/dns-query。");
        }
        else
        {
            if (!IPAddress.TryParse(ServerBox.Text.Trim(), out server)) throw new FormatException("DNS 服务器需要填写 IPv4 或 IPv6 地址。");
            if (!int.TryParse(PortBox.Text, out port) || port is < 1 or > 65535) throw new FormatException("端口应为 1–65535。");
        }
        if (!int.TryParse(TimeoutBox.Text, out var timeout) || timeout is < 100 or > 60000) throw new FormatException("单次超时应为 100–60000 毫秒。");
        if (!int.TryParse(RetriesBox.Text, out var retries) || retries is < 0 or > 5) throw new FormatException("重试次数应为 0–5。");
        var type = TypeBox.Text == AutomaticType ? (ushort)0 : DnsLookupClient.ParseType(TypeBox.Text);
        return new(QueryBox.Text.Trim(), type, new IPEndPoint(server, port))
        {
            QueryClass = ClassBox.SelectedIndex switch { 1 => 3, 2 => 4, _ => 1 },
            UseTcp = TransportBox.SelectedIndex == 1, HttpsEndpoint = endpoint, RecursionDesired = RecursionBox.IsChecked == true,
            TimeoutMilliseconds = timeout, Retries = retries
        };
    }

    private async Task QueryAsync()
    {
        if (_queryLifetime is not null || _closed) return;
        DnsLookupOptions options;
        try { options = ReadOptions(); }
        catch (FormatException ex) { StatusText.Text = ex.Message; return; }

        using var lifetime = new CancellationTokenSource();
        _queryLifetime = lifetime;
        Results.Clear(); DetailsBox.Clear();
        ParametersPanel.IsEnabled = QueryButton.IsEnabled = CopyButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        ResponseText.Text = "正在查询…";
        var serverLabel = options.HttpsEndpoint?.AbsoluteUri ?? options.Server.ToString();
        MetadataText.Text = $"服务器 {serverLabel} · {(options.HttpsEndpoint is not null ? "DoH · HTTPS" : options.UseTcp ? "TCP" : "UDP，截断时转 TCP")}";
        StatusText.Text = "正在等待 DNS 响应…";
        try
        {
            var response = await new DnsLookupClient().QueryAsync(options, lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            if (_closed) return;
            foreach (var record in response.Records) Results.Add(record);
            var answerCount = response.Records.Count(record => record.Section == "应答");
            ResponseText.Text = response.Status + $" · 应答 {answerCount} 条 / 共 {Results.Count} 条";
            MetadataText.Text = $"{response.Server} · {response.Transport} · {response.ElapsedMilliseconds:0.0} ms · 尝试 {response.Attempts} 次\n{response.Flags}";
            DetailsBox.Text = FormatResponse(response);
            CopyButton.IsEnabled = true;
            StatusText.Text = response.ResponseCode == 0 && answerCount == 0
                ? "服务器未返回该类型的应答记录；可查看权威 / 附加区段及详细响应。"
                : $"查询完成 · {response.Question} · {response.Type} / {response.Class}";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            if (!_closed) { ResponseText.Text = "已停止"; StatusText.Text = "查询已取消。"; }
        }
        catch (Exception ex) when (ex is SocketException or HttpRequestException or ArgumentException or FormatException or TimeoutException or System.IO.IOException or System.IO.InvalidDataException)
        {
            if (!_closed)
            {
                ResponseText.Text = "查询失败";
                DetailsBox.Text = $"{options.Query}\n服务器：{serverLabel}\n{ex.Message}";
                StatusText.Text = ex.Message;
                CopyButton.IsEnabled = true;
            }
        }
        finally
        {
            _queryLifetime = null;
            if (!_closed)
            {
                ParametersPanel.IsEnabled = QueryButton.IsEnabled = true;
                StopButton.IsEnabled = false;
            }
        }
    }

    private static string FormatResponse(DnsLookupResponse response)
    {
        var text = new StringBuilder();
        text.AppendLine($"SERVER: {response.Server}");
        text.AppendLine($"QUESTION: {response.Question}  {response.Class}  {response.Type}");
        text.AppendLine($"STATUS: {response.Status}");
        text.AppendLine($"FLAGS: {response.Flags}");
        text.AppendLine(FormattableString.Invariant($"TRANSPORT: {response.Transport}  TIME: {response.ElapsedMilliseconds:0.0} ms  ATTEMPTS: {response.Attempts}  BYTES: {response.MessageBytes}"));
        foreach (var section in new[] { "应答", "权威", "附加" })
        {
            text.AppendLine().AppendLine($";; {section}");
            var records = response.Records.Where(record => record.Section == section).ToArray();
            if (records.Length == 0) text.AppendLine("(空)");
            foreach (var record in records)
                text.AppendLine($"{record.Name}\t{record.Ttl.ToString(CultureInfo.InvariantCulture)}\t{record.Class}\t{record.Type}\t{record.Value}");
        }
        return text.ToString();
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _queryLifetime?.Cancel();
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(DetailsBox.Text); StatusText.Text = "已复制完整 DNS 响应。"; }
        catch (System.Runtime.InteropServices.COMException) { StatusText.Text = "剪贴板正被占用，请重试。"; }
    }

    public void Dispose()
    {
        _closed = true;
        _queryLifetime?.Cancel();
        GC.SuppressFinalize(this);
    }
}
