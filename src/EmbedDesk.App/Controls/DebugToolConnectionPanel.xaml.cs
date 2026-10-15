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
    public bool IsActive => _cancel is not null;
    public Func<ReadOnlyMemory<byte>, Task>? ReceiveAsync { get; set; }
    public Action? Starting { get; set; }
    public event EventHandler? ActivityChanged;

    public DebugToolConnectionPanel()
    {
        InitializeComponent();
        TransportBox.ItemsSource = new[] { "串口", "TCP 客户端", "TCP 服务端", "UDP 监听" }; TransportBox.SelectedIndex = 0;
        BaudBox.ItemsSource = new[] { 9600, 57600, 115200, 230400, 460800, 921600, 2000000 }; BaudBox.Text = "115200";
    }

    public void Initialize(ISerialPortService serial) { _serial = serial; Loaded += async (_, _) => await RefreshAsync(); }

    public void UseCompactLayout()
    {
        foreach (var field in SettingsPanel.Children.OfType<StackPanel>())
        {
            field.Orientation = Orientation.Horizontal; field.Width = 220; field.Margin = new Thickness(0, 0, 0, 5);
            if (field.Children[0] is TextBlock label) { label.Width = 62; label.FontSize = 11; label.Margin = new Thickness(0); label.VerticalAlignment = VerticalAlignment.Center; }
            if (field.Children[1] is FrameworkElement editor) editor.Width = 158;
        }
        ((TextBlock)((StackPanel)BaudBox.Parent).Children[0]).Text = "波特率";
        SettingsPanel.Children.Remove(RefreshButton); ActionPanel.Children.Insert(2, RefreshButton);
        RefreshButton.Content = "刷新"; RefreshButton.ToolTip = "刷新串口列表"; RefreshButton.Margin = new Thickness(0, 0, 0, 0);
        ConnectButton.Content = "连接"; ConnectButton.MinHeight = 28; ConnectButton.Padding = new Thickness(12, 4, 12, 4);
        StateText.Text = "未连接 · 串口 8N1，无流控"; StateText.Width = 220; StateText.FontSize = 11; StateText.Margin = new Thickness(0, 6, 0, 0);
    }

    private void Transport_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (HostPanel is null) return;
        var serial = TransportBox.SelectedIndex == 0;
        SerialPanel.Visibility = BaudPanel.Visibility = RefreshButton.Visibility = serial ? Visibility.Visible : Visibility.Collapsed;
        HostPanel.Visibility = PortPanel.Visibility = serial ? Visibility.Collapsed : Visibility.Visible;
        HostLabel.Text = TransportBox.SelectedIndex == 1 ? "远程地址" : "本机监听 IPv4";
    }

    private async Task RefreshAsync()
    {
        if (_serial is null || IsActive) return;
        RefreshButton.IsEnabled = false;
        try
        {
            var previous = (PortBox.SelectedItem as SerialPortDescriptor)?.PortName;
            var ports = await _serial.ScanAsync();
            PortBox.ItemsSource = ports; PortBox.SelectedItem = ports.FirstOrDefault(p => p.PortName == previous) ?? ports.FirstOrDefault();
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
            var transport = (DebugToolTransport)TransportBox.SelectedIndex;
            SerialPortSettings? serial = null;
            if (transport == DebugToolTransport.Serial)
            {
                if (PortBox.SelectedItem is not SerialPortDescriptor port) throw new InvalidOperationException("请选择串口。");
                if (!int.TryParse(BaudBox.Text, out var baud) || baud is < 50 or > 12000000) throw new FormatException("波特率应为 50–12,000,000。");
                serial = new SerialPortSettings { PortName = port.PortName, BaudRate = baud };
            }
            if (!int.TryParse(NetworkPortBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var networkPort) || networkPort is < 1 or > 65535) throw new FormatException("端口应为 1–65535。");
            Starting?.Invoke();
            _connection = new DebugToolConnection(_serial); _cancel = new CancellationTokenSource();
            SettingsPanel.IsEnabled = ConnectButton.IsEnabled = false; StopButton.IsEnabled = true; StateText.Text = "正在连接…";
            ActivityChanged?.Invoke(this, EventArgs.Empty);
            _run = RunAsync(_connection, new(transport, HostBox.Text.Trim(), networkPort, serial), _cancel);
        }
        catch (Exception ex) { StateText.Text = ex.Message; }
    }

    private async Task RunAsync(DebugToolConnection connection, DebugToolEndpoint endpoint, CancellationTokenSource cancel)
    {
        try
        {
            await Task.Run(() => connection.RunAsync(endpoint, data => ReceiveAsync?.Invoke(data) ?? Task.CompletedTask,
                state => Dispatcher.BeginInvoke(new Action(() => { if (_cancel == cancel) StateText.Text = state; })), cancel.Token));
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { StateText.Text = "已断开"; }
        catch (Exception ex) { StateText.Text = "连接停止：" + ex.Message; }
        finally
        {
            cancel.Dispose(); _cancel = null; _connection = null;
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
    public Task SendAsync(ReadOnlyMemory<byte> data) => _connection?.SendAsync(data, _cancel?.Token ?? default) ?? throw new InvalidOperationException("尚未连接。");
}
