using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using EmbedDesk.Core.Abstractions;
using EmbedDesk.Core.Models;
using EmbedDesk.Infrastructure.Diagnostics;

namespace EmbedDesk.App.Controls;

public partial class DebugToolConnectionPanel : UserControl
{
    private ISerialPortService? _serial;
    private DebugToolConnection? _connection;
    private CancellationTokenSource? _cancel;
    private Task? _run;
    private DebugToolEndpoint? _parameters;
    private bool _embedded;
    private readonly object _sendLock = new();
    private readonly HashSet<Task> _sends = [];
    public bool IsActive => _cancel is not null;
    public string Status { get; private set; } = "未连接";
    private void SetStatus(string status, string? detail = null)
    {
        Status = status; StateText.Text = detail ?? status;
        ActivityChanged?.Invoke(this, EventArgs.Empty);
    }
    public Func<ReadOnlyMemory<byte>, Task>? ReceiveAsync { get; set; }
    public Action? Starting { get; set; }
    public event EventHandler? ActivityChanged;

    public DebugToolConnectionPanel()
    {
        InitializeComponent();
        ProtocolBox.ItemsSource = DebugToolConnectionChoices.Protocols; ProtocolBox.SelectedIndex = 0;
        TransportBox.ItemsSource = DebugToolConnectionChoices.Types; TransportBox.SelectedIndex = 0;
        BaudBox.ItemsSource = new[] { 9600, 57600, 115200, 230400, 460800, 921600, 2000000 }; BaudBox.Text = "115200";
    }

    public void Initialize(ISerialPortService serial) { _serial = serial; Loaded += async (_, _) => await RefreshAsync(); }
    internal void UseEmbeddedLayout()
    {
        _embedded = true; SettingsPanel.Visibility = Visibility.Collapsed; RefreshButton.Visibility = Visibility.Collapsed;
        StateText.Width = double.NaN; StateText.MaxWidth = 600; StateText.Margin = new Thickness(0); StateText.TextTrimming = TextTrimming.CharacterEllipsis;
        ActionPanel.Margin = new Thickness(0); StateText.Text = Status;
    }
    internal DebugToolEndpoint CaptureEndpoint()
    {
        var transport = SelectedTransport;
        var old = _parameters?.Serial;
        var serial = transport == DebugToolTransport.Serial ? new SerialPortSettings
        {
            PortName = (PortBox.SelectedItem as SerialPortDescriptor)?.PortName ?? old?.PortName ?? "",
            BaudRate = int.Parse(BaudBox.Text, CultureInfo.InvariantCulture),
            DataBits = old?.DataBits ?? 8, Parity = old?.Parity ?? System.IO.Ports.Parity.None,
            StopBits = old?.StopBits ?? System.IO.Ports.StopBits.One, Handshake = old?.Handshake ?? System.IO.Ports.Handshake.None
        } : null;
        return new(transport, HostBox.Text.Trim(), int.Parse(NetworkPortBox.Text, CultureInfo.InvariantCulture), serial)
        { LocalHost = _parameters?.LocalHost ?? "0.0.0.0", LocalPort = _parameters?.LocalPort ?? 0, TimeoutMilliseconds = _parameters?.TimeoutMilliseconds ?? 10000 };
    }
    internal void ApplyEndpoint(DebugToolEndpoint endpoint)
    {
        if (IsActive) throw new InvalidOperationException("请先断开当前连接。");
        DebugConnectionPreset.Validate(endpoint);
        if (endpoint.Transport == DebugToolTransport.UdpListen) throw new ArgumentException("PID 与摄像头需要固定目标或锁定首个发送端。");
        _parameters = endpoint;
        ProtocolBox.SelectedValue = DebugToolConnectionChoices.ProtocolFor(endpoint.Transport);
        TransportBox.SelectedValue = DebugToolConnectionChoices.TypeFor(endpoint.Transport);
        HostBox.Text = endpoint.Host; NetworkPortBox.Text = endpoint.Port.ToString(CultureInfo.InvariantCulture);
        if (endpoint.Serial is { } serial)
        {
            var ports = PortBox.Items.Cast<SerialPortDescriptor>().ToList();
            var port = ports.FirstOrDefault(p => p.PortName == serial.PortName);
            if (port is null) { port = new(serial.PortName, serial.PortName, null, null); ports.Add(port); }
            PortBox.ItemsSource = ports; PortBox.SelectedItem = port; BaudBox.Text = serial.BaudRate.ToString(CultureInfo.InvariantCulture);
        }
        SetStatus("未连接");
    }

    public void UseCompactLayout()
    {
        foreach (var field in SettingsPanel.Children.OfType<StackPanel>())
        {
            field.Orientation = Orientation.Horizontal; field.Width = 220; field.Margin = new Thickness(0, 0, 0, 5);
            if (field.Children[0] is TextBlock label) { label.Width = 62; label.FontSize = 11; label.Margin = new Thickness(0); label.VerticalAlignment = VerticalAlignment.Center; }
            if (field.Children[1] is FrameworkElement editor) editor.Width = 158;
        }
        // Keep network settings within three compact rows at the minimum tool width.
        SettingsPanel.Children.Remove(ProtocolPanel);
        SettingsPanel.Children.Insert(SettingsPanel.Children.IndexOf(PortPanel) + 1, ProtocolPanel);
        PortPanel.Width = 118; ((TextBlock)PortPanel.Children[0]).Width = 40; NetworkPortBox.Width = 72;
        ProtocolPanel.Width = 102; ((TextBlock)ProtocolPanel.Children[0]).Width = 32; ProtocolBox.Width = 70;
        ((TextBlock)((StackPanel)BaudBox.Parent).Children[0]).Text = "波特率";
        SettingsPanel.Children.Remove(RefreshButton); ActionPanel.Children.Insert(2, RefreshButton);
        RefreshButton.Content = "刷新"; RefreshButton.ToolTip = "刷新串口列表"; RefreshButton.Margin = new Thickness(0, 0, 0, 0);
        ConnectButton.Content = "连接"; ConnectButton.MinHeight = 28; ConnectButton.Padding = new Thickness(12, 4, 12, 4);
        StateText.Text = "未连接 · 串口 8N1，无流控"; StateText.Width = 220; StateText.FontSize = 11; StateText.Margin = new Thickness(0, 6, 0, 0);
    }

    private DebugToolTransport SelectedTransport => DebugToolConnectionChoices.Resolve(
        TransportBox.SelectedValue is DebugToolTransport type ? type : DebugToolTransport.Serial,
        ProtocolBox.SelectedValue is TransportProtocol protocol ? protocol : TransportProtocol.Tcp);

    private void Transport_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (HostPanel is null) return;
        var transport = SelectedTransport;
        var serial = transport == DebugToolTransport.Serial;
        var client = transport is DebugToolTransport.TcpClient or DebugToolTransport.UdpTarget;
        SerialPanel.Visibility = BaudPanel.Visibility = RefreshButton.Visibility = serial ? Visibility.Visible : Visibility.Collapsed;
        ProtocolPanel.Visibility = serial ? Visibility.Collapsed : Visibility.Visible;
        HostPanel.Visibility = PortPanel.Visibility = serial ? Visibility.Collapsed : Visibility.Visible;
        HostLabel.Text = client ? "远程地址" : "本机监听地址";
        if (HostBox.Text is "0.0.0.0" or "127.0.0.1") HostBox.Text = client ? "127.0.0.1" : "0.0.0.0";
        if (_embedded) RefreshButton.Visibility = Visibility.Collapsed;
    }

    private async Task RefreshAsync()
    {
        if (_serial is null || IsActive) return;
        RefreshButton.IsEnabled = false;
        try
        {
            var ports = (await _serial.ScanAsync()).ToList();
            if (IsActive) return;
            // An offline preset, or a selection made while scanning, must survive refresh.
            var previous = PortBox.SelectedItem as SerialPortDescriptor;
            if (previous is not null && ports.All(p => p.PortName != previous.PortName)) ports.Add(previous);
            PortBox.ItemsSource = ports;
            PortBox.SelectedItem = ports.FirstOrDefault(p => p.PortName == previous?.PortName) ?? ports.FirstOrDefault();
            ActivityChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { StateText.Text = ex.Message; }
        finally { RefreshButton.IsEnabled = true; }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_serial is null || IsActive) return;
        try
        {
            var endpoint = CaptureEndpoint(); DebugConnectionPreset.Validate(endpoint);
            Starting?.Invoke();
            _connection = new DebugToolConnection(_serial); _cancel = new CancellationTokenSource();
            SettingsPanel.IsEnabled = ConnectButton.IsEnabled = false; StopButton.IsEnabled = true;
            SetStatus("连接中");
            _run = RunAsync(_connection, endpoint, _cancel);
        }
        catch (Exception ex) { SetStatus("连接失败", ex.Message); }
    }

    private async Task RunAsync(DebugToolConnection connection, DebugToolEndpoint endpoint, CancellationTokenSource cancel)
    {
        try
        {
            await Task.Run(() => connection.RunAsync(endpoint, data => ReceiveAsync?.Invoke(data) ?? Task.CompletedTask,
                state => Dispatcher.BeginInvoke(new Action(() => { if (_cancel == cancel) SetStatus(state.Contains("监听", StringComparison.Ordinal) ? "监听中" : "已连接", state); })), cancel.Token));
            SetStatus("已断开");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { SetStatus("已断开"); }
        catch (Exception) when (cancel.IsCancellationRequested) { SetStatus("已断开"); }
        catch (Exception ex) { SetStatus("连接失败", "连接停止：" + ex.Message); }
        finally
        {
            Task[] sends;
            lock (_sendLock) { _connection = null; cancel.Cancel(); sends = _sends.ToArray(); }
            try { await Task.WhenAll(sends); } catch (Exception) { /* Each sender reports its own failure. */ }
            connection.Dispose(); cancel.Dispose(); _cancel = null;
            SettingsPanel.IsEnabled = ConnectButton.IsEnabled = true; StopButton.IsEnabled = false;
            ActivityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task StopAsync()
    {
        _cancel?.Cancel();
        if (_run is { } run) await run;
    }

    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopAsync();
    public Task SendAsync(ReadOnlyMemory<byte> data)
    {
        Task send;
        lock (_sendLock)
        {
            if (_connection is null || _cancel is null || _cancel.IsCancellationRequested) throw new InvalidOperationException("尚未连接。");
            send = _connection.SendAsync(data, _cancel.Token); _sends.Add(send);
        }
        return CompleteSendAsync(send);
    }
    private async Task CompleteSendAsync(Task send)
    {
        try { await send.ConfigureAwait(false); }
        finally { lock (_sendLock) _sends.Remove(send); }
    }
}
