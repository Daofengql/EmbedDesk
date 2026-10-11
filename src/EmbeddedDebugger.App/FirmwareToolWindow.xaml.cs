using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Windows;
using EmbeddedDebugger.Core.Abstractions;
using EmbeddedDebugger.Core.Models;
using EmbeddedDebugger.Infrastructure.Firmware;
using EmbeddedDebugger.App.ViewModels;
using Microsoft.Win32;

namespace EmbeddedDebugger.App;

public partial class FirmwareToolWindow : Window
{
    private readonly ISerialPortService _serial;
    private readonly MainWindowViewModel? _workspace;
    private readonly SerialPortAutoRefresh _serialPortAutoRefresh;
    private ISerialPortChannel? _channel;
    private Stm32BootloaderClient? _stm32;
    private string? _firmwarePath;
    private CancellationTokenSource? _operation;
    private bool _closed;
    private bool _closeRequested;

    public FirmwareToolWindow(ISerialPortService serial, MainWindowViewModel? workspace = null, int initialProtocol = 0)
    {
        InitializeComponent();
        _serial = serial;
        _workspace = workspace;
        _serialPortAutoRefresh = new SerialPortAutoRefresh(_serial, Dispatcher, RefreshPortsAsync);
        BaudBox.ItemsSource = new[] { "9600", "19200", "38400", "57600", "115200", "230400", "460800" };
        BaudBox.SelectedItem = "115200";
        BootEntryBox.ItemsSource = new[] { "手动设置 BOOT0 并复位", "DTR / RTS 自动进入" };
        BootEntryBox.SelectedIndex = 0;
        ControlSequenceBox.ItemsSource = new[]
        {
            "DTR=0 / RTS=1 → DTR=1 / RTS=0",
            "DTR=1 / RTS=0 → DTR=0 / RTS=1",
            "DTR=0 / RTS=0 → DTR=1 / RTS=1"
        };
        ControlSequenceBox.SelectedIndex = 0;
        BootEntryBox.SelectionChanged += (_, _) => UpdateState();
        ProbeDriveBox.SelectionChanged += (_, _) => UpdateState();
        WindowTheme.ApplyDarkTitleBar(this);
        Closing += OnClosing;
        Closed += async (_, _) => { _closed = true; _serialPortAutoRefresh.Dispose(); await DisposeTransportAsync(); };
        Loaded += async (_, _) => { await RefreshPortsAsync(); await RefreshProbeDrivesAsync(); };
        UpdateState();
    }

    private async void SelectFirmware_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is not null) return;
        var dialog = new OpenFileDialog { Title = "选择 STM32 固件", Filter = "固件|*.bin;*.hex;*.ihx;*.s19;*.s28;*.s37;*.srec;*.mot|所有文件|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        _firmwarePath = null;
        await OperateAsync(async token =>
        {
            var summary = await FirmwareImageAnalyzer.AnalyzeAsync(dialog.FileName, token);
            AnalysisBox.Text = $"文件：{summary.FilePath}\n格式：{summary.Format}\n文件大小：{summary.Size:N0} 字节\n有效数据：{summary.DataBytes:N0} 字节\n地址范围：{summary.AddressRange}\n记录数：{summary.RecordCount:N0}\nSHA-256：{summary.Sha256}\n" +
                (summary.Warnings.Count == 0 ? "校验：通过" : "警告：\n" + string.Join("\n", summary.Warnings));
            _firmwarePath = dialog.FileName;
            Log($"固件：{summary.FilePath}\n{summary.Format} · {summary.DataBytes:N0} 字节 · SHA256 {summary.Sha256}");
            FlashStatus.Text = "固件已选择。";
        }, false);
    }

    private async void RefreshProbe_Click(object sender, RoutedEventArgs e) => await RefreshProbeDrivesAsync();

    private async Task RefreshProbeDrivesAsync()
    {
        if (_operation is not null) return;
        try
        {
            var selected = (ProbeDriveBox.SelectedItem as Stm32ProbeDrive)?.Root;
            var drives = await Stm32ProbeFlashService.ScanAsync();
            if (_closed) return;
            ProbeDriveBox.ItemsSource = drives;
            ProbeDriveBox.DisplayMemberPath = nameof(Stm32ProbeDrive.DisplayName);
            ProbeDriveBox.SelectedItem = drives.FirstOrDefault(drive => drive.Root.Equals(selected, StringComparison.OrdinalIgnoreCase)) ?? drives.FirstOrDefault();
            ProbeInfoText.Text = drives.Count == 0 ? "未发现探针盘" : $"发现 {drives.Count} 个目标盘";
            ProbeStatus.Text = drives.Count == 0 ? "请连接带 USB 大容量存储接口的 ST-LINK / DAPLink。" : "请选择目标盘并确认板卡型号。";
            UpdateState();
        }
        catch (Exception ex) { ProbeStatus.Text = "扫描探针失败：" + ex.Message; }
    }
    private async Task RefreshPortsAsync()
    {
        if (_operation is not null || _stm32 is not null) return;
        try
        {
            var selected = (PortBox.SelectedItem as SerialPortDescriptor)?.PortName;
            var ports = await _serial.ScanAsync();
            if (_closed) return;
            PortBox.ItemsSource = ports;
            PortBox.DisplayMemberPath = nameof(SerialPortDescriptor.DisplayName);
            PortBox.SelectedItem = ports.FirstOrDefault(p => p.PortName == selected) ?? ports.FirstOrDefault();
        }
        catch (Exception ex) { FlashStatus.Text = "扫描串口失败：" + ex.Message; }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is not null || PortBox.SelectedItem is not SerialPortDescriptor descriptor) return;
        if (!int.TryParse(BaudBox.Text, out var baud) || baud is < 1200 or > 3000000)
        { FlashStatus.Text = "波特率无效。"; return; }
        if (!await SerialPortUsePrompt.EnsureExclusiveAsync(this, _workspace, descriptor.PortName))
        { FlashStatus.Text = "未释放调试串口，烧录连接已取消。"; return; }
        await OperateAsync(async token =>
        {
            _channel = _serial.CreateChannel(new SerialPortSettings { PortName = descriptor.PortName, BaudRate = baud, DataBits = 8, Parity = Parity.Even, StopBits = StopBits.One, Handshake = Handshake.None });
            _stm32 = new Stm32BootloaderClient(_channel);
            await _channel.OpenAsync(token);
            await EnterBootloaderAsync(token);
            await _stm32.ConnectAsync(token);
            var id = await _stm32.ReadDeviceIdAsync(token);
            Log($"{descriptor.PortName} · STM32 ID 0x{id:X4} · Bootloader {_stm32.Version >> 4}.{_stm32.Version & 15}");
            FlashStatus.Text = "已连接。请核对芯片实际 Flash 容量后操作。";
        });
    }

    private async Task EnterBootloaderAsync(CancellationToken token)
    {
        if (BootEntryBox.SelectedIndex == 0)
        {
            Log("请确认 BOOT0 已拉高并完成硬件复位，然后等待 Bootloader。 ");
            return;
        }
        if (_channel is not ISerialPortControl control)
            throw new NotSupportedException("当前串口通道不支持 DTR / RTS 控制，请改为手动进入 Bootloader。");
        var sequence = ControlSequenceBox.SelectedIndex switch
        {
            1 => (true, false, false, true),
            2 => (false, false, true, true),
            _ => (false, true, true, false)
        };
        await control.SetControlLinesAsync(sequence.Item1, sequence.Item2, token);
        await Task.Delay(80, token);
        await control.SetControlLinesAsync(sequence.Item3, sequence.Item4, token);
        await Task.Delay(120, token);
        Log($"已发送 DTR/RTS 进入序列：{ControlSequenceBox.Text}。 ");
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    { if (_operation is null) { await DisposeTransportAsync(); FlashStatus.Text = "已断开。"; UpdateState(); } }

    private async void Erase_Click(object sender, RoutedEventArgs e)
    {
        if (_stm32 is null || _operation is not null) return;
        if (new ConfirmationWindow("全片擦除 STM32", $"将擦除 {_channel?.Settings.PortName} 上目标芯片的全部主 Flash，现有固件将丢失。确定继续？", "确认全片擦除") { Owner = this }.ShowDialog() != true) return;
        await OperateAsync(async token => { await _stm32!.MassEraseAsync(token); Log("全片擦除完成。"); FlashStatus.Text = "擦除完成。"; });
    }

    private async void Write_Click(object sender, RoutedEventArgs e) => await ProcessFirmwareAsync(true);
    private async void Verify_Click(object sender, RoutedEventArgs e) => await ProcessFirmwareAsync(false);
    private async Task ProcessFirmwareAsync(bool write)
    {
        if (_stm32 is null || _operation is not null || _firmwarePath is null) return;
        await OperateAsync(async token =>
        {
            var address = ParseAddress(); var capacity = ParseCapacity();
            // Validate the complete immutable image before sending any erase command.
            var segments = await FirmwareImageLoader.LoadAsync(_firmwarePath, address, token);
            Stm32BootloaderClient.ValidateFlashSegments(segments);
            if (segments.Any(s => (ulong)s.Address + (ulong)((s.Data.Length + 3) & ~3) > 0x08000000UL + (uint)capacity))
                throw new ArgumentException("固件超过所填 Flash 容量；未执行擦写。");
            var total = segments.Sum(s => (long)s.Data.Length);
            if (write)
            {
                if (new ConfirmationWindow("擦除并烧录 STM32", $"串口：{_channel?.Settings.PortName}\n固件：{Path.GetFileName(_firmwarePath)}\n数据：{total:N0} 字节 / {segments.Count} 个地址段\n将先擦除全部主 Flash，再写入并回读校验。确定继续？", "确认擦除并写入") { Owner = this }.ShowDialog() != true)
                { FlashStatus.Text = "已取消，未执行擦写。"; return; }
                FlashStatus.Text = "正在全片擦除…";
                await _stm32!.MassEraseAsync(token);
                Log("全片擦除完成，开始写入并回读校验。");
            }
            long done = 0;
            foreach (var segment in segments)
            {
                var baseDone = done;
                var progress = new Progress<long>(v => { if (!_closed) FlashProgress.Value = (baseDone + v) * 100d / total; });
                if (write) await _stm32!.WriteMemoryAsync(segment.Address, segment.Data, progress, token);
                else await _stm32!.VerifyMemoryAsync(segment.Address, segment.Data, progress, token);
                done += segment.Data.Length;
            }
            FlashProgress.Value = 100;
            FlashStatus.Text = write ? "烧录完成，全部数据已回读校验。" : "回读校验通过，Flash 内容与固件相同。";
            Log($"{FlashStatus.Text} {done:N0} 字节。");
        });
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (_stm32 is null || _operation is not null) return;
        var dialog = new SaveFileDialog { Title = "备份 STM32 Flash", FileName = "stm32-backup.bin", Filter = "原始固件|*.bin" };
        if (dialog.ShowDialog(this) != true) return;
        await OperateAsync(async token =>
        {
            var address = ParseAddress(); var capacity = ParseCapacity();
            var count = checked((int)(0x08000000L + capacity - address));
            Stm32BootloaderClient.ValidateFlashRange(address, count);
            var temporary = dialog.FileName + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                    await _stm32!.ReadFlashAsync(address, count, output, new Progress<long>(v => FlashProgress.Value = v * 100d / count), token);
                File.Move(temporary, dialog.FileName, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            FlashStatus.Text = "Flash 备份完成。"; Log($"备份：{dialog.FileName} · 0x{address:X8} · {count:N0} 字节。");
        });
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_stm32 is null || _operation is not null) return;
        await OperateAsync(async token => { var address = ParseAddress(); await _stm32!.GoAsync(address, token); await DisposeTransportAsync(); FlashStatus.Text = $"已执行 GO 0x{address:X8} 并释放串口。"; });
    }

    private async Task OperateAsync(Func<CancellationToken, Task> action, bool disconnectOnFailure = true)
    {
        if (_operation is not null) return;
        using var operation = new CancellationTokenSource(); _operation = operation;
        FlashProgress.Value = 0; FlashStatus.Text = "处理中…"; UpdateState();
        try { await action(operation.Token); }
        catch (OperationCanceledException) { FlashStatus.Text = "已取消。重新进入 Bootloader 后连接；已擦写的数据不会恢复。"; if (disconnectOnFailure) await DisposeTransportAsync(); }
        catch (Exception ex) { FlashStatus.Text = "操作失败：" + ex.Message; Log(FlashStatus.Text); if (disconnectOnFailure) await DisposeTransportAsync(); }
        finally { _operation = null; UpdateState(); if (_closeRequested) Close(); }
    }

    private uint ParseAddress()
    {
        var text = AddressBox.Text.Trim(); var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (!uint.TryParse(hex ? text[2..] : text, hex ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out var address))
            throw new ArgumentException("地址无效，可填写十进制或 0x 开头的十六进制。");
        return address;
    }
    private int ParseCapacity()
    {
        if (!int.TryParse(CapacityBox.Text, out var kib) || kib is < 1 or > 2048) throw new ArgumentException("Flash 容量必须为 1–2048 KiB，并与实际芯片一致。");
        return kib * 1024;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private void ClearFile_Click(object sender, RoutedEventArgs e) { if (_operation is not null) return; _firmwarePath = null; AnalysisBox.Clear(); UpdateState(); }

    private async void FlashProbe_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is not null || ProbeDriveBox.SelectedItem is not Stm32ProbeDrive drive || _firmwarePath is null) return;
        var info = new FileInfo(_firmwarePath);
        if (new ConfirmationWindow("确认探针烧录",
            $"目标：{drive.DisplayName}（{drive.Root}）\n固件：{Path.GetFileName(_firmwarePath)} · {info.Length:N0} 字节\n探针将接管 SWD 擦除、写入和校验。确定复制并开始？", "复制并开始") { Owner = this }.ShowDialog() != true) return;
        await OperateProbeAsync(async token =>
        {
            ProbeStatus.Text = "正在复制到探针…";
            await Stm32ProbeFlashService.CopyFirmwareAsync(drive.Root, _firmwarePath, new Progress<long>(value => ProbeProgress.Value = value * 100d / Math.Max(1, info.Length)), token);
            ProbeProgress.Value = 100;
            ProbeStatus.Text = "文件已交给探针，等待板卡复位；请查看探针指示灯或 FAIL.TXT。";
            ProbeLogBox.AppendText($"{DateTime.Now:T} 已复制 {Path.GetFileName(_firmwarePath)} 到 {drive.Root}{Environment.NewLine}");
            ProbeLogBox.ScrollToEnd();
        });
    }

    private async Task OperateProbeAsync(Func<CancellationToken, Task> action)
    {
        if (_operation is not null) return;
        using var operation = new CancellationTokenSource(); _operation = operation;
        ProbeProgress.Value = 0; ProbeStatus.Text = "处理中…"; UpdateState();
        try { await action(operation.Token); }
        catch (OperationCanceledException) { ProbeStatus.Text = "已取消；探针可能已接收部分文件，请检查目标盘。"; }
        catch (Exception ex) { ProbeStatus.Text = "探针烧录失败：" + ex.Message; ProbeLogBox.AppendText(ProbeStatus.Text + Environment.NewLine); }
        finally { _operation = null; UpdateState(); if (_closeRequested) Close(); }
    }
    private void Log(string text) { FlashLogBox.AppendText(text + Environment.NewLine); FlashLogBox.ScrollToEnd(); }
    private void UpdateState()
    {
        if (_closed) return;
        var idle = _operation is null;
        AnalyzeFileButton.IsEnabled = SelectFirmwareButton.IsEnabled = ClearFileButton.IsEnabled = AddressBox.IsEnabled = CapacityBox.IsEnabled = idle;
        BootEntryBox.IsEnabled = idle;
        ControlSequenceBox.IsEnabled = idle && BootEntryBox.SelectedIndex == 1;
        PortBox.IsEnabled = BaudBox.IsEnabled = ConnectButton.IsEnabled = idle && _stm32 is null;
        DisconnectButton.IsEnabled = idle && _stm32 is not null;
        EraseButton.IsEnabled = idle && _stm32?.SupportsErase == true;
        WriteButton.IsEnabled = idle && _stm32?.SupportsErase == true && _stm32.SupportsWrite && _firmwarePath is not null;
        VerifyButton.IsEnabled = idle && _stm32?.SupportsRead == true && _firmwarePath is not null;
        BackupButton.IsEnabled = idle && _stm32?.SupportsRead == true;
        RunButton.IsEnabled = idle && _stm32?.SupportsGo == true;
        CancelButton.IsEnabled = !idle;
        RefreshProbeButton.IsEnabled = ProbeDriveBox.IsEnabled = idle;
        FlashProbeButton.IsEnabled = idle && ProbeDriveBox.SelectedItem is Stm32ProbeDrive && _firmwarePath is not null;
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_operation is null) return;
        e.Cancel = true;
        if (new ConfirmationWindow("中止操作并关闭", "正在操作设备。中止后可能需要重新烧录，是否取消当前操作并关闭？", "中止并关闭") { Owner = this }.ShowDialog() != true) return;
        _closeRequested = true; _operation.Cancel();
    }
    private async Task DisposeTransportAsync()
    {
        var client = _stm32; var channel = _channel; _stm32 = null; _channel = null;
        if (client is not null) await client.DisposeAsync();
        else if (channel is not null) await channel.DisposeAsync();
    }
}
