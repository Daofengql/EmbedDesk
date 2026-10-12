using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using EmbedDesk.Core.Abstractions;
using EmbedDesk.Core.Models;
using EmbedDesk.Infrastructure.Firmware;
using EmbedDesk.App.ViewModels;
using Microsoft.Win32;

namespace EmbedDesk.App;

public partial class EspConnectToolWindow : Window
{
    public sealed class FirmwareRow
    {
        public string Address { get; set; } = "0x10000";
        public string Path { get; init; } = "";
        public string SizeText { get; init; } = "";
    }
    private readonly ISerialPortService _serial;
    private readonly MainWindowViewModel? _workspace;
    private readonly SerialPortAutoRefresh _serialPortAutoRefresh;
    private readonly ObservableCollection<FirmwareRow> _firmware = [];
    private IReadOnlyList<EspPartition> _partitions = [];
    private ISerialPortChannel? _channel;
    private EspRomBootloaderClient? _esp;
    private CancellationTokenSource? _operation;
    private readonly object _monitorLock = new();
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _pendingMonitor = new();
    private readonly DispatcherTimer _monitorTimer;
    private bool _closed;

    public EspConnectToolWindow(ISerialPortService serial, MainWindowViewModel? workspace = null)
    {
        InitializeComponent();
        _serial = serial;
        _workspace = workspace;
        _serialPortAutoRefresh = new SerialPortAutoRefresh(_serial, Dispatcher, RefreshPortsAsync);
        BaudBox.ItemsSource = new[] { 115200, 230400, 460800, 921600 };
        BaudBox.SelectedItem = 115200;
        MonitorBaudBox.ItemsSource = new[] { 9600, 19200, 38400, 57600, 74880, 115200, 230400, 460800, 921600 };
        MonitorBaudBox.SelectedItem = 115200;
        FirmwareGrid.ItemsSource = _firmware;
        SupportedText.Text = "支持芯片：" + EspRomBootloaderClient.SupportedChips;
        WindowTheme.ApplyDarkTitleBar(this);
        _monitorTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => FlushMonitor(), Dispatcher);
        Loaded += async (_, _) => await RefreshPortsAsync();
        Closed += async (_, _) =>
        {
            _closed = true;
            _serialPortAutoRefresh.Dispose();
            _monitorTimer.Stop();
            _operation?.Cancel();
            try { await DisconnectAsync(); } catch { /* Closing still releases the connection. */ }
        };
    }

    private async Task RefreshPortsAsync()
    {
        if (_closed || _operation is not null || _channel is not null) return;
        try
        {
            var selected = (PortBox.SelectedItem as SerialPortDescriptor)?.PortName;
            var ports = await _serial.ScanAsync();
            if (_closed) return;
            PortBox.ItemsSource = ports;
            PortBox.SelectedItem = ports.FirstOrDefault(port => port.PortName == selected) ?? ports.FirstOrDefault();
            StatusText.Text = $"扫描到 {ports.Count} 个串口。";
        }
        catch (Exception ex) { ReportError(ex); }
    }

    private async Task OpenChannelAsync(int baud, CancellationToken token)
    {
        if (_channel is not null)
        {
            if (_channel.Settings.BaudRate == baud) return;
            if (_channel is ISerialPortControl control) { await control.SetBaudRateAsync(baud, token); return; }
            await DisconnectAsync();
        }
        if (PortBox.SelectedItem is not SerialPortDescriptor descriptor) throw new InvalidOperationException("请选择串口。");
        if (!await SerialPortUsePrompt.EnsureExclusiveAsync(this, _workspace, descriptor.PortName))
            throw new OperationCanceledException("未释放调试串口，下载连接已取消。");
        _channel = _serial.CreateChannel(new SerialPortSettings { PortName = descriptor.PortName, BaudRate = baud, DataBits = 8, Parity = Parity.None, StopBits = StopBits.One, Handshake = Handshake.None });
        try { await _channel.OpenAsync(token); _esp = new EspRomBootloaderClient(_channel); }
        catch { await DisconnectAsync(); throw; }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e) => await RunOperationAsync(async token =>
    {
        _esp?.StopMonitor();
        var baud = (int)BaudBox.SelectedItem;
        await OpenChannelAsync(baud, token);
        Log("连接 ROM 下载模式…" + (AutoResetBox.IsChecked == true ? " 自动 BOOT / RESET。" : " 请手动按住 BOOT 后点 RESET。"));
        try { await _esp!.ConnectAsync(AutoResetBox.IsChecked == true, token); }
        catch { await DisconnectAsync(); throw; }
        ChipFamilyText.Text = _esp.ChipFamily;
        ChipIdText.Text = $"0x{_esp.ChipId:X8}（芯片型号，不是唯一序列号）";
        FlashInfoText.Text = $"{_esp.FlashSize / 1048576d:N2} MiB · JEDEC 0x{_esp.FlashId:X6}";
        SecurityText.Text = _esp.SecurityDescription;
        PortText.Text = $"{_channel!.Settings.PortName} · {baud} 8N1";
        DeviceSummary.Text = $"{_esp.ChipFamily} · {FlashInfoText.Text}";
        ReadLengthBox.Text = $"0x{_esp.FlashSize:X}";
        Log($"已连接 {_esp.ChipFamily}，{FlashInfoText.Text}。{_esp.SecurityDescription}");
    });

    private async void Disconnect_Click(object sender, RoutedEventArgs e) => await RunOperationAsync(_ => DisconnectAsync());
    private async Task DisconnectAsync()
    {
        var esp = _esp;
        var channel = _channel;
        _esp = null;
        _channel = null;
        if (esp is not null) await esp.DisposeAsync();
        else if (channel is not null) await channel.DisposeAsync();
        if (!_closed) UpdateControls();
    }

    private void SelectFirmware_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "添加 ESP 固件（请确认各文件的烧录地址）", Filter = "ESP 固件|*.bin|所有文件|*.*", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var path in dialog.FileNames)
        {
            var name = Path.GetFileName(path).ToLowerInvariant();
            var address = name.Contains("partition", StringComparison.Ordinal) ? "0x8000"
                : name.Contains("boot_app0", StringComparison.Ordinal) ? "0xE000"
                : name.Contains("bootloader", StringComparison.Ordinal) ? (_esp?.ChipFamily == "ESP32" || _esp?.ChipFamily == "ESP32-S2" ? "0x1000" : "0x0")
                : name.Contains("merged", StringComparison.Ordinal) || _esp?.ChipFamily == "ESP8266" ? "0x0" : "0x10000";
            _firmware.Add(new() { Address = address, Path = path, SizeText = $"{new FileInfo(path).Length:N0} B" });
        }
        Log("已添加固件。地址只是按文件名建议，请按项目构建输出核对。");
        UpdateControls();
    }
    private void RemoveFirmware_Click(object sender, RoutedEventArgs e)
    {
        if (FirmwareGrid.SelectedItem is FirmwareRow row) _firmware.Remove(row);
        UpdateControls();
    }

    private async Task<List<EspFlashImage>> LoadImagesAsync(CancellationToken token)
    {
        FirmwareGrid.CommitEdit();
        var images = new List<EspFlashImage>();
        foreach (var row in _firmware)
        {
            if (new FileInfo(row.Path).Length is <= 0 or > 16 * 1024 * 1024) throw new InvalidDataException("固件必须为 1 字节～16 MiB。");
            images.Add(new(ParseNumber(row.Address, "烧录地址"), await File.ReadAllBytesAsync(row.Path, token), Path.GetFileName(row.Path)));
        }
        EspRomBootloaderClient.ValidateImages(images, _esp!.FlashSize);
        return images;
    }

    private async void Flash_Click(object sender, RoutedEventArgs e) => await RunOperationAsync(async token =>
    {
        var images = await LoadImagesAsync(token);
        var details = string.Join("\n", images.Select(image => $"{image.Name} → 0x{image.Address:X8}，{image.Data.Length:N0} 字节"));
        if (!Confirm($"目标：{_esp!.ChipFamily} / {_channel!.Settings.PortName}\n\n{details}\n\n上述文件覆盖的整扇区会先擦除，然后烧录和校验。是否继续？", "确认 ESP 烧录")) return;
        await _esp.FlashImagesAsync(images, CreateProgress(), token);
        Log("全部文件写入并完成 Flash 内容校验。");
        if (RebootAfterBox.IsChecked == true) { await _esp.RebootAsync(token); Log("设备已复位运行；下次操作前需重新连接下载模式。"); }
    });

    private async void Verify_Click(object sender, RoutedEventArgs e) => await RunOperationAsync(async token =>
    {
        foreach (var image in await LoadImagesAsync(token))
        {
            Log($"校验 {image.Name} / 0x{image.Address:X8}…");
            await _esp!.VerifyAsync(image.Address, image.Data, token);
            Log($"{image.Name} 与 Flash 内容一致。");
        }
    });

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        try { await SaveRangeAsync(ParseNumber(ReadAddressBox.Text, "读取地址"), checked((int)ParseNumber(ReadLengthBox.Text, "读取长度")), "esp-backup.bin"); }
        catch (Exception ex) { ReportError(ex); }
    }
    private void WholeFlash_Click(object sender, RoutedEventArgs e)
    {
        if (_esp is null) return;
        ReadAddressBox.Text = "0x0";
        ReadLengthBox.Text = $"0x{_esp.FlashSize:X}";
    }
    private async Task SaveRangeAsync(uint address, int length, string filename)
    {
        var dialog = new SaveFileDialog { Title = "备份 Flash（ROM 读取速度较慢，可取消）", Filter = "原始二进制|*.bin", FileName = filename };
        if (dialog.ShowDialog(this) != true) return;
        await RunOperationAsync(async token =>
        {
            var temporary = dialog.FileName + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                    await _esp!.ReadFlashAsync(address, length, stream, CreateProgress(), token);
                File.Move(temporary, dialog.FileName, true);
                Log($"已备份 0x{address:X8} / {length:N0} 字节到 {dialog.FileName}");
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        });
    }
    private async void Erase_Click(object sender, RoutedEventArgs e) => await RunOperationAsync(async token =>
    {
        var address = ParseNumber(ReadAddressBox.Text, "擦除地址");
        var length = checked((int)ParseNumber(ReadLengthBox.Text, "擦除长度"));
        if (!Confirm($"目标：{_esp!.ChipFamily} / {_channel!.Settings.PortName}\n范围：0x{address:X8}，{length:N0} 字节\n\n将清除该区域内的固件、配置或文件，写入 0xFF 并校验。是否继续？", "确认擦除 Flash")) return;
        await _esp.EraseAsync(address, length, CreateProgress(), token);
        Log("指定范围已擦除并通过空白校验。");
    });

    private async void ReadPartitions_Click(object sender, RoutedEventArgs e) => await RunOperationAsync(async token =>
    {
        var bytes = await _esp!.ReadFlashAsync(ParseNumber(PartitionAddressBox.Text, "分区表地址"), 4096, token);
        SetPartitions(EspPartitionTable.Parse(bytes, _esp.FlashSize));
    });
    private async void ImportPartitions_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "打开 ESP-IDF 分区表", Filter = "分区表|*.bin" };
        if (dialog.ShowDialog(this) != true) return;
        await RunOperationAsync(async token =>
        {
            if (new FileInfo(dialog.FileName).Length > 4096) throw new InvalidDataException("请选择单独的分区表 .bin（不超过 4096 字节）。");
            SetPartitions(EspPartitionTable.Parse(await File.ReadAllBytesAsync(dialog.FileName, token)));
        });
    }
    private void SetPartitions(IReadOnlyList<EspPartition> partitions)
    {
        _partitions = partitions;
        PartitionGrid.ItemsSource = _partitions;
        Log($"识别到 {partitions.Count} 个分区。分区格式、地址重叠和附带 MD5 已检查。");
    }
    private async void ExportPartitions_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "导出分区表", Filter = "ESP-IDF CSV|*.csv", FileName = "partitions.csv" };
        if (dialog.ShowDialog(this) != true) return;
        await RunOperationAsync(async token => { await File.WriteAllTextAsync(dialog.FileName, EspPartitionTable.ToCsv(_partitions), new UTF8Encoding(false), token); Log("已导出分区 CSV。"); });
    }
    private async void ExportPartition_Click(object sender, RoutedEventArgs e)
    {
        if (PartitionGrid.SelectedItem is not EspPartition partition) { StatusText.Text = "请先选择分区。"; return; }
        var name = string.Concat(partition.Label.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        await SaveRangeAsync(partition.Offset, checked((int)partition.Size), name + ".bin");
    }

    private async void Reboot_Click(object sender, RoutedEventArgs e) => await RunOperationAsync(async token => { await _esp!.RebootAsync(token); Log("设备已复位运行。"); });
    private async void Monitor_Click(object sender, RoutedEventArgs e) => await RunOperationAsync(async token =>
    {
        if (_esp?.IsMonitoring == true) { _esp.StopMonitor(); Log("监视器已停止。烧录前请重新连接下载模式。"); return; }
        if (_esp?.IsConnected == true) await _esp.RebootAsync(token);
        await OpenChannelAsync((int)MonitorBaudBox.SelectedItem, token);
        lock (_monitorLock) { _decoder.Reset(); _pendingMonitor.Clear(); }
        _esp!.StartMonitor(OnMonitorData);
        Log("串口监视器已启动。");
    });
    private void OnMonitorData(object? sender, ReadOnlyMemory<byte> data)
    {
        lock (_monitorLock)
        {
            var chars = new char[Encoding.UTF8.GetMaxCharCount(data.Length)];
            var count = _decoder.GetChars(data.Span, chars, false);
            _pendingMonitor.Append(chars, 0, count);
            if (_pendingMonitor.Length > 100_000) _pendingMonitor.Remove(0, _pendingMonitor.Length - 100_000);
        }
    }
    private void FlushMonitor()
    {
        string text;
        lock (_monitorLock) { if (_pendingMonitor.Length == 0) return; text = _pendingMonitor.ToString(); _pendingMonitor.Clear(); }
        if (_closed) return;
        MonitorBox.AppendText(text);
        if (MonitorBox.Text.Length > 250_000) MonitorBox.Text = MonitorBox.Text[^200_000..];
        MonitorBox.ScrollToEnd();
    }
    private async void SendMonitor_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var ending = LineEndingBox.SelectedIndex switch { 1 => "\n", 2 => "\r\n", _ => "" };
            if (_esp is not null) await _esp.SendMonitorAsync(Encoding.UTF8.GetBytes(MonitorInput.Text + ending));
            MonitorInput.Clear();
        }
        catch (Exception ex) { ReportError(ex); }
    }
    private void ClearMonitor_Click(object sender, RoutedEventArgs e)
    {
        lock (_monitorLock) { _decoder.Reset(); _pendingMonitor.Clear(); }
        MonitorBox.Clear();
    }

    private IProgress<EspOperationProgress> CreateProgress()
    {
        var operation = _operation;
        return new Progress<EspOperationProgress>(progress =>
        {
            if (_closed || !ReferenceEquals(operation, _operation)) return;
            FlashProgress.Value = progress.Total <= 0 ? 0 : progress.Completed * 100d / progress.Total;
            StatusText.Text = $"{progress.Stage} · {progress.Completed:N0} / {progress.Total:N0} 字节";
        });
    }
    private async Task RunOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (_operation is not null || _closed) return;
        using var cancellation = new CancellationTokenSource();
        _operation = cancellation;
        UpdateControls();
        FlashProgress.Value = 0;
        try { await operation(cancellation.Token); }
        catch (OperationCanceledException) { if (!_closed) Log("操作已取消。若已开始擦写，目标内容可能不完整；请重新连接后烧录。"); }
        catch (Exception ex) { ReportError(ex); }
        finally { _operation = null; if (!_closed) UpdateControls(); }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private bool Confirm(string text, string title) => MessageBox.Show(this, text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    private static uint ParseNumber(string text, string name)
    {
        text = text.Trim();
        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (!uint.TryParse(hex ? text[2..] : text, hex ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new ArgumentException($"{name}无效，请输入十进制或 0x 开头的十六进制数。");
        return value;
    }
    private void Log(string text)
    {
        if (_closed) return;
        StatusText.Text = text;
        FlashLogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\n");
        if (FlashLogBox.Text.Length > 100_000) FlashLogBox.Text = FlashLogBox.Text[^80_000..];
        FlashLogBox.ScrollToEnd();
    }
    private void ReportError(Exception ex) { if (!_closed) Log("失败：" + ex.Message); }
    private void UpdateControls()
    {
        var idle = _operation is null;
        var connected = _esp?.IsConnected == true;
        var monitoring = _esp?.IsMonitoring == true;
        var writable = connected && _esp?.WriteProtected == false;
        ConnectionState.Text = connected ? "ROM 已连接" : monitoring ? "串口监视中" : _channel is not null ? "需要连接下载模式" : "未连接";
        ConnectButton.IsEnabled = idle && !connected;
        DisconnectButton.IsEnabled = idle && _channel is not null;
        PortBox.IsEnabled = idle && _channel is null;
        BaudBox.IsEnabled = idle && !connected;
        AutoResetBox.IsEnabled = idle;
        AddFilesButton.IsEnabled = RemoveFileButton.IsEnabled = FirmwareGrid.IsEnabled = idle;
        FlashButton.IsEnabled = idle && writable && _firmware.Count > 0;
        VerifyButton.IsEnabled = idle && connected && _firmware.Count > 0;
        RebootAfterBox.IsEnabled = idle;
        BackupButton.IsEnabled = WholeFlashButton.IsEnabled = idle && connected;
        EraseButton.IsEnabled = idle && writable;
        ReadPartitionsButton.IsEnabled = idle && connected && _esp?.ChipFamily != "ESP8266";
        ImportPartitionsButton.IsEnabled = idle;
        ExportPartitionsButton.IsEnabled = idle && _partitions.Count > 0;
        ExportPartitionButton.IsEnabled = idle && connected && _partitions.Count > 0;
        ReadAddressBox.IsEnabled = ReadLengthBox.IsEnabled = PartitionAddressBox.IsEnabled = idle;
        RebootButton.IsEnabled = idle && connected;
        MonitorButton.IsEnabled = idle;
        MonitorButton.Content = monitoring ? "停止监视器" : "启动监视器";
        MonitorBaudBox.IsEnabled = idle && !monitoring;
        SendMonitorButton.IsEnabled = idle && monitoring;
        CancelButton.IsEnabled = !idle;
    }
}
