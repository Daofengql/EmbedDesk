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

public partial class FirmwareToolWindow : Window
{
    private readonly ISerialPortService _serial;
    private ISerialPortChannel? _channel;
    private Stm32BootloaderClient? _stm32;
    private string? _firmwarePath;
    private FirmwareImageSummary? _summary;
    private bool _busy;
    private bool _closed;

    public FirmwareToolWindow(ISerialPortService serial, int initialProtocol = 0)
    {
        InitializeComponent();
        _serial = serial;
        ProtocolBox.ItemsSource = new[] { "STM32 UART Bootloader", "C51 ISP（STC / 通用串口入口）", "CH347T SPI NAND" };
        ProtocolBox.SelectedIndex = Math.Clamp(initialProtocol, 0, 2);
        BaudBox.ItemsSource = new[] { "9600", "19200", "38400", "57600", "115200", "230400", "460800" };
        BaudBox.SelectedItem = "115200";
        WindowTheme.ApplyDarkTitleBar(this);
        Closed += async (_, _) => { _closed = true; await DisposeTransportAsync(); };
        Loaded += async (_, _) => await RefreshPortsAsync();
    }

    private async void AnalyzeFile_Click(object sender, RoutedEventArgs e) => await ChooseAndAnalyzeAsync();
    private async void SelectFirmware_Click(object sender, RoutedEventArgs e) => await ChooseAndAnalyzeAsync();

    private async Task ChooseAndAnalyzeAsync()
    {
        if (_busy) return;
        var dialog = new OpenFileDialog { Title = "选择固件", Filter = "固件|*.bin;*.hex;*.ihx;*.s19;*.mot|所有文件|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        _firmwarePath = dialog.FileName;
        await AnalyzeAsync();
    }

    private async Task AnalyzeAsync()
    {
        if (_firmwarePath is null) return;
        SetBusy(true);
        try
        {
            _summary = await FirmwareImageAnalyzer.AnalyzeAsync(_firmwarePath);
            AnalysisBox.Text = FormatSummary(_summary);
            FlashLogBox.AppendText($"固件：{_firmwarePath}\n{_summary.Format} · {_summary.DataBytes:N0} 数据字节 · SHA256 {_summary.Sha256}\n");
            FlashStatus.Text = "固件已分析，可以选择协议并连接。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        { AnalysisBox.Text = "分析失败：" + ex.Message; FlashStatus.Text = "固件格式无效。"; }
        finally { SetBusy(false); }
    }

    private static string FormatSummary(FirmwareImageSummary summary) =>
        $"文件：{summary.FilePath}\n格式：{summary.Format}\n文件大小：{summary.Size:N0} 字节\n有效数据：{summary.DataBytes:N0} 字节\n地址范围：{summary.AddressRange}\n记录数：{summary.RecordCount:N0}\nSHA-256：{summary.Sha256}\n" +
        (summary.Warnings.Count == 0 ? "校验：通过" : "警告：\n" + string.Join("\n", summary.Warnings.Select(w => "· " + w)));

    private async void RefreshPorts_Click(object sender, RoutedEventArgs e) => await RefreshPortsAsync();
    private async Task RefreshPortsAsync()
    {
        try
        {
            var ports = await _serial.ScanAsync();
            PortBox.ItemsSource = ports;
            PortBox.DisplayMemberPath = nameof(SerialPortDescriptor.DisplayName);
            PortBox.SelectedIndex = 0;
        }
        catch (Exception ex) { FlashStatus.Text = "扫描串口失败：" + ex.Message; }
    }

    private void Protocol_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ProtocolHint is null) return;
        ProtocolHint.Text = ProtocolBox.SelectedIndex switch
        {
            0 => "STM32：使用系统存储器中的 AN3155 USART Bootloader。连接前请让芯片进入系统 Bootloader，工具会执行同步、读取 ID、全片擦除和分块写入。",
            1 => "C51：C51 不是单一烧录协议，STC、Nuvoton、Silicon Labs 等型号的握手不同。这里保留统一固件分析入口，后续按具体型号添加协议配置。",
            _ => "CH347T：SPI NAND 需要芯片 ID、页大小、块大小、ECC 和坏块策略。选择具体 NAND 型号后再启用写入，避免损坏 NAND。"
        };
        ConnectButton.IsEnabled = ProtocolBox.SelectedIndex == 0;
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || ProtocolBox.SelectedIndex != 0 || PortBox.SelectedItem is not SerialPortDescriptor descriptor) return;
        if (!int.TryParse(BaudBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var baud) || baud is < 1200 or > 3000000)
        { FlashStatus.Text = "波特率无效。"; return; }
        await DisposeTransportAsync();
        SetBusy(true);
        try
        {
            _channel = _serial.CreateChannel(new SerialPortSettings { PortName = descriptor.PortName, BaudRate = baud, DataBits = 8, Parity = Parity.Even, StopBits = StopBits.One, Handshake = Handshake.None });
            await _channel.OpenAsync();
            _stm32 = new Stm32BootloaderClient(_channel);
            await _stm32.ConnectAsync();
            var id = await _stm32.ReadDeviceIdAsync();
            FlashLogBox.AppendText($"STM32 Bootloader 已连接，设备 ID：0x{id:X4}\n");
            FlashStatus.Text = "已连接，可以擦除或写入。";
            EraseButton.IsEnabled = WriteButton.IsEnabled = true;
            ConnectButton.Content = "已连接";
        }
        catch (Exception ex) { FlashStatus.Text = "连接失败：" + ex.Message; await DisposeTransportAsync(); }
        finally { SetBusy(false); }
    }

    private async void Erase_Click(object sender, RoutedEventArgs e)
    {
        if (_stm32 is null || _busy) return;
        SetBusy(true);
        try { await _stm32.MassEraseAsync(); FlashLogBox.AppendText("全片擦除完成。\n"); FlashStatus.Text = "擦除完成。"; }
        catch (Exception ex) { FlashStatus.Text = "擦除失败：" + ex.Message; }
        finally { SetBusy(false); }
    }

    private async void Write_Click(object sender, RoutedEventArgs e)
    {
        if (_stm32 is null || _busy || _firmwarePath is null) return;
        if (!TryParseAddress(AddressBox.Text, out var binaryAddress)) { FlashStatus.Text = "BIN 起始地址无效。"; return; }
        SetBusy(true);
        try
        {
            var segments = await FirmwareImageLoader.LoadAsync(_firmwarePath, binaryAddress);
            var total = segments.Sum(segment => (long)segment.Data.Length); var done = 0L;
            foreach (var segment in segments)
            {
                var baseDone = done;
                var progress = new Progress<long>(value => FlashProgress.Value = total == 0 ? 0 : (baseDone + value) * 100d / total);
                await _stm32.WriteMemoryAsync(segment.Address, segment.Data, progress);
                done += segment.Data.Length;
            }
            FlashProgress.Value = 100; FlashLogBox.AppendText($"写入完成：{done:N0} 字节，地址段 {segments.Count} 个。\n"); FlashStatus.Text = "写入完成；建议复位设备后进行业务校验。";
        }
        catch (Exception ex) { FlashStatus.Text = "写入失败：" + ex.Message; }
        finally { SetBusy(false); }
    }

    private static bool TryParseAddress(string text, out uint value)
    {
        text = text.Trim(); var style = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? NumberStyles.HexNumber : NumberStyles.Integer;
        if (style == NumberStyles.HexNumber) text = text[2..];
        return uint.TryParse(text, style, CultureInfo.InvariantCulture, out value);
    }

    private void ClearFile_Click(object sender, RoutedEventArgs e) { _firmwarePath = null; _summary = null; AnalysisBox.Clear(); FlashStatus.Text = "未选择固件。"; }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        if (_closed) return;
        AnalyzeFileButton.IsEnabled = SelectFirmwareButton.IsEnabled = RefreshPortsButton.IsEnabled = !busy;
        ConnectButton.IsEnabled = !busy && ProtocolBox.SelectedIndex == 0 && _stm32 is null;
        EraseButton.IsEnabled = WriteButton.IsEnabled = !busy && _stm32 is not null;
        if (busy) FlashStatus.Text = "处理中…";
    }

    private async Task DisposeTransportAsync()
    {
        EraseButton.IsEnabled = WriteButton.IsEnabled = false;
        if (_stm32 is not null) { await _stm32.DisposeAsync(); _stm32 = null; _channel = null; }
        else if (_channel is not null) { await _channel.DisposeAsync(); _channel = null; }
        if (!_closed && ConnectButton is not null) { ConnectButton.Content = "连接 Bootloader"; ConnectButton.IsEnabled = ProtocolBox.SelectedIndex == 0; }
    }
}
