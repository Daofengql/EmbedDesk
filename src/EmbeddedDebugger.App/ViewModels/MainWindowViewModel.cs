using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbeddedDebugger.Core.Abstractions;
using EmbeddedDebugger.Core.Models;

namespace EmbeddedDebugger.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ITransportService _transportService;
    private readonly ISerialPortService _serialPortService;
    private readonly ISerialBridgeService _serialBridgeService;
    private readonly IRemoteSerialClient _remoteSerialClient;
    private readonly Dictionary<string, ISerialPortChannel> _openSerialChannels = new(StringComparer.OrdinalIgnoreCase);
    private NetworkSessionViewModel? _selectedSession;
    private SerialPortRowViewModel? _selectedSerialPort;
    private string _serialSendText = string.Empty;
    private string _networkSendText = string.Empty;
    private string _activityStatus = "就绪";
    private bool _serialSendAsHex;
    private bool _serialAppendCrLf;
    private bool _networkSendAsHex;
    private bool _networkAppendCrLf;
    private string? _bridgeId;
    private string _bridgeMode = "服务端";
    private bool _shellMode;
    private bool _serialLoopEnabled;
    private bool _queueRepeat;
    private bool _serialSending;
    private string _serialIntervalMs = "1000";
    private string _serialJobStatus = "未运行";

    public MainWindowViewModel(
        ITransportService transportService,
        ISerialPortService serialPortService,
        ISerialBridgeService serialBridgeService,
        IRemoteSerialClient remoteSerialClient,
        IRemoteDebugService? remoteDebugService = null,
        string? remoteSettingsDirectory = null)
    {
        _transportService = transportService;
        _serialPortService = serialPortService;
        _serialBridgeService = serialBridgeService;
        _remoteSerialClient = remoteSerialClient;
        RemoteDebug = remoteDebugService is null ? null : new RemoteDebugWorkspaceViewModel(remoteDebugService, remoteSettingsDirectory);
        _remoteSerialClient.PortsChanged += OnRemotePortsChanged;
        _remoteSerialClient.ConnectionChanged += (_, _) => RunOnUi(NotifyBridgeState);

        ProtocolOptions = new ObservableCollection<string> { "TCP", "UDP" };
        ModeOptions = new ObservableCollection<string> { "客户端", "服务端" };
        BaudRateOptions = new ObservableCollection<int> { 110, 300, 600, 1200, 2400, 4800, 7200, 9600, 14400, 19200, 28800, 38400, 56000, 57600, 115200, 128000, 230400, 460800, 500000, 921600, 1000000, 1500000, 2000000 };
        Sessions = new ObservableCollection<NetworkSessionViewModel>();
        SerialPorts = new ObservableCollection<SerialPortRowViewModel>();
        Logs = new ObservableCollection<LogEntryViewModel>();

        AddNetworkSessionCommand = new AsyncRelayCommand(AddNetworkSessionAsync);
        RemoveSelectedNetworkCommand = new AsyncRelayCommand(RemoveSelectedNetworkAsync, () => SelectedSession is not null);
        SendSelectedNetworkCommand = new AsyncRelayCommand(SendSelectedNetworkAsync, () => SelectedSession?.CanSend == true && !string.IsNullOrWhiteSpace(NetworkSendText));
        ScanSerialPortsCommand = new AsyncRelayCommand(ScanSerialPortsAsync);
        OpenSelectedSerialCommand = new AsyncRelayCommand(OpenSelectedSerialAsync, () => SelectedSerialPort is { IsOpen: false } && (SelectedSerialPort.IsRemote || _bridgeId is null));
        CloseSelectedSerialCommand = new AsyncRelayCommand(CloseSelectedSerialAsync, () => SelectedSerialPort?.IsOpen == true);
        SendSelectedSerialCommand = new AsyncRelayCommand(SendSelectedSerialAsync, () => SelectedSerialPort?.IsOpen == true && !string.IsNullOrWhiteSpace(SerialSendText));
        StartBridgeCommand = new AsyncRelayCommand(StartBridgeAsync, () => !IsBridgeRunning && (BridgeMode == "客户端" || SerialPorts.Any(port => port.IsSelected && !port.IsRemote)));
        StopBridgeCommand = new AsyncRelayCommand(StopBridgeAsync, () => IsBridgeRunning);
        ClearLogsCommand = new RelayCommand(() => Logs.Clear());
        AddQueueItemCommand = new RelayCommand(() => SerialSendQueue.Add(new SerialSendQueueItem { Payload = SerialSendText }));
        RemoveQueueItemCommand = new RelayCommand(() => { if (SelectedQueueItem is not null) SerialSendQueue.Remove(SelectedQueueItem); });
        RunQueueCommand = new AsyncRelayCommand(RunQueueAsync, () => SelectedSerialPort?.IsOpen == true && !_serialSending);
        StopSerialJobCommand = new RelayCommand(() => { SerialLoopEnabled = false; RunQueueCommand.Cancel(); });
        SerialPorts.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSerialPorts));
    }

    public ObservableCollection<string> ProtocolOptions { get; }
    public ObservableCollection<string> ModeOptions { get; }
    public ObservableCollection<int> BaudRateOptions { get; }
    public ObservableCollection<NetworkSessionViewModel> Sessions { get; }
    public ObservableCollection<SerialPortRowViewModel> SerialPorts { get; }
    public ObservableCollection<LogEntryViewModel> Logs { get; }
    public ObservableCollection<SerialSendQueueItem> SerialSendQueue { get; } = new();
    public RemoteDebugWorkspaceViewModel? RemoteDebug { get; }
    public SerialSendQueueItem? SelectedQueueItem { get; set; }
    public int[] DataBitsOptions { get; } = [5, 6, 7, 8];
    public System.IO.Ports.Parity[] ParityOptions { get; } = [System.IO.Ports.Parity.None, System.IO.Ports.Parity.Odd, System.IO.Ports.Parity.Even, System.IO.Ports.Parity.Mark, System.IO.Ports.Parity.Space];
    public System.IO.Ports.StopBits[] StopBitsOptions { get; } = [System.IO.Ports.StopBits.One, System.IO.Ports.StopBits.OnePointFive, System.IO.Ports.StopBits.Two];
    public System.IO.Ports.Handshake[] HandshakeOptions { get; } = [System.IO.Ports.Handshake.None, System.IO.Ports.Handshake.RequestToSend, System.IO.Ports.Handshake.XOnXOff, System.IO.Ports.Handshake.RequestToSendXOnXOff];
    public bool HasSerialPorts => SerialPorts.Count > 0;
    public bool ShellMode { get => _shellMode; set { if (SetProperty(ref _shellMode, value)) { SerialLoopEnabled = false; RunQueueCommand.Cancel(); } } }
    public string SerialIntervalMs { get => _serialIntervalMs; set => SetProperty(ref _serialIntervalMs, value); }
    public bool QueueRepeat { get => _queueRepeat; set => SetProperty(ref _queueRepeat, value); }
    public string SerialJobStatus { get => _serialJobStatus; private set => SetProperty(ref _serialJobStatus, value); }
    public bool SerialLoopEnabled
    {
        get => _serialLoopEnabled;
        set
        {
            if (!SetProperty(ref _serialLoopEnabled, value)) return;
            if (value) { RunQueueCommand.Cancel(); _ = RunLoopAsync(); }
        }
    }

    public string ActivityStatus { get => _activityStatus; private set => SetProperty(ref _activityStatus, value); }
    public NetworkSessionViewModel? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (_selectedSession is not null) _selectedSession.PropertyChanged -= OnSelectedSessionChanged;
            SetProperty(ref _selectedSession, value);
            if (value is not null) value.PropertyChanged += OnSelectedSessionChanged;
            RemoveSelectedNetworkCommand.NotifyCanExecuteChanged();
            SendSelectedNetworkCommand.NotifyCanExecuteChanged();
        }
    }
    public SerialPortRowViewModel? SelectedSerialPort
    {
        get => _selectedSerialPort;
        set { if (SetProperty(ref _selectedSerialPort, value)) { SerialLoopEnabled = false; RunQueueCommand.Cancel(); NotifySerialCommands(); } }
    }

    public string NetworkProtocol { get; set; } = "TCP";
    public string NetworkMode { get; set; } = "客户端";
    public string LocalHost { get; set; } = "0.0.0.0";
    public string LocalPort { get; set; } = "9000";
    public string RemoteHost { get; set; } = "127.0.0.1";
    public string RemotePort { get; set; } = "9000";
    public bool AutoReconnect { get; set; } = true;
    public bool TreatAsHttp { get; set; }
    public bool ScheduledSendEnabled { get; set; }
    public string ScheduleIntervalMs { get; set; } = "1000";
    public string ScheduledPayload { get; set; } = "ping";
    public string SerialSendText { get => _serialSendText; set { if (SetProperty(ref _serialSendText, value)) SendSelectedSerialCommand.NotifyCanExecuteChanged(); } }
    public string NetworkSendText { get => _networkSendText; set { if (SetProperty(ref _networkSendText, value)) SendSelectedNetworkCommand.NotifyCanExecuteChanged(); } }
    public bool SerialSendAsHex { get => _serialSendAsHex; set => SetProperty(ref _serialSendAsHex, value); }
    public bool SerialAppendCrLf { get => _serialAppendCrLf; set => SetProperty(ref _serialAppendCrLf, value); }
    public bool NetworkSendAsHex { get => _networkSendAsHex; set => SetProperty(ref _networkSendAsHex, value); }
    public bool NetworkAppendCrLf { get => _networkAppendCrLf; set => SetProperty(ref _networkAppendCrLf, value); }
    public bool EnableVirtualSerialPorts { get; set; }
    public string BridgeLocalHost { get; set; } = "0.0.0.0";
    public string BridgeLocalPort { get; set; } = "9100";
    public string BridgeRemoteHost { get; set; } = "127.0.0.1";
    public string BridgeRemotePort { get; set; } = "9100";
    public string BridgeMode { get => _bridgeMode; set { if (SetProperty(ref _bridgeMode, value)) { OnPropertyChanged(nameof(IsBridgeServer)); StartBridgeCommand.NotifyCanExecuteChanged(); } } }
    public bool IsBridgeServer => BridgeMode == "服务端";
    public string BridgeStatus => _remoteSerialClient.IsConnected ? "客户端已连接" : _bridgeId is null ? "未启动" : "服务端运行中";
    public bool IsBridgeRunning => _bridgeId is not null || _remoteSerialClient.IsConnected;
    public bool CanConfigureBridge => !IsBridgeRunning;

    public IAsyncRelayCommand AddNetworkSessionCommand { get; }
    public IAsyncRelayCommand RemoveSelectedNetworkCommand { get; }
    public IAsyncRelayCommand SendSelectedNetworkCommand { get; }
    public IAsyncRelayCommand ScanSerialPortsCommand { get; }
    public IAsyncRelayCommand OpenSelectedSerialCommand { get; }
    public IAsyncRelayCommand CloseSelectedSerialCommand { get; }
    public IAsyncRelayCommand SendSelectedSerialCommand { get; }
    public IAsyncRelayCommand StartBridgeCommand { get; }
    public IAsyncRelayCommand StopBridgeCommand { get; }
    public IRelayCommand ClearLogsCommand { get; }
    public IRelayCommand AddQueueItemCommand { get; }
    public IRelayCommand RemoveQueueItemCommand { get; }
    public IAsyncRelayCommand RunQueueCommand { get; }
    public IRelayCommand StopSerialJobCommand { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await ScanSerialPortsAsync(cancellationToken);
        AddLog("系统", "Embedded Debugger 已就绪", TransportEventKind.Info);
    }

    public async ValueTask DisposeAsync()
    {
        SerialLoopEnabled = false;
        RunQueueCommand.Cancel();
        _remoteSerialClient.PortsChanged -= OnRemotePortsChanged;
        await _remoteSerialClient.DisconnectAsync();
        foreach (var session in Sessions.ToArray()) await session.DisposeAsync();
        foreach (var channel in _openSerialChannels.Values.ToArray()) await channel.DisposeAsync();
        if (_bridgeId is not null) await _serialBridgeService.StopAsync(_bridgeId);
        if (RemoteDebug is not null) await RemoteDebug.DisposeAsync();
    }

    private async Task AddNetworkSessionAsync(CancellationToken cancellationToken = default)
    {
        var options = new TransportOptions
        {
            Protocol = NetworkProtocol.Equals("UDP", StringComparison.OrdinalIgnoreCase) ? TransportProtocol.Udp : TransportProtocol.Tcp,
            Mode = NetworkMode == "服务端" ? TransportMode.Server : TransportMode.Client,
            LocalHost = LocalHost,
            LocalPort = ParsePort(LocalPort, 9000),
            RemoteHost = RemoteHost,
            RemotePort = ParsePort(RemotePort, 9000),
            AutoReconnect = AutoReconnect,
            TreatAsHttp = TreatAsHttp,
            SendInterval = ScheduledSendEnabled ? TimeSpan.FromMilliseconds(Math.Max(50, ParseInt(ScheduleIntervalMs, 1000))) : null,
            ScheduledPayload = ScheduledSendEnabled ? ScheduledPayload : null
        };

        await CreateNetworkSessionAsync(options, cancellationToken);
    }

    public async Task<NetworkSessionViewModel> CreateNetworkSessionAsync(TransportOptions options, CancellationToken cancellationToken = default)
    {
        var vm = new NetworkSessionViewModel(_transportService.CreateSession(options), AddLog);
        Sessions.Add(vm);
        SelectedSession = vm;
        try
        {
            await vm.StartAsync(cancellationToken);
            AddLog("网络", $"已启动 {vm.Detail}", TransportEventKind.Connected);
        }
        catch (Exception ex)
        {
            AddLog("网络", $"启动失败: {ex.Message}", TransportEventKind.Error);
        }
        return vm;
    }

    private async Task RemoveSelectedNetworkAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedSession is null) return;
        var session = SelectedSession;
        Sessions.Remove(session);
        SelectedSession = Sessions.FirstOrDefault();
        await session.DisposeAsync();
        AddLog("网络", $"已关闭 {session.Detail}", TransportEventKind.Disconnected);
    }

    private async Task SendSelectedNetworkAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedSession is null) return;
        try
        {
            await SelectedSession.SendAsync(SendPayload.Encode(NetworkSendText, NetworkSendAsHex, NetworkAppendCrLf), cancellationToken);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or System.IO.IOException or System.Net.Sockets.SocketException)
        {
            AddLog("网络", $"发送失败: {ex.Message}", TransportEventKind.Error);
        }
    }

    private async Task ScanSerialPortsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var ports = await _serialPortService.ScanAsync(cancellationToken);
            var selectedName = SelectedSerialPort?.PortName;
            var knownPorts = SerialPorts.ToDictionary(port => port.PortName, StringComparer.OrdinalIgnoreCase);
            var rows = ports.Select(port => knownPorts.TryGetValue(port.PortName, out var row) ? row : new SerialPortRowViewModel(port)).ToList();
            rows.AddRange(SerialPorts.Where(port => (port.IsOpen || port.IsRemote) && rows.All(row => row.PortName != port.PortName)));
            foreach (var row in SerialPorts) row.PropertyChanged -= OnSerialPortChanged;
            SerialPorts.Clear();
            foreach (var row in rows) { row.PropertyChanged += OnSerialPortChanged; SerialPorts.Add(row); }
            SelectedSerialPort = SerialPorts.FirstOrDefault(port => port.PortName == selectedName) ?? SerialPorts.FirstOrDefault();
            StartBridgeCommand.NotifyCanExecuteChanged();
            AddLog("串口", $"扫描到 {ports.Count} 个本地串口", TransportEventKind.Info);
        }
        catch (Exception ex)
        {
            AddLog("串口", $"扫描失败: {ex.Message}", TransportEventKind.Error);
        }
    }

    private async Task OpenSelectedSerialAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedSerialPort is null) return;
        var row = SelectedSerialPort;
        ISerialPortChannel? channel = null;
        try
        {
            channel = row.IsRemote ? _remoteSerialClient.CreateChannel(row.PortName, row.ToSettings()) : _serialPortService.CreateChannel(row.ToSettings());
            channel.DataReceived += OnSerialDataReceived;
            await channel.OpenAsync(cancellationToken);
            _openSerialChannels[row.PortName] = channel;
            row.IsOpen = true;
            AddLog("串口", $"已打开 {row.PortName} @ {row.EffectiveBaudRate}", TransportEventKind.Connected);
        }
        catch (Exception ex)
        {
            if (channel is not null) { channel.DataReceived -= OnSerialDataReceived; await channel.DisposeAsync(); }
            AddLog("串口", $"打开失败: {ex.Message}", TransportEventKind.Error);
        }
    }

    private async Task CloseSelectedSerialAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedSerialPort is null || !_openSerialChannels.Remove(SelectedSerialPort.PortName, out var channel)) return;
        SerialLoopEnabled = false;
        RunQueueCommand.Cancel();
        channel.DataReceived -= OnSerialDataReceived;
        await channel.CloseAsync(cancellationToken);
        await channel.DisposeAsync();
        SelectedSerialPort.IsOpen = false;
        AddLog("串口", $"已关闭 {SelectedSerialPort.PortName}", TransportEventKind.Disconnected);
    }

    private async Task SendSelectedSerialAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedSerialPort is null || !_openSerialChannels.TryGetValue(SelectedSerialPort.PortName, out var channel)) return;
        if (string.IsNullOrEmpty(SerialSendText)) return;
        try
        {
            var row = SelectedSerialPort;
            var payload = SendPayload.Encode(SerialSendText, SerialSendAsHex, SerialAppendCrLf);
            await channel.SendAsync(payload, cancellationToken);
            row.Terminal.Add(DateTimeOffset.Now, false, payload);
            AddLog("串口", $"TX {row.PortName}: {payload.Length} B", TransportEventKind.Sent);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or System.IO.IOException)
        {
            AddLog("串口", $"发送失败: {ex.Message}", TransportEventKind.Error);
        }
    }

    private async Task StartBridgeAsync(CancellationToken cancellationToken = default)
    {
        if (IsBridgeRunning) return;
        if (!IsBridgeServer)
        {
            try
            {
                await _remoteSerialClient.ConnectAsync(BridgeRemoteHost, ParsePort(BridgeRemotePort, 9100), cancellationToken);
                NotifyBridgeState();
                AddLog("映射", $"已连接网络串口服务 {BridgeRemoteHost}:{BridgeRemotePort}", TransportEventKind.Connected);
            }
            catch (Exception ex) { AddLog("映射", $"连接失败: {ex.Message}", TransportEventKind.Error); }
            return;
        }
        var selected = SerialPorts.Where(row => row.IsSelected && !row.IsRemote).ToArray();
        if (selected.Length == 0)
        {
            AddLog("映射", "请先勾选需要复用的串口通道", TransportEventKind.Error);
            return;
        }
        if (selected.Any(port => port.IsOpen))
        {
            AddLog("映射", "选中的串口正在用于调试，请先断开后再启动映射", TransportEventKind.Error);
            return;
        }
        var profile = new SerialBridgeProfile
        {
            Name = "Desktop Bridge",
            Mode = BridgeMode == "客户端" ? SerialBridgeMode.Client : SerialBridgeMode.Server,
            LocalHost = BridgeLocalHost,
            LocalPort = ParsePort(BridgeLocalPort, 9100),
            RemoteHost = BridgeRemoteHost,
            RemotePort = ParsePort(BridgeRemotePort, 9100),
            EnableMultiplexing = true,
            CreateVirtualPorts = EnableVirtualSerialPorts,
            Channels = selected.Select(row => row.ToSettings()).ToArray()
        };
        try
        {
            _bridgeId = await _serialBridgeService.StartAsync(profile, cancellationToken);
            OnPropertyChanged(nameof(BridgeStatus));
            NotifyBridgeState();
            AddLog("映射", $"串口复用链路已启动，通道数: {selected.Length}", TransportEventKind.Connected);
        }
        catch (Exception ex)
        {
            AddLog("映射", $"启动失败: {ex.Message}", TransportEventKind.Error);
        }
    }

    private async Task StopBridgeAsync(CancellationToken cancellationToken = default)
    {
        if (_remoteSerialClient.IsConnected)
        {
            await _remoteSerialClient.DisconnectAsync();
            NotifyBridgeState();
            AddLog("映射", "网络串口客户端已断开", TransportEventKind.Disconnected);
            return;
        }
        if (_bridgeId is null) return;
        await _serialBridgeService.StopAsync(_bridgeId, cancellationToken);
        _bridgeId = null;
        OnPropertyChanged(nameof(BridgeStatus));
        NotifyBridgeState();
        AddLog("映射", "串口复用链路已停止", TransportEventKind.Disconnected);
    }

    private void OnSerialDataReceived(object? sender, ReadOnlyMemory<byte> data)
    {
        var channel = sender as ISerialPortChannel;
        var bytes = data.ToArray();
        RunOnUi(() =>
        {
            SerialPorts.FirstOrDefault(row => row.PortName == channel?.Settings.PortName)?.Terminal.Add(DateTimeOffset.Now, true, bytes);
            AddLog("串口", $"RX {channel?.Settings.PortName ?? "-"}: {bytes.Length} B", TransportEventKind.Received);
        });
    }

    private void AddLog(string category, string message, TransportEventKind kind)
    {
        var action = new Action(() =>
        {
            Logs.Insert(0, new LogEntryViewModel(DateTimeOffset.Now, category, message, kind));
            while (Logs.Count > 1000) Logs.RemoveAt(Logs.Count - 1);
            ActivityStatus = message;
        });
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    private static int ParsePort(string value, int fallback) => Math.Clamp(ParseInt(value, fallback), 1, 65535);
    private static int ParseInt(string value, int fallback) => int.TryParse(value, out var result) ? result : fallback;

    private void OnSelectedSessionChanged(object? sender, PropertyChangedEventArgs e) => SendSelectedNetworkCommand.NotifyCanExecuteChanged();
    private void OnSerialPortChanged(object? sender, PropertyChangedEventArgs e)
    {
        NotifySerialCommands();
        StartBridgeCommand.NotifyCanExecuteChanged();
    }
    private void NotifySerialCommands()
    {
        OpenSelectedSerialCommand.NotifyCanExecuteChanged();
        CloseSelectedSerialCommand.NotifyCanExecuteChanged();
        SendSelectedSerialCommand.NotifyCanExecuteChanged();
        RunQueueCommand.NotifyCanExecuteChanged();
    }
    private void NotifyBridgeState()
    {
        OnPropertyChanged(nameof(IsBridgeRunning));
        OnPropertyChanged(nameof(CanConfigureBridge));
        StartBridgeCommand.NotifyCanExecuteChanged();
        StopBridgeCommand.NotifyCanExecuteChanged();
        NotifySerialCommands();
    }
    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    public async Task SendShellInputAsync(string text)
    {
        if (SelectedSerialPort is null || !_openSerialChannels.TryGetValue(SelectedSerialPort.PortName, out var channel)) return;
        try
        {
            var row = SelectedSerialPort;
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            await channel.SendAsync(bytes);
            row.Terminal.Add(DateTimeOffset.Now, false, bytes);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException) { AddLog("串口", $"发送失败: {ex.Message}", TransportEventKind.Error); }
    }

    private async Task RunLoopAsync()
    {
        if (_serialSending) { SerialLoopEnabled = false; return; }
        if (SelectedSerialPort is not { IsOpen: true } || string.IsNullOrWhiteSpace(SerialSendText))
        {
            SerialLoopEnabled = false;
            SerialJobStatus = "串口未连接或发送内容为空";
            return;
        }
        _serialSending = true;
        NotifySerialCommands();
        var port = SelectedSerialPort;
        try
        {
            SerialJobStatus = "循环发送中";
            while (SerialLoopEnabled && port == SelectedSerialPort && port.IsOpen)
            {
                await SendSelectedSerialAsync();
                await Task.Delay(Math.Max(50, ParseInt(SerialIntervalMs, 1000)));
            }
        }
        finally { _serialSending = false; SerialLoopEnabled = false; SerialJobStatus = "已停止"; NotifySerialCommands(); }
    }

    private async Task RunQueueAsync(CancellationToken cancellationToken)
    {
        var port = SelectedSerialPort;
        if (port is null || !_openSerialChannels.TryGetValue(port.PortName, out var channel)) return;
        var items = SerialSendQueue.Where(item => item.Enabled && !string.IsNullOrWhiteSpace(item.Payload)).Select(item => item.Payload).ToArray();
        if (items.Length == 0) { SerialJobStatus = "队列为空"; return; }
        _serialSending = true;
        NotifySerialCommands();
        try
        {
            do
            {
                for (var index = 0; index < items.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SerialJobStatus = $"队列发送 {index + 1}/{items.Length}";
                    var payload = SendPayload.Encode(items[index], SerialSendAsHex, SerialAppendCrLf);
                    await channel.SendAsync(payload, cancellationToken);
                    port.Terminal.Add(DateTimeOffset.Now, false, payload);
                    await Task.Delay(Math.Max(50, ParseInt(SerialIntervalMs, 1000)), cancellationToken);
                }
            } while (QueueRepeat);
            SerialJobStatus = "队列已完成";
        }
        catch (OperationCanceledException) { SerialJobStatus = "已停止"; }
        catch (Exception ex) { SerialJobStatus = $"发送失败: {ex.Message}"; AddLog("串口", SerialJobStatus, TransportEventKind.Error); }
        finally { _serialSending = false; NotifySerialCommands(); }
    }

    private void OnRemotePortsChanged(object? sender, EventArgs e) => RunOnUi(() =>
    {
        var remotePorts = _remoteSerialClient.Ports;
        foreach (var row in SerialPorts.Where(row => row.IsRemote && remotePorts.All(port => port.PortName != row.PortName)).ToArray())
        {
            row.IsOpen = false;
            row.PropertyChanged -= OnSerialPortChanged;
            SerialPorts.Remove(row);
            if (_openSerialChannels.Remove(row.PortName, out var channel)) _ = channel.DisposeAsync();
        }
        foreach (var port in remotePorts.Where(port => SerialPorts.All(row => row.PortName != port.PortName)))
        {
            var row = new SerialPortRowViewModel(new SerialPortDescriptor(port.PortName, port.DisplayName, "网络串口 · 应用内虚拟端口", "EDBG")) { IsRemote = true };
            row.PropertyChanged += OnSerialPortChanged;
            SerialPorts.Add(row);
        }
        if (SelectedSerialPort is null || !SerialPorts.Contains(SelectedSerialPort)) SelectedSerialPort = SerialPorts.FirstOrDefault();
        NotifyBridgeState();
    });
}
