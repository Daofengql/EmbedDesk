using System.ComponentModel;
using System.IO;
using System.IO.Ports;
using System.Windows;
using EmbeddedDebugger.Core.Abstractions;
using EmbeddedDebugger.Core.Models;
using EmbeddedDebugger.Infrastructure.Firmware;
using Microsoft.Win32;

namespace EmbeddedDebugger.App;

public partial class StcIspToolWindow : Window
{
    private readonly ISerialPortService _serial;
    private ISerialPortChannel? _channel;
    private Stc89BootloaderClient? _client;
    private string? _path;
    private CancellationTokenSource? _operation;
    private bool _closed;
    private bool _closeRequested;

    public StcIspToolWindow(ISerialPortService serial)
    {
        InitializeComponent(); _serial = serial;
        HandshakeBaudBox.ItemsSource = new[] { 1200, 2400, 4800, 9600 }; HandshakeBaudBox.SelectedItem = 2400;
        TransferBaudBox.ItemsSource = new[] { 2400, 4800, 9600, 19200, 38400, 57600, 115200 }; TransferBaudBox.SelectedItem = 19200;
        WindowTheme.ApplyDarkTitleBar(this);
        Loaded += async (_, _) => await RefreshAsync();
        Closing += OnClosing;
        Closed += async (_, _) => { _closed = true; await DisconnectAsync(); };
        UpdateState();
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async Task RefreshAsync()
    {
        if (_operation is not null || _client is not null) return;
        try
        {
            var selected = (PortBox.SelectedItem as SerialPortDescriptor)?.PortName;
            var ports = await _serial.ScanAsync();
            if (_closed) return;
            PortBox.ItemsSource = ports; PortBox.DisplayMemberPath = nameof(SerialPortDescriptor.DisplayName);
            PortBox.SelectedItem = ports.FirstOrDefault(p => p.PortName == selected) ?? ports.FirstOrDefault();
        }
        catch (Exception ex) { Status.Text = "扫描失败：" + ex.Message; }
    }
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is not null || _client is not null || PortBox.SelectedItem is not SerialPortDescriptor port) return;
        var handshakeBaud = (int)HandshakeBaudBox.SelectedItem; var transferBaud = (int)TransferBaudBox.SelectedItem;
        await OperateAsync(async token =>
        {
            _channel = _serial.CreateChannel(new SerialPortSettings { PortName = port.PortName, BaudRate = handshakeBaud, DataBits = 8, Parity = Parity.None, StopBits = StopBits.One, Handshake = Handshake.None });
            _client = new Stc89BootloaderClient(_channel);
            await _channel.OpenAsync(token);
            Status.Text = "等待 STC89 上电握手（30 秒）…现在请给目标芯片断电再上电。";
            Log($"{port.PortName} · 握手 {handshakeBaud} / 传输 {transferBaud} baud · 等待上电");
            var info = await _client.ConnectAsync(transferBaud, token);
            DeviceText.Text = $"{info.Device.Name} · ID 0x{info.Device.Id:X4} · 代码区 {info.Device.CodeBytes / 1024} KiB\nBootloader {info.Bootloader} · {info.ClockHz / 1000000:F3} MHz · {(info.SixClockMode ? "6T" : "12T")} · 选项 0x{info.Options:X2}";
            Log(DeviceText.Text); Status.Text = "握手及传输波特率测试通过。";
        });
    }
    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    { if (_operation is null) { await DisconnectAsync(); Status.Text = "已断开；下次连接请重新上电。"; UpdateState(); } }
    private async void Choose_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is not null) return;
        var dialog = new OpenFileDialog { Title = "选择 STC89 固件", Filter = "固件|*.bin;*.hex;*.ihx;*.s19;*.s28;*.s37;*.srec;*.mot|所有文件|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        _path = null;
        await OperateAsync(async token =>
        {
            var segments = await FirmwareImageLoader.LoadAsync(dialog.FileName, 0, token);
            var info = _client?.Info;
            if (info is not null) Stc89BootloaderClient.PrepareFirmware(segments, info.Device);
            var summary = await FirmwareImageAnalyzer.AnalyzeAsync(dialog.FileName, token);
            _path = dialog.FileName;
            FileText.Text = $"固件：{Path.GetFileName(_path)} · {summary.DataBytes:N0} 字节 · {summary.Format}";
            Log($"{_path}\nSHA-256：{summary.Sha256}");
            Status.Text = info is null ? "固件已选择；连接后会按芯片实际容量校验。" : "固件地址及容量校验通过。";
        }, false);
    }
    private async void Erase_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is not null || _client?.Info is not { } info) return;
        if (new ConfirmationWindow("擦除 STC89 代码区", $"将擦除 {info.Device.Name} 的 {info.Device.CodeBytes / 1024} KiB 代码区。原程序将丢失，选项字节保持原值。确定继续？", "确认擦除") { Owner = this }.ShowDialog() != true) return;
        await OperateAsync(async token => { await _client!.EraseCodeAsync(token); Status.Text = "代码区擦除完成，原选项已恢复。"; Log(Status.Text); });
    }
    private async void Write_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is not null || _client?.Info is not { } info || _path is null) return;
        await OperateAsync(async token =>
        {
            var segments = await FirmwareImageLoader.LoadAsync(_path, 0, token);
            var prepared = Stc89BootloaderClient.PrepareFirmware(segments, info.Device);
            if (new ConfirmationWindow("擦除并烧录 STC89", $"目标：{info.Device.Name}（{_channel?.Settings.PortName}）\n固件：{Path.GetFileName(_path)}\n写入：{prepared.Length:N0} 字节（含 128 字节对齐）\n将擦除整个代码区，逐块写入并检查设备校验和。确定继续？", "确认擦除并写入") { Owner = this }.ShowDialog() != true)
            { Status.Text = "已取消，未执行擦写。"; return; }
            Status.Text = "正在擦除、写入并检查设备分块校验和…";
            await _client!.ProgramAsync(segments, new Progress<long>(v => { if (!_closed) Progress.Value = v * 100d / prepared.Length; }), token);
            Progress.Value = 100; Status.Text = "烧录完成，设备分块校验和全部通过，原选项已恢复。"; Log(Status.Text);
        });
    }
    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is not null || _client?.Info is null) return;
        await OperateAsync(async token => { await _client!.RunAsync(token); await DisconnectAsync(); Status.Text = "运行命令已发送，串口已释放。"; });
    }
    private async Task OperateAsync(Func<CancellationToken, Task> action, bool disconnectOnFailure = true)
    {
        if (_operation is not null) return;
        using var operation = new CancellationTokenSource(); _operation = operation; Progress.Value = 0; UpdateState();
        try { await action(operation.Token); }
        catch (OperationCanceledException) { Status.Text = "已取消。已擦写数据不会恢复；请重新上电后连接。"; if (disconnectOnFailure) await DisconnectAsync(); }
        catch (Exception ex) { Status.Text = "操作失败：" + ex.Message; Log(Status.Text); if (disconnectOnFailure) await DisconnectAsync(); }
        finally { _operation = null; UpdateState(); if (_closeRequested) Close(); }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private void Log(string text) { LogBox.AppendText(text + Environment.NewLine); LogBox.ScrollToEnd(); }
    private void UpdateState()
    {
        if (_closed) return;
        var idle = _operation is null; var connected = _client?.Info is not null;
        PortBox.IsEnabled = HandshakeBaudBox.IsEnabled = TransferBaudBox.IsEnabled = RefreshButton.IsEnabled = ConnectButton.IsEnabled = idle && !connected;
        ChooseButton.IsEnabled = idle; DisconnectButton.IsEnabled = EraseButton.IsEnabled = RunButton.IsEnabled = idle && connected;
        WriteButton.IsEnabled = idle && connected && _path is not null; CancelButton.IsEnabled = !idle;
    }
    private async Task DisconnectAsync()
    {
        var client = _client; var channel = _channel; _client = null; _channel = null;
        if (client is not null) await client.DisposeAsync(); else if (channel is not null) await channel.DisposeAsync();
        if (!_closed) DeviceText.Text = "芯片：未连接";
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_operation is null) return; e.Cancel = true;
        if (new ConfirmationWindow("中止操作并关闭", "中止烧录后可能需要重新上电并烧录。是否取消当前操作并关闭？", "中止并关闭") { Owner = this }.ShowDialog() != true) return;
        _closeRequested = true; _operation.Cancel();
    }
}
