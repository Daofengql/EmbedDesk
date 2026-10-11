using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Windows;
using EmbeddedDebugger.Core.Abstractions;
using EmbeddedDebugger.Core.Models;
using EmbeddedDebugger.Infrastructure.Firmware;
using Microsoft.Win32;

namespace EmbeddedDebugger.App;

public partial class EspConnectToolWindow : Window
{
    private readonly ISerialPortService _serial;
    private ISerialPortChannel? _channel;
    private EspRomBootloaderClient? _esp;
    private string? _firmwarePath;
    private bool _busy;
    private bool _closed;

    public EspConnectToolWindow(ISerialPortService serial)
    {
        InitializeComponent();
        _serial = serial;
        BaudBox.ItemsSource = new[] { "115200", "230400", "460800", "921600" };
        BaudBox.SelectedItem = "115200";
        WindowTheme.ApplyDarkTitleBar(this);
        Loaded += async (_, _) => await RefreshPortsAsync();
        Closed += async (_, _) => { _closed = true; await DisconnectAsync(); };
    }

    private async void RefreshPorts_Click(object sender, RoutedEventArgs e) => await RefreshPortsAsync();

    private async Task RefreshPortsAsync()
    {
        try
        {
            var ports = await _serial.ScanAsync();
            PortBox.ItemsSource = ports;
            PortBox.DisplayMemberPath = nameof(SerialPortDescriptor.DisplayName);
            PortBox.SelectedIndex = 0;
            StatusText.Text = $"扫描到 {ports.Count} 个串口。请让 ESP 进入下载模式后连接。";
        }
        catch (Exception ex) { StatusText.Text = "扫描串口失败：" + ex.Message; }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_esp is not null) { await DisconnectAsync(); return; }
        if (PortBox.SelectedItem is not SerialPortDescriptor descriptor) { StatusText.Text = "请选择串口。"; return; }
        if (!int.TryParse(BaudBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var baud) || baud is < 1200 or > 3_000_000)
        { StatusText.Text = "波特率无效。"; return; }

        SetBusy(true);
        try
        {
            _channel = _serial.CreateChannel(new SerialPortSettings { PortName = descriptor.PortName, BaudRate = baud, DataBits = 8, Parity = Parity.None, StopBits = StopBits.One, Handshake = Handshake.None, DtrEnable = false, RtsEnable = false });
            await _channel.OpenAsync();
            _esp = new EspRomBootloaderClient(_channel);
            StatusText.Text = "正在同步 ROM Bootloader…如果没有响应，请按住 BOOT 后点 RESET。";
            await _esp.ConnectAsync();
            var chipId = await _esp.TryReadChipIdAsync();
            ChipFamilyText.Text = _esp.ChipFamily;
            ChipIdText.Text = chipId is null ? "ROM 未提供统一 ID" : $"0x{chipId:X8}";
            PortText.Text = $"{descriptor.DisplayName} · {baud} 8N1";
            DeviceSummary.Text = $"已连接：{_esp.ChipFamily}";
            ConnectionState.Text = "已连接";
            ConnectButton.Content = "断开";
            MonitorButton.IsEnabled = true;
            FlashButton.IsEnabled = _firmwarePath is not null;
            FlashStatus.Text = "已连接，可以烧录。";
            StatusText.Text = "ESP ROM 握手完成。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "连接失败：" + ex.Message;
            await DisconnectAsync();
        }
        finally { SetBusy(false); }
    }

    private async Task DisconnectAsync()
    {
        MonitorButton.IsEnabled = false;
        SendMonitorButton.IsEnabled = false;
        if (_esp is not null) await _esp.DisposeAsync();
        else if (_channel is not null) await _channel.DisposeAsync();
        _esp = null;
        _channel = null;
        if (!_closed && ConnectButton is not null)
        {
            ConnectButton.Content = "连接";
            ConnectionState.Text = "未连接";
            DeviceSummary.Text = "选择串口后连接到下载模式。";
            FlashButton.IsEnabled = false;
            StatusText.Text = "已断开。";
        }
    }

    private void SelectFirmware_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择 ESP 固件", Filter = "ESP 固件|*.bin|所有文件|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        _firmwarePath = dialog.FileName;
        FirmwarePathBox.Text = _firmwarePath;
        var info = new FileInfo(_firmwarePath);
        FlashLogBox.AppendText($"固件：{info.Name} · {info.Length:N0} 字节\n");
        FlashStatus.Text = _esp is null ? "固件已选择，请先连接 ESP。" : "固件已选择，可以烧录。";
        FlashButton.IsEnabled = _esp is not null;
    }

    private async void Flash_Click(object sender, RoutedEventArgs e)
    {
        if (_esp is null || _firmwarePath is null || _busy) return;
        if (!TryParseAddress(AddressBox.Text, out var address)) { FlashStatus.Text = "烧录地址无效。"; return; }
        SetBusy(true);
        try
        {
            var bytes = await File.ReadAllBytesAsync(_firmwarePath);
            FlashProgress.Value = 0;
            var progress = new Progress<long>(value => FlashProgress.Value = bytes.Length == 0 ? 0 : value * 100d / bytes.Length);
            FlashLogBox.AppendText($"开始写入 0x{address:X8}，目标区域会先擦除…\n");
            await _esp.FlashAsync(address, bytes, progress, reboot: true);
            FlashProgress.Value = 100;
            FlashLogBox.AppendText("写入完成，设备已复位。\n");
            FlashStatus.Text = "烧录完成。";
            StatusText.Text = "ESP 固件烧录完成。";
        }
        catch (Exception ex) { FlashStatus.Text = "烧录失败：" + ex.Message; FlashLogBox.AppendText("失败：" + ex.Message + "\n"); }
        finally { SetBusy(false); }
    }

    private void Monitor_Click(object sender, RoutedEventArgs e)
    {
        if (_esp is null) return;
        if (MonitorButton.Content is string text && text == "停止监视器")
        {
            _esp.StopMonitor();
            MonitorButton.Content = "启动监视器";
            SendMonitorButton.IsEnabled = false;
            return;
        }
        _esp.StartMonitor(OnMonitorData);
        MonitorButton.Content = "停止监视器";
        SendMonitorButton.IsEnabled = true;
        WorkTabs.SelectedIndex = 2;
        StatusText.Text = "串口监视器已启动。";
    }

    private void OnMonitorData(object? sender, ReadOnlyMemory<byte> data)
    {
        var text = Encoding.UTF8.GetString(data.Span);
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed) return;
            MonitorBox.AppendText(text);
            if (MonitorBox.Text.Length > 250_000) MonitorBox.Text = MonitorBox.Text[^200_000..];
            MonitorBox.ScrollToEnd();
        });
    }

    private async void SendMonitor_Click(object sender, RoutedEventArgs e)
    {
        if (_channel is null || !_channel.IsOpen || string.IsNullOrEmpty(MonitorInput.Text)) return;
        var bytes = Encoding.UTF8.GetBytes(MonitorInput.Text + "\r\n");
        await _channel.SendAsync(bytes);
        MonitorInput.Clear();
    }

    private void ClearMonitor_Click(object sender, RoutedEventArgs e) => MonitorBox.Clear();

    private static bool TryParseAddress(string text, out uint value)
    {
        text = text.Trim();
        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (hex) text = text[2..];
        return uint.TryParse(text, hex ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        if (_closed) return;
        ConnectButton.IsEnabled = !busy;
        PortBox.IsEnabled = !busy && _esp is null;
        BaudBox.IsEnabled = !busy && _esp is null;
        FlashButton.IsEnabled = !busy && _esp is not null && _firmwarePath is not null;
        if (busy) StatusText.Text = "处理中…";
    }
}
