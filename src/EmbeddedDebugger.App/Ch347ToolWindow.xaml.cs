using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using EmbeddedDebugger.Infrastructure.Firmware;
using Microsoft.Win32;

namespace EmbeddedDebugger.App;

public partial class Ch347ToolWindow : Window
{
    private SpiFlashClient? _client;
    private CancellationTokenSource? _cancellation;
    private bool _closeAfterCancel;
    private bool _closed;
    private string? _imagePath;
    private IProgress<SpiFlashProgress> _progress = null!;
    private DateTime _lastProgress;

    public Ch347ToolWindow()
    {
        InitializeComponent(); WindowTheme.ApplyDarkTitleBar(this);
        SpeedBox.ItemsSource = new[] { "60 MHz", "30 MHz", "15 MHz", "7.5 MHz", "3.75 MHz", "1.875 MHz", "937.5 kHz", "468.75 kHz" };
        SpeedBox.SelectedIndex = 5; CsBox.ItemsSource = new[] { "CS1", "CS2" }; CsBox.SelectedIndex = 0;
        Loaded += (_, _) => RefreshDevices();
        Closing += Window_Closing;
        Closed += async (_, _) => { _closed = true; if (_client is not null) { try { await _client.DisposeAsync(); } catch { } _client = null; } };
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshDevices();
    private void RefreshDevices()
    {
        if (_cancellation is not null || _client is not null) return;
        try
        {
            DeviceBox.ItemsSource = Ch347HidTransport.Enumerate(); DeviceBox.SelectedIndex = 0;
            StatusText.Text = DeviceBox.Items.Count == 0 ? "未发现 CH347T HID SPI。请将板卡设为标准 HID 模式后重新插入，并刷新。" : "请选择设备，连接后自动识别芯片。";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_client is not null)
        {
            await RunAsync("断开连接", async _ => { var client = _client; _client = null; await client.DisposeAsync(); ChipInfoBox.Text = "设备已断开。"; });
            return;
        }
        if (DeviceBox.SelectedItem is not Ch347Device device) { StatusText.Text = "请先选择 CH347T HID SPI 设备。"; return; }
        var divider = SpeedBox.SelectedIndex; var cs = CsBox.SelectedIndex;
        await RunAsync("连接并识别", async token =>
        {
            var transport = await Ch347HidTransport.OpenAsync(device, divider, cs, token);
            try { _client = await SpiFlashClient.IdentifyAsync(transport, token); }
            catch { await transport.DisposeAsync(); throw; }
            var info = _client.Info;
            ChipInfoBox.Text = $"{info.Description}\n页大小 {info.PageSize:N0} B · 擦除块 {info.EraseSize:N0} B" +
                (info.Kind == SpiFlashKind.Nand ? $" · OOB {info.SpareSize} B/页 · 片内 ECC 开启" : " · SPI 模式 0 / 3 字节地址");
            LengthBox.Text = $"0x{info.Capacity:X}"; OffsetBox.Text = "0x0";
            RawOobBox.IsChecked = false;
            Append(info.Description);
        });
    }

    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择不含 OOB 的 BIN 固件", Filter = "BIN 固件|*.bin|所有文件|*.*" };
        if (dialog.ShowDialog(this) == true)
        {
            if (Path.GetExtension(dialog.FileName).Equals(".nand", StringComparison.OrdinalIgnoreCase))
            { StatusText.Text = "含 OOB 的 .nand 原始备份不能作为普通 BIN 刷写。"; return; }
            _imagePath = dialog.FileName; ImagePathBox.Text = dialog.FileName; UpdateControls();
        }
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null || !TryRange(out var offset, out var length)) return;
        var raw = RawOobBox.IsChecked == true;
        var dialog = new SaveFileDialog { Title = "保存 Flash 备份", Filter = raw ? "原始 NAND（页 + OOB）|*.nand" : "BIN 备份|*.bin", FileName = _client.Info.Name.Split('/')[0] + (raw ? ".nand" : ".bin") };
        if (dialog.ShowDialog(this) != true) return;
        var target = dialog.FileName; var partial = target + ".partial-" + Guid.NewGuid().ToString("N")[..8];
        await RunAsync("读取备份", async token =>
        {
            try
            {
                await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                    await _client.BackupAsync(output, offset, length, raw, _progress, token);
                string hash;
                await using (var input = File.OpenRead(partial)) hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
                File.Move(partial, target, true);
                var metadata = new { Chip = _client.Info, Offset = offset, Length = length, IncludesOob = raw, EccEnabled = _client.Info.Kind == SpiFlashKind.Nand && !raw, _client.CorrectedPages, Sha256 = hash, CreatedAt = DateTimeOffset.Now };
                var sidecar = target + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + ".json";
                await File.WriteAllTextAsync(sidecar, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
                Append($"备份：{target}\nSHA-256：{hash}\n元数据：{sidecar}");
            }
            catch { if (File.Exists(partial)) Append($"未完成的备份保留在：{partial}"); throw; }
        });
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null) return;
        await RunAsync("扫描坏块", async token =>
        {
            var bad = await _client.ScanBadBlocksAsync(_progress, token);
            Append(bad.Count == 0 ? "未发现工厂 / 已标记坏块。" : $"发现 {bad.Count} 个坏块（物理块编号）：{string.Join(", ", bad)}");
        });
    }

    private async void Erase_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null || !TryRange(out var offset, out var length)) return;
        if (!ConfirmDestructive("擦除", offset, length)) return;
        await RunAsync("擦除并校验", token => _client.EraseAsync(offset, length, _progress, token));
    }

    private async void Write_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null || _imagePath is null) return;
        if (!TryNumber(OffsetBox.Text, out var offset)) { StatusText.Text = "起始地址无效。"; return; }
        FileStream input;
        try { input = new FileStream(_imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true); }
        catch (Exception ex) { StatusText.Text = ex.Message; return; }
        await using (input)
        {
            var erasedLength = (input.Length + _client.Info.EraseSize - 1) / _client.Info.EraseSize * _client.Info.EraseSize;
            if (!ConfirmDestructive("擦除后写入并校验", offset, erasedLength)) return;
            await RunAsync("写入并回读校验", token => _client.ProgramAndVerifyAsync(input, offset, _progress, token));
        }
    }

    private bool ConfirmDestructive(string operation, long offset, long length)
    {
        var info = _client!.Info;
        if (offset < 0 || length <= 0 || offset > info.Capacity || length > info.Capacity - offset || offset % info.EraseSize != 0 || length % info.EraseSize != 0)
        { StatusText.Text = $"操作范围必须在芯片容量内，并按 {info.EraseSize:N0} 字节擦除块对齐。"; return false; }
        return new ConfirmationWindow("确认 Flash 擦写",
            $"目标：{info.Description}\n操作：{operation}\n物理范围：0x{offset:X8} – 0x{offset + length:X8}（末地址不包含）\n覆盖 {length:N0} 字节。擦除块内原有数据会丢失，固件末尾不足一块的区域补 FF。\n\nNAND 目标含坏块时将在擦除前停止。请确认已备份，需要继续吗？",
            "确认擦写") { Owner = this }.ShowDialog() == true;
    }

    private bool TryRange(out long offset, out long length)
    {
        var validOffset = TryNumber(OffsetBox.Text, out offset); var validLength = TryNumber(LengthBox.Text, out length);
        if (validOffset && validLength && length > 0) return true;
        StatusText.Text = "请输入有效的起始地址与正数长度，十六进制使用 0x 前缀。"; return false;
    }
    private static bool TryNumber(string text, out long value)
    {
        text = text.Trim(); return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(text[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value) && value >= 0
            : long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;
    }

    private async Task RunAsync(string name, Func<CancellationToken, Task> action)
    {
        if (_cancellation is not null) return;
        using var operation = new CancellationTokenSource();
        _cancellation = operation;
        _progress = new Progress<SpiFlashProgress>(value =>
        {
            if (_closed || !ReferenceEquals(_cancellation, operation) || operation.IsCancellationRequested) return;
            if (value.Completed != value.Total && DateTime.UtcNow - _lastProgress < TimeSpan.FromMilliseconds(80)) return;
            _lastProgress = DateTime.UtcNow;
            Progress.Value = value.Percent; StatusText.Text = $"{value.Stage} · {value.Percent:F1}% · {value.Detail}";
        });
        UpdateControls(); Progress.Value = 0; Append($"开始：{name}");
        try { await action(_cancellation.Token); Progress.Value = 100; StatusText.Text = name + "完成。"; Append(StatusText.Text); }
        catch (OperationCanceledException) { StatusText.Text = "已取消。已写入 / 擦除的内容不会回滚。"; Append(StatusText.Text); }
        catch (Exception ex) { StatusText.Text = name + "失败：" + ex.Message; Append(StatusText.Text); }
        finally
        {
            _cancellation = null; UpdateControls();
            if (_closeAfterCancel) Close();
        }
    }

    private void UpdateControls()
    {
        var idle = _cancellation is null; var connected = _client is not null;
        DeviceBox.IsEnabled = SpeedBox.IsEnabled = CsBox.IsEnabled = RefreshButton.IsEnabled = idle && !connected;
        ConnectButton.IsEnabled = idle; ConnectButton.Content = connected ? "断开" : "连接并识别";
        ChooseButton.IsEnabled = RangePanel.IsEnabled = idle;
        BackupButton.IsEnabled = EraseButton.IsEnabled = idle && connected;
        WriteButton.IsEnabled = idle && connected && _imagePath is not null;
        ScanButton.IsEnabled = idle && _client?.Info.Kind == SpiFlashKind.Nand;
        RawOobBox.IsEnabled = idle && _client?.Info.Kind == SpiFlashKind.Nand;
        CancelButton.IsEnabled = !idle;
    }
    private void Append(string text)
    {
        if (_closed) return;
        if (LogBox.Text.Length > 120000) LogBox.Text = LogBox.Text[^60000..];
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\n"); LogBox.ScrollToEnd();
    }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    { _cancellation?.Cancel(); StatusText.Text = "正在取消，等待当前页 / 擦除块完成…"; }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_cancellation is null) return;
        e.Cancel = true;
        if (_closeAfterCancel) return;
        if (new ConfirmationWindow("取消当前操作", "操作正在进行。取消会保留已完成的擦写，是否取消并关闭？", "取消并关闭") { Owner = this }.ShowDialog() != true) return;
        _closeAfterCancel = true; _cancellation.Cancel(); StatusText.Text = "正在取消并释放设备…";
    }
}
