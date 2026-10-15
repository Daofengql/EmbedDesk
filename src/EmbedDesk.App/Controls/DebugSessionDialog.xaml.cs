using System.Globalization;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EmbedDesk.App.ViewModels;
using EmbedDesk.Core.Models;
using EmbedDesk.Infrastructure.Diagnostics;

namespace EmbedDesk.App.Controls;

internal enum DebugSessionKind { Serial, NetworkClient, NetworkServer, Pid, Camera, HttpClient, HttpServer, Mqtt }
internal sealed record DebugSessionRequest(DebugSessionKind Kind, DebugToolEndpoint Endpoint, TransportOptions? NetworkOptions);

public partial class DebugSessionDialog : UserControl
{
    private readonly MainWindowViewModel _workspace;
    private readonly bool _editing;
    private bool _loading;
    private bool _busy;
    private DebugSessionKind _kind;
    internal event EventHandler? Cancelled;
    internal event EventHandler? Submitted;
    internal DebugSessionRequest? Request { get; private set; }
    internal static readonly IReadOnlyDictionary<DebugToolTransport, string> TransportNames = new Dictionary<DebugToolTransport, string>
    {
        [DebugToolTransport.Serial] = "串口", [DebugToolTransport.TcpClient] = "TCP 客户端", [DebugToolTransport.TcpServer] = "TCP 服务端",
        [DebugToolTransport.UdpTarget] = "UDP 客户端", [DebugToolTransport.UdpListen] = "UDP 服务端", [DebugToolTransport.Udp] = "UDP 服务端"
    };
    private static readonly IReadOnlyDictionary<DebugSessionKind, string> KindNames = new Dictionary<DebugSessionKind, string>
    {
        [DebugSessionKind.Serial] = "串口调试", [DebugSessionKind.NetworkClient] = "TCP/UDP 客户端", [DebugSessionKind.NetworkServer] = "TCP/UDP 服务端",
        [DebugSessionKind.Pid] = "PID 调试", [DebugSessionKind.Camera] = "摄像头调试",
        [DebugSessionKind.HttpClient] = "HTTP 客户端", [DebugSessionKind.HttpServer] = "HTTP 服务端", [DebugSessionKind.Mqtt] = "MQTT 客户端"
    };

    internal DebugSessionDialog(MainWindowViewModel workspace, DebugSessionKind? kind = null, DebugToolEndpoint? endpoint = null, TransportOptions? options = null)
    {
        _workspace = workspace;
        _editing = kind is not null;
        InitializeComponent();
        PortBox.ItemsSource = workspace.SerialPorts;
        DataBitsBox.ItemsSource = new[] { 5, 6, 7, 8 };
        ParityBox.ItemsSource = Enum.GetValues<Parity>();
        StopBitsBox.ItemsSource = new[] { StopBits.One, StopBits.OnePointFive, StopBits.Two };
        FlowBox.ItemsSource = Enum.GetValues<Handshake>();
        ProtocolBox.ItemsSource = DebugToolConnectionChoices.Protocols;
        if (kind is { } selected) ShowSettings(selected, endpoint, options);
        Loaded += (_, _) => { if (_editing) FocusFirstField(); else SerialTypeButton.Focus(); };
    }

    private void Type_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<DebugSessionKind>(tag, out var kind)) ShowSettings(kind);
    }

    internal void ShowSettings(DebugSessionKind kind, DebugToolEndpoint? endpoint = null, TransportOptions? options = null)
    {
        _kind = kind;
        _loading = true;
        try
        {
            TypePicker.Visibility = Visibility.Collapsed;
            ConnectionFields.Visibility = ApplyButton.Visibility = Visibility.Visible;
            BackButton.Visibility = _editing ? Visibility.Collapsed : Visibility.Visible;
            Heading.Text = (_editing ? "编辑连接 · " : "新建 · ") + KindNames[kind];
            Description.Text = kind is DebugSessionKind.Pid or DebugSessionKind.Camera ? "设置通信参数，创建后在会话内连接设备。" : kind == DebugSessionKind.Serial ? "设置串口参数，也可以创建后再选择设备。" : "设置连接端点，收发内容在会话内管理。";
            ApplyButton.Content = _editing ? "应用设置" : IsNetwork ? "创建并启动" : "创建会话";
            var transport = kind switch
            {
                DebugSessionKind.NetworkClient or DebugSessionKind.HttpClient or DebugSessionKind.Mqtt => DebugToolTransport.TcpClient,
                DebugSessionKind.NetworkServer or DebugSessionKind.HttpServer => DebugToolTransport.TcpServer,
                _ => DebugToolTransport.Serial
            };
            endpoint ??= new(transport, transport is DebugToolTransport.TcpServer or DebugToolTransport.UdpListen ? "0.0.0.0" : "127.0.0.1", kind == DebugSessionKind.Mqtt ? 1883 : 9000,
                new() { PortName = _workspace.SerialPorts.FirstOrDefault()?.PortName ?? "" });
            var transportChoices = TransportNames.Where(p => kind switch
            {
                DebugSessionKind.NetworkClient => p.Key is DebugToolTransport.TcpClient or DebugToolTransport.UdpTarget,
                DebugSessionKind.NetworkServer => p.Key is DebugToolTransport.TcpServer or DebugToolTransport.UdpListen,
                _ => p.Key == transport
            });
            TransportBox.ItemsSource = IsDeviceTool ? DebugToolConnectionChoices.Types : transportChoices.Select(p => IsRawNetwork
                ? new KeyValuePair<DebugToolTransport, string>(p.Key, p.Key is DebugToolTransport.TcpClient or DebugToolTransport.TcpServer ? "TCP" : "UDP") : p).ToArray();
            ProtocolBox.SelectedValue = DebugToolConnectionChoices.ProtocolFor(endpoint.Transport);
            TransportBox.SelectedValue = IsDeviceTool ? DebugToolConnectionChoices.TypeFor(endpoint.Transport)
                : kind == DebugSessionKind.NetworkServer && endpoint.Transport == DebugToolTransport.Udp ? DebugToolTransport.UdpListen : endpoint.Transport;
            LockUdpPeerBox.IsChecked = endpoint.Transport == DebugToolTransport.Udp;
            TransportLabel.Text = IsRawNetwork ? "协议" : "连接方式";
            TransportFields.Visibility = IsRawNetwork || kind is DebugSessionKind.Pid or DebugSessionKind.Camera ? Visibility.Visible : Visibility.Collapsed;
            var serial = endpoint.Serial ?? new();
            PortBox.SelectedValue = serial.PortName; PortBox.Text = serial.PortName;
            BaudBox.Text = serial.BaudRate.ToString(CultureInfo.InvariantCulture); DataBitsBox.SelectedItem = serial.DataBits;
            ParityBox.SelectedItem = serial.Parity; StopBitsBox.SelectedItem = serial.StopBits; FlowBox.SelectedItem = serial.Handshake;
            HostBox.Text = endpoint.Host; PortNumberBox.Text = endpoint.Port.ToString(CultureInfo.InvariantCulture);
            LocalHostBox.Text = endpoint.LocalHost; LocalPortBox.Text = endpoint.LocalPort.ToString(CultureInfo.InvariantCulture);
            TimeoutBox.Text = endpoint.TimeoutMilliseconds.ToString(CultureInfo.InvariantCulture);
            ReconnectBox.IsChecked = options?.AutoReconnect ?? kind is DebugSessionKind.HttpClient or DebugSessionKind.Mqtt;
            MqttTopicBox.Text = options?.MqttTopic ?? "embeddesk"; MqttUsernameBox.Text = options?.MqttUsername ?? "";
            MqttPasswordBox.Password = options?.MqttPassword ?? ""; MqttTlsBox.IsChecked = options?.MqttUseTls == true;
            ErrorText.Visibility = Visibility.Collapsed;
        }
        finally { _loading = false; UpdateFields(); }
        DialogScroll.ScrollToTop();
        FocusFirstField();
    }

    private bool IsNetwork => _kind is not (DebugSessionKind.Serial or DebugSessionKind.Pid or DebugSessionKind.Camera);
    private bool IsDeviceTool => _kind is DebugSessionKind.Pid or DebugSessionKind.Camera;
    private bool IsRawNetwork => _kind is DebugSessionKind.NetworkClient or DebugSessionKind.NetworkServer;
    private DebugToolTransport? SelectedTransport => TransportBox.SelectedValue is DebugToolTransport transport
        ? IsDeviceTool ? DebugToolConnectionChoices.Resolve(transport, ProtocolBox.SelectedValue is TransportProtocol protocol ? protocol : TransportProtocol.Tcp)
            : _kind == DebugSessionKind.NetworkServer && transport == DebugToolTransport.UdpListen && LockUdpPeerBox.IsChecked == true ? DebugToolTransport.Udp : transport
        : null;
    private void FocusFirstField()
    {
        if (TransportFields.IsVisible) TransportBox.Focus();
        else if (SerialFields.IsVisible) PortBox.Focus();
        else HostBox.Focus();
    }
    private void Transport_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsInitialized) return;
        if (SelectedTransport is { } transport)
        {
            if (HostBox.Text is "0.0.0.0" or "127.0.0.1") HostBox.Text = transport is DebugToolTransport.TcpClient or DebugToolTransport.UdpTarget ? "127.0.0.1" : "0.0.0.0";
        }
        UpdateFields();
    }
    private void Protocol_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && IsInitialized) UpdateFields();
    }
    private void UdpMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading && IsInitialized) UpdateFields();
    }
    private void UpdateFields()
    {
        if (SelectedTransport is not { } t) return;
        ProtocolFields.Visibility = IsDeviceTool && t != DebugToolTransport.Serial ? Visibility.Visible : Visibility.Collapsed;
        SerialFields.Visibility = t == DebugToolTransport.Serial ? Visibility.Visible : Visibility.Collapsed;
        NetworkFields.Visibility = t == DebugToolTransport.Serial ? Visibility.Collapsed : Visibility.Visible;
        var client = t is DebugToolTransport.TcpClient or DebugToolTransport.UdpTarget;
        HostLabel.Text = client ? "远程地址" : "本机监听地址";
        LocalFields.Visibility = client && _kind != DebugSessionKind.Mqtt ? Visibility.Visible : Visibility.Collapsed;
        TimeoutFields.Visibility = t == DebugToolTransport.TcpClient && _kind != DebugSessionKind.Mqtt ? Visibility.Visible : Visibility.Collapsed;
        MqttFields.Visibility = _kind == DebugSessionKind.Mqtt ? Visibility.Visible : Visibility.Collapsed;
        LockUdpPeerBox.Visibility = _kind == DebugSessionKind.NetworkServer && t is DebugToolTransport.UdpListen or DebugToolTransport.Udp ? Visibility.Visible : Visibility.Collapsed;
        ReconnectBox.Visibility = IsNetwork && t == DebugToolTransport.TcpClient ? Visibility.Visible : Visibility.Collapsed;
        ModeHint.Text = _kind == DebugSessionKind.Mqtt ? "连接后订阅并发布到所填主题。TLS 会验证服务器证书。" : t switch
        {
            DebugToolTransport.Serial => "创建会话不会打开串口；流控默认关闭。",
            DebugToolTransport.Udp => "首个数据报锁定发送端；回复只发给该对端，重新连接后解除锁定。",
            DebugToolTransport.UdpTarget => "只与指定目标收发；本地端口为 0 时由系统分配。",
            DebugToolTransport.UdpListen => "接收多个对端的数据报，保留报文边界，可选择回复对端。",
            DebugToolTransport.TcpServer => _kind is DebugSessionKind.Pid or DebugSessionKind.Camera ? "等待一个开发板连接；本会话只解析该开发板的数据。" : "监听本机端口；可向全部或指定客户端发送。",
            _ => "本地地址 0.0.0.0 使用系统路由；本地端口 0 由系统分配。"
        };
    }

    internal DebugToolEndpoint ReadEndpoint()
    {
        if (SelectedTransport is not { } transport) throw new ArgumentException("请选择连接方式。");
        static int Number(TextBox box) => int.Parse(box.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture);
        var portName = PortBox.Text.Trim();
        if (PortBox.SelectedItem is SerialPortRowViewModel selected && (portName == selected.SelectionLabel || portName == selected.PortName)) portName = selected.PortName;
        var serial = transport == DebugToolTransport.Serial ? new SerialPortSettings
        {
            PortName = portName,
            BaudRate = Number(BaudBox), DataBits = (int)DataBitsBox.SelectedItem, Parity = (Parity)ParityBox.SelectedItem,
            StopBits = (StopBits)StopBitsBox.SelectedItem, Handshake = (Handshake)FlowBox.SelectedItem
        } : null;
        var endpoint = new DebugToolEndpoint(transport, HostBox.Text.Trim(), transport == DebugToolTransport.Serial ? 9000 : Number(PortNumberBox), serial)
        {
            LocalHost = transport is DebugToolTransport.TcpClient or DebugToolTransport.UdpTarget ? LocalHostBox.Text.Trim() : "0.0.0.0",
            LocalPort = transport is DebugToolTransport.TcpClient or DebugToolTransport.UdpTarget ? Number(LocalPortBox) : 0,
            TimeoutMilliseconds = transport == DebugToolTransport.TcpClient ? Number(TimeoutBox) : 10000
        };
        // An unconfigured serial session can be created before its device is plugged in.
        DebugConnectionPreset.Validate(endpoint, allowUnconfiguredSerial: !_editing);
        return endpoint;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            var endpoint = ReadEndpoint();
            TransportOptions? options = null;
            if (IsNetwork)
            {
                var raw = DebugConnectionPreset.ToOptions(endpoint);
                options = new()
                {
                    Protocol = _kind == DebugSessionKind.Mqtt ? TransportProtocol.Mqtt : endpoint.Transport is DebugToolTransport.Udp or DebugToolTransport.UdpListen or DebugToolTransport.UdpTarget ? TransportProtocol.Udp : TransportProtocol.Tcp,
                    TreatAsHttp = _kind is DebugSessionKind.HttpClient or DebugSessionKind.HttpServer,
                    Mode = raw.Mode, LocalHost = raw.LocalHost, LocalPort = raw.LocalPort, ClientLocalAddress = raw.ClientLocalAddress,
                    RemoteHost = raw.RemoteHost, RemotePort = raw.RemotePort, ConnectTimeoutMilliseconds = raw.ConnectTimeoutMilliseconds,
                    LockFirstUdpPeer = raw.LockFirstUdpPeer,
                    AutoReconnect = endpoint.Transport == DebugToolTransport.TcpClient && ReconnectBox.IsChecked == true,
                    MqttTopic = MqttTopicBox.Text.Trim(), MqttUsername = MqttUsernameBox.Text.Trim(),
                    MqttPassword = MqttPasswordBox.Password, MqttUseTls = MqttTlsBox.IsChecked == true
                };
                if (_kind == DebugSessionKind.Mqtt && string.IsNullOrWhiteSpace(options.MqttTopic)) throw new ArgumentException("请填写 MQTT 主题。");
            }
            Request = new(_kind, endpoint, options);
            Submitted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException) { ShowError(ex.Message); }
    }

    internal void SetBusy(bool busy)
    {
        _busy = busy;
        ConnectionFields.IsEnabled = TypePicker.IsEnabled = ApplyButton.IsEnabled = BackButton.IsEnabled = CancelButton.IsEnabled = CloseButton.IsEnabled = !busy;
    }
    internal void ShowError(string message) { ErrorText.Text = message; ErrorText.Visibility = Visibility.Visible; }
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        var selected = PortBox.Text;
        try { await _workspace.ScanSerialPortsCommand.ExecuteAsync(null); if (PortBox.SelectedItem is null) PortBox.Text = selected; }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { RefreshButton.IsEnabled = true; }
    }
    private void Back_Click(object sender, RoutedEventArgs e)
    {
        TypePicker.Visibility = Visibility.Visible;
        ConnectionFields.Visibility = ApplyButton.Visibility = BackButton.Visibility = ErrorText.Visibility = Visibility.Collapsed;
        Heading.Text = "新建调试会话"; Description.Text = "选择会话类型，每个会话独立连接与收发。";
        MqttPasswordBox.Clear(); Request = null;
        DialogScroll.ScrollToTop(); SerialTypeButton.Focus();
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) { if (!_busy) Cancelled?.Invoke(this, EventArgs.Empty); }
    private void Dialog_KeyDown(object sender, KeyEventArgs e)
    {
        if (_busy) return;
        if (e.Key == Key.Escape) { e.Handled = true; Cancelled?.Invoke(this, EventArgs.Empty); }
        else if (e.Key == Key.Enter && ApplyButton.IsVisible && e.OriginalSource is not Button) { e.Handled = true; Apply_Click(sender, e); }
    }
}
