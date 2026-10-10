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
    private readonly Dictionary<string, SerialSessionViewModel> _serialOwners = new(StringComparer.OrdinalIgnoreCase);
    private SerialSessionViewModel? _selectedSerialSession;
    private int _serialSessionNumber;
    private bool _disposed, _bridgeStarting;
    private NetworkSessionViewModel? _selectedSession;
    private string _networkSendText = string.Empty;
    private string _activityStatus = "就绪";
    private bool _networkSendAsHex;
    private bool _networkAppendCrLf;
    private string? _bridgeId;
    private string _bridgeMode = "服务端";

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
        _remoteSerialClient.ConnectionChanged += OnRemoteConnectionChanged;

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
        StartBridgeCommand = new AsyncRelayCommand(StartBridgeAsync, () => !IsBridgeRunning && (BridgeMode == "客户端" || SerialPorts.Any(port => port.IsSelected && !port.IsRemote)));
        StopBridgeCommand = new AsyncRelayCommand(StopBridgeAsync, () => IsBridgeRunning);
        ClearLogsCommand = new RelayCommand(() => Logs.Clear());
        AddSerialSessionCommand = new RelayCommand(() => AddSerialSession());
        AddSerialSession();
        SerialPorts.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSerialPorts));
    }

    public ObservableCollection<string> ProtocolOptions { get; }
    public ObservableCollection<string> ModeOptions { get; }
    public ObservableCollection<int> BaudRateOptions { get; }
    public ObservableCollection<NetworkSessionViewModel> Sessions { get; }
    public ObservableCollection<SerialPortRowViewModel> SerialPorts { get; }
    public ObservableCollection<LogEntryViewModel> Logs { get; }
    public RemoteDebugWorkspaceViewModel? RemoteDebug { get; }
    public int[] DataBitsOptions { get; } = [5, 6, 7, 8];
    public System.IO.Ports.Parity[] ParityOptions { get; } = [System.IO.Ports.Parity.None, System.IO.Ports.Parity.Odd, System.IO.Ports.Parity.Even, System.IO.Ports.Parity.Mark, System.IO.Ports.Parity.Space];
    public System.IO.Ports.StopBits[] StopBitsOptions { get; } = [System.IO.Ports.StopBits.One, System.IO.Ports.StopBits.OnePointFive, System.IO.Ports.StopBits.Two];
    public System.IO.Ports.Handshake[] HandshakeOptions { get; } = [System.IO.Ports.Handshake.None, System.IO.Ports.Handshake.RequestToSend, System.IO.Ports.Handshake.XOnXOff, System.IO.Ports.Handshake.RequestToSendXOnXOff];
    public bool HasSerialPorts => SerialPorts.Count > 0;
    public ObservableCollection<SerialSessionViewModel> SerialSessions { get; } = [];
    public IRelayCommand AddSerialSessionCommand { get; }
    public SerialSessionViewModel? SelectedSerialSession
    {
        get => _selectedSerialSession;
        set { if (value is not null && SerialSessions.Contains(value)) SetProperty(ref _selectedSerialSession, value); }
    }

    public SerialSessionViewModel AddSerialSession()
    {
        var session = new SerialSessionViewModel(this, ++_serialSessionNumber);
        session.RefreshPorts(SerialPorts.ToArray());
        session.SelectedSerialPort = session.SerialPorts.FirstOrDefault(port => !SerialSessions.Any(s => s.SelectedSerialPort?.PortName == port.PortName)) ?? session.SelectedSerialPort;
        SerialSessions.Add(session);
        SelectedSerialSession = session;
        return session;
    }

    public async Task CloseSerialSessionAsync(SerialSessionViewModel session)
    {
        if (!SerialSessions.Contains(session)) return;
        await session.DisposeAsync();
        var index = SerialSessions.IndexOf(session);
        if (index < 0) return;
        SerialSessions.RemoveAt(index);
        if (SerialSessions.Count == 0 && !_disposed) AddSerialSession();
        else if (SelectedSerialSession == session && SerialSessions.Count > 0)
            SelectedSerialSession = SerialSessions[Math.Min(index, SerialSessions.Count - 1)];
    }

    internal bool TryAcquireSerialPort(SerialSessionViewModel session, SerialPortRowViewModel port, out string error)
    {
        if (_serialOwners.TryGetValue(port.PortName, out var owner))
        {
            SelectedSerialSession = owner;
            error = "串口已在其他标签打开";
            AddSerialLog($"{port.PortName} 已在其他标签打开，已切换到该标签", TransportEventKind.Info);
            return false;
        }
        if (!port.IsRemote && (_bridgeId is not null || _bridgeStarting)) { error = "请先停止串口映射服务"; return false; }
        _serialOwners.Add(port.PortName, session);
        UpdateSerialUsage(); error = ""; return true;
    }
    internal void ReleaseSerialPort(SerialSessionViewModel session)
    {
        foreach (var key in _serialOwners.Where(pair => pair.Value == session).Select(pair => pair.Key).ToArray()) _serialOwners.Remove(key);
        UpdateSerialUsage();
    }
    private void UpdateSerialUsage()
    {
        foreach (var row in SerialPorts) row.IsOpen = _serialOwners.ContainsKey(row.PortName);
        StartBridgeCommand.NotifyCanExecuteChanged();
    }
    internal ISerialPortChannel CreateSerialChannel(SerialPortRowViewModel port) => port.IsRemote
        ? _remoteSerialClient.CreateChannel(port.PortName, port.ToSettings()) : _serialPortService.CreateChannel(port.ToSettings());
    internal void AddSerialLog(string message, TransportEventKind kind) => AddLog("串口", message, kind);
    private void RefreshSerialSessions()
    {
        var available = SerialPorts.ToArray();
        foreach (var session in SerialSessions) session.RefreshPorts(available);
        UpdateSerialUsage();
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
    public string NetworkSendText { get => _networkSendText; set { if (SetProperty(ref _networkSendText, value)) SendSelectedNetworkCommand.NotifyCanExecuteChanged(); } }
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
    public IAsyncRelayCommand StartBridgeCommand { get; }
    public IAsyncRelayCommand StopBridgeCommand { get; }
    public IRelayCommand ClearLogsCommand { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await ScanSerialPortsAsync(cancellationToken);
        AddLog("系统", "Embedded Debugger 已就绪", TransportEventKind.Info);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var session in SerialSessions.ToArray()) await session.DisposeAsync();
        _remoteSerialClient.PortsChanged -= OnRemotePortsChanged;
        _remoteSerialClient.ConnectionChanged -= OnRemoteConnectionChanged;
        await _remoteSerialClient.DisconnectAsync();
        foreach (var session in Sessions.ToArray()) await session.DisposeAsync();
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
            var knownPorts = SerialPorts.ToDictionary(port => port.PortName, StringComparer.OrdinalIgnoreCase);
            var rows = ports.Select(port => knownPorts.TryGetValue(port.PortName, out var row) ? row : new SerialPortRowViewModel(port)).ToList();
            rows.AddRange(SerialPorts.Where(port => port.IsRemote));
            foreach (var row in SerialPorts) row.PropertyChanged -= OnSerialPortChanged;
            SerialPorts.Clear();
            foreach (var row in rows) { row.PropertyChanged += OnSerialPortChanged; SerialPorts.Add(row); }
            RefreshSerialSessions();
            StartBridgeCommand.NotifyCanExecuteChanged();
            AddLog("串口", $"扫描到 {ports.Count} 个本地串口", TransportEventKind.Info);
        }
        catch (Exception ex)
        {
            AddLog("串口", $"扫描失败: {ex.Message}", TransportEventKind.Error);
        }
    }

    private async Task StartBridgeAsync(CancellationToken cancellationToken = default)
    {
        if (IsBridgeRunning || _bridgeStarting) return;
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
        _bridgeStarting = true;
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
        finally { _bridgeStarting = false; }
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

    private void OnSelectedSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NetworkSessionViewModel.CanSend) or nameof(NetworkSessionViewModel.State)) SendSelectedNetworkCommand.NotifyCanExecuteChanged();
    }
    private void OnSerialPortChanged(object? sender, PropertyChangedEventArgs e)
    {
        StartBridgeCommand.NotifyCanExecuteChanged();
    }
    private void NotifyBridgeState()
    {
        OnPropertyChanged(nameof(IsBridgeRunning));
        OnPropertyChanged(nameof(CanConfigureBridge));
        StartBridgeCommand.NotifyCanExecuteChanged();
        StopBridgeCommand.NotifyCanExecuteChanged();
    }
    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    private void OnRemoteConnectionChanged(object? sender, EventArgs e) => RunOnUi(NotifyBridgeState);
    private void OnRemotePortsChanged(object? sender, EventArgs e) => RunOnUi(() =>
    {
        if (_disposed) return;
        var remotePorts = _remoteSerialClient.Ports;
        foreach (var row in SerialPorts.Where(row => row.IsRemote && remotePorts.All(port => port.PortName != row.PortName)).ToArray())
        {
            row.IsOpen = false;
            row.PropertyChanged -= OnSerialPortChanged;
            SerialPorts.Remove(row);
        }
        foreach (var port in remotePorts.Where(port => SerialPorts.All(row => row.PortName != port.PortName)))
        {
            var row = new SerialPortRowViewModel(new SerialPortDescriptor(port.PortName, port.DisplayName, "网络串口 · 应用内虚拟端口", "EDBG")) { IsRemote = true };
            row.PropertyChanged += OnSerialPortChanged;
            SerialPorts.Add(row);
        }
        RefreshSerialSessions();
        NotifyBridgeState();
    });
}
