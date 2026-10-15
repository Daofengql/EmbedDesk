using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbedDesk.Core.Abstractions;
using EmbedDesk.Core.Models;
using EmbedDesk.Infrastructure.Diagnostics;

namespace EmbedDesk.App.ViewModels;

/// <summary>A tab owns its connection, traffic and send jobs for its entire lifetime.</summary>
public sealed class SerialSessionViewModel : ObservableObject, IAsyncDisposable
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private ISerialPortChannel? _channel;
    private CancellationTokenSource? _connecting, _jobCancellation;
    private Task _jobTask = Task.CompletedTask;
    private Task? _closeTask;
    private SerialPortRowViewModel? _selectedSerialPort;
    private bool _busy, _disposed, _shellMode, _serialLoopEnabled, _serialSending;
    private bool _sendHex, _appendCrLf, _queueRepeat;
    private string _customTitle = "";
    private string _sendText = "", _interval = "1000", _jobStatus = "未运行", _status = "未连接";

    public SerialSessionViewModel(MainWindowViewModel owner, int number)
    {
        Owner = owner;
        Number = number;
        OpenSelectedSerialCommand = new AsyncRelayCommand(OpenAsync, () => CanConfigure && SelectedSerialPort is { IsAvailable: true });
        CloseSelectedSerialCommand = new AsyncRelayCommand(() => CloseAsync(), () => IsOpen && !IsBusy);
        SendSelectedSerialCommand = new AsyncRelayCommand(SendCurrentAsync, () => IsOpen && !IsBusy && !_serialSending && !string.IsNullOrWhiteSpace(SerialSendText));
        AddQueueItemCommand = new RelayCommand(() => SerialSendQueue.Add(new SerialSendQueueItem { Payload = SerialSendText }));
        RemoveQueueItemCommand = new RelayCommand(() => { if (SelectedQueueItem is not null) SerialSendQueue.Remove(SelectedQueueItem); });
        RunQueueCommand = new AsyncRelayCommand(RunQueueAsync, () => IsOpen && !IsBusy && !_serialSending);
        StopSerialJobCommand = new RelayCommand(StopJob);
    }

    public MainWindowViewModel Owner { get; }
    public int Number { get; }
    public bool IsDisposed => _disposed;
    public string CustomTitle
    {
        get => _customTitle;
        set
        {
            if (!SetProperty(ref _customTitle, value?.Trim() ?? "")) return;
            OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(Detail));
        }
    }
    public string Title => string.IsNullOrEmpty(CustomTitle) ? SelectedSerialPort?.PortName ?? $"串口 {Number}" : CustomTitle;
    public string Detail => $"{Title} · {Status}\n{SelectedSerialPort?.SelectionLabel ?? "未选择串口"}\n双击标签名称可重命名";
    public ObservableCollection<SerialPortRowViewModel> SerialPorts { get; } = [];
    public TerminalBufferViewModel Terminal { get; } = new();
    public ObservableCollection<SerialSendQueueItem> SerialSendQueue { get; } = [];
    public SerialSendQueueItem? SelectedQueueItem { get; set; }
    public bool IsOpen => SelectedSerialPort?.IsOpen == true;
    public bool IsBusy { get => _busy; private set { if (SetProperty(ref _busy, value)) NotifyState(); } }
    public bool CanConfigure => !_disposed && !IsOpen && !IsBusy;
    public string Status { get => _status; private set { if (SetProperty(ref _status, value)) { OnPropertyChanged(nameof(Detail)); OnPropertyChanged(nameof(StatusLabel)); OnPropertyChanged(nameof(ConnectionError)); } } }
    public string StatusLabel => Status.StartsWith("连接失败", StringComparison.Ordinal) ? "连接失败" : Status.StartsWith("连接中断", StringComparison.Ordinal) ? "连接中断" : Status;
    public string ConnectionError => StatusLabel is "连接失败" or "连接中断" ? Status : "";
    public string SerialJobStatus { get => _jobStatus; private set => SetProperty(ref _jobStatus, value); }
    public string SerialSendText { get => _sendText; set { if (SetProperty(ref _sendText, value)) SendSelectedSerialCommand.NotifyCanExecuteChanged(); } }
    public bool SerialSendAsHex { get => _sendHex; set => SetProperty(ref _sendHex, value); }
    public bool SerialAppendCrLf { get => _appendCrLf; set => SetProperty(ref _appendCrLf, value); }
    public string SerialIntervalMs { get => _interval; set => SetProperty(ref _interval, value); }
    public bool QueueRepeat { get => _queueRepeat; set => SetProperty(ref _queueRepeat, value); }
    public bool ShellMode { get => _shellMode; set { if (SetProperty(ref _shellMode, value)) { OnPropertyChanged(nameof(NormalMode)); StopJob(); } } }
    public bool NormalMode { get => !ShellMode; set { if (value) ShellMode = false; } }
    public bool SerialLoopEnabled
    {
        get => _serialLoopEnabled;
        set
        {
            if (!SetProperty(ref _serialLoopEnabled, value)) return;
            if (!value) { _jobCancellation?.Cancel(); return; }
            if (!IsOpen || IsBusy || _serialSending || string.IsNullOrWhiteSpace(SerialSendText))
            {
                SetProperty(ref _serialLoopEnabled, false);
                SerialJobStatus = "请先连接串口、填写内容并停止其他发送任务";
                return;
            }
            _jobTask = RunJobAsync(loop: true, CancellationToken.None);
        }
    }

    public SerialPortRowViewModel? SelectedSerialPort
    {
        get => _selectedSerialPort;
        set
        {
            if (!CanConfigure || ReferenceEquals(value, _selectedSerialPort)) return;
            if (_selectedSerialPort is not null) _selectedSerialPort.PropertyChanged -= PortChanged;
            SetProperty(ref _selectedSerialPort, value);
            if (value is not null) value.PropertyChanged += PortChanged;
            Status = "未连接";
            OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(Detail)); NotifyState();
        }
    }

    public IAsyncRelayCommand OpenSelectedSerialCommand { get; }
    public IAsyncRelayCommand CloseSelectedSerialCommand { get; }
    public IAsyncRelayCommand SendSelectedSerialCommand { get; }
    public IRelayCommand AddQueueItemCommand { get; }
    public IRelayCommand RemoveQueueItemCommand { get; }
    public IAsyncRelayCommand RunQueueCommand { get; }
    public IRelayCommand StopSerialJobCommand { get; }

    internal void RefreshPorts(IReadOnlyCollection<SerialPortRowViewModel> available)
    {
        foreach (var row in SerialPorts.ToArray())
        {
            row.IsAvailable = available.Any(p => string.Equals(p.PortName, row.PortName, StringComparison.OrdinalIgnoreCase));
            row.IsShared = available.FirstOrDefault(p => p.PortName == row.PortName)?.IsShared == true;
            if (!row.IsAvailable && row != SelectedSerialPort) SerialPorts.Remove(row);
        }
        foreach (var port in available)
            if (!SerialPorts.Any(p => string.Equals(p.PortName, port.PortName, StringComparison.OrdinalIgnoreCase)))
                SerialPorts.Add(Owner.CreateSerialPortRow(new SerialPortDescriptor(port.PortName, port.DisplayName, port.Description, port.Manufacturer), port.IsRemote));
        if (SelectedSerialPort is null) SelectedSerialPort = SerialPorts.FirstOrDefault();
        if (SelectedSerialPort is { IsAvailable: false } && (IsOpen || IsBusy)) _ = CloseAsync("设备已离线");
        NotifyState();
    }

    private async Task OpenAsync(CancellationToken token)
    {
        if (!CanConfigure || SelectedSerialPort is not { IsAvailable: true } port) return;
        if (!Owner.TryAcquireSerialPort(this, port, out var error)) { Status = error; return; }
        IsBusy = true; Status = "连接中";
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _connecting = cancellation;
        try
        {
            var channel = Owner.CreateSerialChannel(port);
            _channel = channel;
            channel.DataReceived += OnDataReceived;
            if (channel is ISerialPortFaultNotifier notifier) notifier.Faulted += OnChannelFaulted;
            await Task.Run(() => channel.OpenAsync(cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            port.IsOpen = true; Status = "已连接";
            Owner.AddSerialLog($"已打开 {port.PortName} @ {port.EffectiveBaudRate}", TransportEventKind.Connected);
        }
        catch (Exception ex)
        {
            await ReleaseChannelAsync();
            Status = ex is OperationCanceledException ? "已断开" : "连接失败：" + ex.Message;
            if (ex is not OperationCanceledException) Owner.AddSerialLog($"{port.PortName} 打开失败: {ex.Message}", TransportEventKind.Error);
        }
        finally { _connecting = null; IsBusy = false; }
    }

    public Task CloseAsync(string status = "已断开")
    {
        if (_closeTask is { IsCompleted: false }) return _closeTask;
        return _closeTask = CloseCoreAsync(status);
    }

    private async Task CloseCoreAsync(string status)
    {
        _connecting?.Cancel();
        StopJob();
        IsBusy = true;
        if (OpenSelectedSerialCommand.ExecutionTask is { IsCompleted: false } opening) await opening;
        IsBusy = true;
        try { await _jobTask; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Owner.AddSerialLog($"{Title} 发送任务已结束: {ex.Message}", TransportEventKind.Error); }
        if (RunQueueCommand.ExecutionTask is { IsCompleted: false } queue) await queue;
        await _sendGate.WaitAsync();
        try { await ReleaseChannelAsync(); Status = status; }
        finally { _sendGate.Release(); IsBusy = false; }
    }

    private async Task ReleaseChannelAsync()
    {
        var channel = _channel; _channel = null;
        try
        {
            if (channel is not null)
            {
                channel.DataReceived -= OnDataReceived;
                if (channel is ISerialPortFaultNotifier notifier) notifier.Faulted -= OnChannelFaulted;
                await Task.Run(async () => await channel.DisposeAsync());
            }
        }
        catch (Exception ex) { Owner.AddSerialLog($"{Title} 关闭失败: {ex.Message}", TransportEventKind.Error); }
        finally
        {
            if (SelectedSerialPort is not null) SelectedSerialPort.IsOpen = false;
            Owner.ReleaseSerialPort(this);
            NotifyState();
        }
    }

    private void OnDataReceived(object? sender, ReadOnlyMemory<byte> data)
    {
        var bytes = data.ToArray();
        var timestamp = DateTimeOffset.Now;
        void Append()
        {
            if (_disposed || !ReferenceEquals(sender, _channel)) return;
            Terminal.Add(timestamp, true, bytes);
        }
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Append(); else dispatcher.BeginInvoke(Append);
    }
    private void OnChannelFaulted(object? sender, string error)
    {
        void Handle() { if (!_disposed && ReferenceEquals(sender, _channel)) { Owner.AddSerialLog(error, TransportEventKind.Error); _ = CloseAsync("连接中断：" + error); } }
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Handle(); else dispatcher.BeginInvoke(new Action(Handle));
    }

    private async Task SendBytesAsync(byte[] bytes, CancellationToken token)
    {
        var channel = _channel ?? throw new InvalidOperationException("串口未连接");
        await _sendGate.WaitAsync(token);
        try
        {
            if (_disposed || IsBusy || !ReferenceEquals(channel, _channel)) throw new OperationCanceledException();
            await Task.Run(() => channel.SendAsync(bytes, token), token);
            Terminal.Add(DateTimeOffset.Now, false, bytes);
        }
        finally { _sendGate.Release(); }
    }

    private async Task SendCurrentAsync(CancellationToken token)
    {
        if (!IsOpen || _serialSending || string.IsNullOrEmpty(SerialSendText)) return;
        try { await SendBytesAsync(SendPayload.Encode(SerialSendText, SerialSendAsHex, SerialAppendCrLf), token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportSendFailure(ex); }
    }

    public async Task SendShellInputAsync(string text)
    {
        if (!IsOpen || _serialSending) return;
        try { await SendBytesAsync(Encoding.UTF8.GetBytes(text), CancellationToken.None); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportSendFailure(ex); }
    }

    private Task RunQueueAsync(CancellationToken token)
    {
        if (_serialSending || !IsOpen || IsBusy) return Task.CompletedTask;
        return _jobTask = RunJobAsync(loop: false, token);
    }

    public Task ReplayRecordingAsync(IReadOnlyList<TrafficRecord> records, double speed, CancellationToken token)
    {
        if (!IsOpen || IsBusy || _serialSending || SendSelectedSerialCommand.IsRunning)
            throw new InvalidOperationException("请先连接串口，并停止循环、队列及其他发送任务。");
        return _jobTask = ReplayCoreAsync(records, speed, token);
    }

    private async Task ReplayCoreAsync(IReadOnlyList<TrafficRecord> records, double speed, CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _jobCancellation = cancellation; _serialSending = true; NotifyState();
        SerialJobStatus = "录制 TX 回放中";
        try
        {
            await TrafficRecordingFile.ReplayAsync(records, speed, async (record, ct) =>
            {
                if (record.IsReceived) return;
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is not null && !dispatcher.CheckAccess())
                    await dispatcher.InvokeAsync(() => SendBytesAsync(record.Data, ct)).Task.Unwrap();
                else await SendBytesAsync(record.Data, ct);
            }, cancellation.Token);
            SerialJobStatus = "录制 TX 回放完成";
        }
        catch (OperationCanceledException) { SerialJobStatus = "回放已停止"; throw; }
        catch (Exception ex) { ReportSendFailure(ex); throw; }
        finally { _jobCancellation = null; _serialSending = false; NotifyState(); }
    }

    private async Task RunJobAsync(bool loop, CancellationToken token)
    {
        var items = SerialSendQueue.Where(i => i.Enabled && !string.IsNullOrWhiteSpace(i.Payload)).Select(i => i.Payload).ToArray();
        if (!loop && items.Length == 0) { SerialJobStatus = "队列为空"; return; }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _jobCancellation = cancellation;
        _serialSending = true; NotifyState();
        try
        {
            do
            {
                var payloads = loop ? new[] { SerialSendText } : items;
                for (var index = 0; index < payloads.Length; index++)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    SerialJobStatus = loop ? "循环发送中" : $"队列发送 {index + 1}/{items.Length}";
                    await SendBytesAsync(SendPayload.Encode(payloads[index], SerialSendAsHex, SerialAppendCrLf), cancellation.Token);
                    await Task.Delay(Math.Max(50, int.TryParse(SerialIntervalMs, out var ms) ? ms : 1000), cancellation.Token);
                }
            } while (loop || QueueRepeat);
            SerialJobStatus = "队列已完成";
        }
        catch (OperationCanceledException) { SerialJobStatus = "已停止"; }
        catch (Exception ex) { ReportSendFailure(ex); }
        finally
        {
            _jobCancellation = null; _serialSending = false;
            SetProperty(ref _serialLoopEnabled, false, nameof(SerialLoopEnabled)); NotifyState();
        }
    }

    private void StopJob()
    {
        SetProperty(ref _serialLoopEnabled, false, nameof(SerialLoopEnabled));
        _jobCancellation?.Cancel(); RunQueueCommand.Cancel();
    }
    private void ReportSendFailure(Exception ex)
    {
        SerialJobStatus = $"发送失败: {ex.Message}";
        Owner.AddSerialLog($"{Title} {SerialJobStatus}", TransportEventKind.Error);
    }
    private void PortChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SerialPortRowViewModel.IsOpen) or nameof(SerialPortRowViewModel.IsAvailable)) NotifyState();
    }
    private void NotifyState()
    {
        OnPropertyChanged(nameof(IsOpen)); OnPropertyChanged(nameof(CanConfigure));
        OpenSelectedSerialCommand.NotifyCanExecuteChanged(); CloseSelectedSerialCommand.NotifyCanExecuteChanged();
        SendSelectedSerialCommand.NotifyCanExecuteChanged(); RunQueueCommand.NotifyCanExecuteChanged();
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await CloseAsync();
        if (_selectedSerialPort is not null) _selectedSerialPort.PropertyChanged -= PortChanged;
    }
}
