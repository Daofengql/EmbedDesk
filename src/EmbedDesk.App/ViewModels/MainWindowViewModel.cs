using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbedDesk.Core.Abstractions;
using EmbedDesk.Core.Models;
using EmbedDesk.Infrastructure.Connections;
using EmbedDesk.Infrastructure.Serial;

namespace EmbedDesk.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ITransportService _transportService;
    private readonly ISerialPortService _serialPortService;
    private readonly ISerialBridgeService _serialBridgeService;
    private readonly IRemoteSerialClient _remoteSerialClient;
    private readonly AppSettingsStore _appSettings;
    private readonly Dictionary<string, SerialSessionViewModel> _serialOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _serialScanGate = new(1, 1);
    private readonly SerialPortAutoRefresh _serialPortAutoRefresh;
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
    private string _bridgeLocalHost = "0.0.0.0", _bridgeLocalPort = "9100";
    private string _bridgeRemoteHost = "127.0.0.1", _bridgeRemotePort = "9100";

    public MainWindowViewModel(
        ITransportService transportService,
        ISerialPortService serialPortService,
        ISerialBridgeService serialBridgeService,
        IRemoteSerialClient remoteSerialClient,
        IRemoteDebugService? remoteDebugService = null,
        string? remoteSettingsDirectory = null,
        LocalShellStartupOptions? startup = null)
    {
        _transportService = transportService;
        _serialPortService = serialPortService is ISerialPortSharing ? serialPortService : new SharedSerialPortService(serialPortService);
        DebugSerialPortService = new WorkspaceSerialPortService(this);
        _serialBridgeService = serialBridgeService;
        _remoteSerialClient = remoteSerialClient;
        _appSettings = new AppSettingsStore(remoteSettingsDirectory);
        RemoteDebug = remoteDebugService is null ? null : new RemoteDebugWorkspaceViewModel(remoteDebugService, remoteSettingsDirectory, _appSettings, startup);
        _remoteSerialClient.PortsChanged += OnRemotePortsChanged;
        _remoteSerialClient.ConnectionChanged += OnRemoteConnectionChanged;

        ProtocolOptions = new ObservableCollection<string> { "TCP", "UDP" };
        ModeOptions = new ObservableCollection<string> { "客户端", "服务端" };
        BaudRateOptions = new ObservableCollection<int> { 110, 300, 600, 1200, 2400, 4800, 7200, 9600, 14400, 19200, 28800, 38400, 56000, 57600, 115200, 128000, 230400, 460800, 500000, 921600, 1000000, 1500000, 2000000 };
        Sessions = new ObservableCollection<NetworkSessionViewModel>();
        NetworkTools = new ObservableCollection<NetworkToolViewModel>
        {
            new("MQTT 客户端", "发布 / 订阅", "\uE774", TransportProtocol.Mqtt, TransportMode.Client),
            new("TCP 客户端", "主动连接远端", "\uE968", TransportProtocol.Tcp, TransportMode.Client),
            new("TCP 服务器", "监听多个客户端", "\uE968", TransportProtocol.Tcp, TransportMode.Server),
            new("UDP 客户端", "无连接数据报", "\uE968", TransportProtocol.Udp, TransportMode.Client),
            new("UDP 服务器", "接收多个对端", "\uE968", TransportProtocol.Udp, TransportMode.Server),
            new("HTTP 客户端", "TCP 请求诊断", "\uE774", TransportProtocol.Tcp, TransportMode.Client, true),
            new("HTTP 服务器", "自动回复 HTTP 200", "\uE774", TransportProtocol.Tcp, TransportMode.Server, true)
        };
        SelectedNetworkTool = NetworkTools[1];
        SerialPorts = new ObservableCollection<SerialPortRowViewModel>();
        Logs = new ObservableCollection<LogEntryViewModel>();
        _serialPortAutoRefresh = new SerialPortAutoRefresh(
            _serialPortService,
            Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher,
            () => ScanSerialPortsAsync());

        AddNetworkSessionCommand = new AsyncRelayCommand(AddNetworkSessionAsync);
        RemoveSelectedNetworkCommand = new AsyncRelayCommand(RemoveSelectedNetworkAsync, () => SelectedSession is not null);
        SendSelectedNetworkCommand = new AsyncRelayCommand(SendSelectedNetworkAsync, () => SelectedSession?.CanSend == true && !string.IsNullOrWhiteSpace(NetworkSendText));
        ScanSerialPortsCommand = new AsyncRelayCommand(ScanSerialPortsAsync);
        StartBridgeCommand = new AsyncRelayCommand(StartBridgeAsync, () => CanConfigureBridge && (BridgeMode == "客户端" || SerialPorts.Any(port => port.IsSelected && !port.IsRemote)));
        StopBridgeCommand = new AsyncRelayCommand(StopBridgeAsync, () => IsBridgeRunning);
        ClearLogsCommand = new RelayCommand(() => Logs.Clear());
        AddSerialSessionCommand = new RelayCommand(() => AddSerialSession());
        SerialPorts.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSerialPorts));
        Connections = new ConnectionCenterViewModel(new DeviceNode(_serialPortService), _appSettings);
        Connections.Node.Changed += OnDeviceConnectionsChanged;
    }

    public ObservableCollection<string> ProtocolOptions { get; }
    public ObservableCollection<string> ModeOptions { get; }
    public ObservableCollection<int> BaudRateOptions { get; }
    public ObservableCollection<NetworkSessionViewModel> Sessions { get; }
    public ObservableCollection<NetworkToolViewModel> NetworkTools { get; }
    public NetworkToolViewModel? SelectedNetworkTool { get; set; }
    public ObservableCollection<SerialPortRowViewModel> SerialPorts { get; }
    public ObservableCollection<LogEntryViewModel> Logs { get; }
    public RemoteDebugWorkspaceViewModel? RemoteDebug { get; }
    public ConnectionCenterViewModel Connections { get; }
    internal AppSettingsStore AppSettings => _appSettings;
    internal NetworkDisplayPreferences NetworkDisplayPreferences => _appSettings.Snapshot().NetworkDisplayPreferences;
    internal IReadOnlyList<NetworkConnectionPreset> GetNetworkPresets() => _appSettings.GetNetworkPresets();
    internal void SaveNetworkPreset(NetworkConnectionPreset preset) => _appSettings.SaveNetworkPreset(preset);
    internal void DeleteNetworkPreset(Guid id) => _appSettings.DeleteNetworkPreset(id);
    internal IReadOnlyList<SerialBridgeClientPreset> GetSerialBridgePresets() => _appSettings.GetSerialBridgePresets();
    internal void SaveSerialBridgePreset(SerialBridgeClientPreset preset) => _appSettings.SaveSerialBridgePreset(preset);
    internal void DeleteSerialBridgePreset(Guid id) => _appSettings.DeleteSerialBridgePreset(id);
    internal ConfigurationMergeResult ImportConfiguration(ImportedConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var result = new ConfigurationMergeResult(0, configuration.DebugPresets.Count + configuration.NetworkPresets.Count)
            .Add(_appSettings.MergeSerialBridgePresets(configuration.SerialBridgePresets))
            .Add(_appSettings.MergeStcpTunnels(configuration.StcpTunnels));
        if (RemoteDebug is not null) result = result.Add(RemoteDebug.MergeProfiles(configuration.RemoteProfiles));
        return result;
    }
    internal void SaveNetworkDisplayPreferences(NetworkDisplayPreferences preferences) =>
        _appSettings.Update(settings => settings.NetworkDisplayPreferences = preferences);
    internal ISerialPortService SerialPortService => _serialPortService;
    internal ISerialPortService DebugSerialPortService { get; }
    private sealed class WorkspaceSerialPortService(MainWindowViewModel owner) : ISerialPortService
    {
        public async Task<IReadOnlyList<SerialPortDescriptor>> ScanAsync(CancellationToken cancellationToken = default)
        {
            await owner.ScanSerialPortsAsync(cancellationToken);
            return owner.SerialPorts.Select(row => new SerialPortDescriptor(row.PortName, row.DisplayName, row.Description, row.Manufacturer)).ToArray();
        }
        public ISerialPortChannel CreateChannel(SerialPortSettings settings) => settings.PortName.StartsWith("NODE:", StringComparison.Ordinal)
            ? owner.Connections.Node.CreateSerialChannel(settings.PortName, settings)
            : settings.PortName.StartsWith("REMOTE:", StringComparison.Ordinal)
                ? owner._remoteSerialClient.CreateChannel(settings.PortName, settings)
                : owner._serialPortService.CreateChannel(settings);
    }
    internal Task ReconfigureNetworkAsync(NetworkSessionViewModel session, TransportOptions options) =>
        session.ReconfigureAsync(_transportService.CreateSession(options));
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
        set { if (value is null || SerialSessions.Contains(value)) SetProperty(ref _selectedSerialSession, value); }
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

    public async Task CloseSerialSessionAsync(SerialSessionViewModel session, bool keepEmptyTab = false)
    {
        if (!SerialSessions.Contains(session)) return;
        await session.DisposeAsync();
        var index = SerialSessions.IndexOf(session);
        if (index < 0) return;
        SerialSessions.RemoveAt(index);
        if (SerialSessions.Count == 0 && !_disposed && keepEmptyTab) AddSerialSession();
        else if (SelectedSerialSession == session && SerialSessions.Count > 0)
            SelectedSerialSession = SerialSessions[Math.Min(index, SerialSessions.Count - 1)];
        else if (SelectedSerialSession == session) SelectedSerialSession = null;
    }

    internal bool TryAcquireSerialPort(SerialSessionViewModel session, SerialPortRowViewModel port, out string error)
    {
        if (!port.IsRemote && Connections.Node.IsShared(port.PortName)) { error = "串口已预留给网络共享，请先在连接中心取消共享。"; return false; }
        if (_serialOwners.TryGetValue(port.PortName, out var owner))
        {
            SelectedSerialSession = owner;
            error = "串口已在其他标签打开";
            AddSerialLog($"{port.PortName} 已在其他标签打开，已切换到该标签", TransportEventKind.Info);
            return false;
        }
        _serialOwners.Add(port.PortName, session);
        UpdateSerialUsage(); error = ""; return true;
    }

    /// <summary>Returns whether a live serial debug tab currently owns this port.</summary>
    internal bool IsSerialPortInUse(string portName) => _serialOwners.ContainsKey(portName);

    /// <summary>Stops the debug tab that owns a port so a firmware tool can use it exclusively.</summary>
    internal async Task<bool> ReleaseSerialPortForToolAsync(string portName)
    {
        if (!_serialOwners.TryGetValue(portName, out var owner)) return true;
        await owner.CloseAsync("已为烧录工具释放");
        return !_serialOwners.ContainsKey(portName);
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
    internal ISerialPortChannel CreateSerialChannel(SerialPortRowViewModel port) => port.PortName.StartsWith("NODE:", StringComparison.Ordinal)
        ? Connections.Node.CreateSerialChannel(port.PortName, port.ToSettings()) : port.IsRemote
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
    public string BridgeLocalHost { get => _bridgeLocalHost; set => SetProperty(ref _bridgeLocalHost, value); }
    public string BridgeLocalPort { get => _bridgeLocalPort; set => SetProperty(ref _bridgeLocalPort, value); }
    public string BridgeRemoteHost { get => _bridgeRemoteHost; set => SetProperty(ref _bridgeRemoteHost, value); }
    public string BridgeRemotePort { get => _bridgeRemotePort; set => SetProperty(ref _bridgeRemotePort, value); }
    public string BridgeMode { get => _bridgeMode; set { if (SetProperty(ref _bridgeMode, value)) { OnPropertyChanged(nameof(IsBridgeServer)); StartBridgeCommand.NotifyCanExecuteChanged(); } } }
    public bool IsBridgeServer => BridgeMode == "服务端";
    public string BridgeStatus => _remoteSerialClient.IsConnected ? "客户端已连接" : _bridgeId is null ? "未启动" : "服务端运行中";
    public bool IsBridgeRunning => _bridgeId is not null || _remoteSerialClient.IsConnected;
    public bool CanConfigureBridge => !IsBridgeRunning && !_bridgeStarting;

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
        await Connections.InitializeAsync();
        AddLog("系统", "EmbedDesk 已就绪", TransportEventKind.Info);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _serialPortAutoRefresh.Dispose();
        Connections.Node.Changed -= OnDeviceConnectionsChanged;
        await Connections.DisposeAsync();
        foreach (var session in SerialSessions.ToArray()) await session.DisposeAsync();
        _remoteSerialClient.PortsChanged -= OnRemotePortsChanged;
        _remoteSerialClient.ConnectionChanged -= OnRemoteConnectionChanged;
        await _remoteSerialClient.DisconnectAsync();
        foreach (var session in Sessions.ToArray()) await session.DisposeAsync();
        if (_bridgeId is not null) await _serialBridgeService.StopAsync(_bridgeId);
        if (RemoteDebug is not null) await RemoteDebug.DisposeAsync();
        _appSettings.Dispose();
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

    public async Task<NetworkSessionViewModel?> CreateNetworkSessionFromToolAsync(NetworkToolViewModel tool, CancellationToken cancellationToken = default)
    {
        if (!tool.IsSupported || tool.Protocol is null || tool.Mode is null) return null;
        var server = tool.Mode == TransportMode.Server;
        var options = new TransportOptions
        {
            Protocol = tool.Protocol.Value,
            Mode = tool.Mode.Value,
            LocalHost = "0.0.0.0",
            LocalPort = server ? 9000 : 0,
            RemoteHost = "127.0.0.1",
            RemotePort = tool.Protocol == TransportProtocol.Mqtt ? 1883 : 9000,
            AutoReconnect = !server,
            TreatAsHttp = tool.TreatAsHttp,
            MqttTopic = "embeddesk"
        };
        return await CreateNetworkSessionAsync(options, cancellationToken);
    }

    public async Task<NetworkSessionViewModel> CreateNetworkSessionAsync(TransportOptions options, CancellationToken cancellationToken = default)
    {
        var vm = new NetworkSessionViewModel(_transportService.CreateSession(options), AddLog,
            _appSettings.Snapshot().NetworkDisplayPreferences, SaveNetworkDisplayPreferences);
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
        await RemoveNetworkSessionAsync(SelectedSession, cancellationToken);
    }

    public async Task RemoveNetworkSessionAsync(NetworkSessionViewModel session, CancellationToken cancellationToken = default)
    {
        if (!Sessions.Contains(session)) return;
        await session.DisposeAsync();
        Sessions.Remove(session);
        if (SelectedSession == session) SelectedSession = Sessions.FirstOrDefault();
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
        await _serialScanGate.WaitAsync(cancellationToken);
        try
        {
            if (_disposed) return;
            var ports = await _serialPortService.ScanAsync(cancellationToken);
            var knownPorts = SerialPorts.ToDictionary(port => port.PortName, StringComparer.OrdinalIgnoreCase);
            var rows = ports.Select(port => knownPorts.TryGetValue(port.PortName, out var row) ? row : CreateSerialPortRow(port)).ToList();
            rows.AddRange(SerialPorts.Where(port => port.IsRemote));
            foreach (var row in SerialPorts) row.PropertyChanged -= OnSerialPortChanged;
            SerialPorts.Clear();
            foreach (var row in rows) { row.PropertyChanged += OnSerialPortChanged; SerialPorts.Add(row); }
            Connections.UpdateLocalSerialDevices(ports);
            RefreshSerialSessions();
            OnDeviceConnectionsChanged(this, EventArgs.Empty);
            StartBridgeCommand.NotifyCanExecuteChanged();
            AddLog("串口", $"扫描到 {ports.Count} 个本地串口", TransportEventKind.Info);
        }
        catch (Exception ex)
        {
            AddLog("串口", $"扫描失败: {ex.Message}", TransportEventKind.Error);
        }
        finally { _serialScanGate.Release(); }
    }

    private async Task StartBridgeAsync(CancellationToken cancellationToken = default)
    {
        if (!CanConfigureBridge) return;
        _bridgeStarting = true; NotifyBridgeState();
        try
        {
            if (!IsBridgeServer)
            {
                await _remoteSerialClient.ConnectAsync(BridgeRemoteHost, ParsePort(BridgeRemotePort, 9100), cancellationToken);
                AddLog("映射", $"已连接网络串口服务 {BridgeRemoteHost}:{BridgeRemotePort}", TransportEventKind.Connected);
                return;
            }
            var selected = SerialPorts.Where(row => row.IsSelected && !row.IsRemote).ToArray();
            if (selected.Length == 0) throw new InvalidOperationException("请先勾选需要映射的 COM。");
            var profile = new SerialBridgeProfile
            {
                Name = "Desktop Bridge", Mode = SerialBridgeMode.Server,
                LocalHost = BridgeLocalHost, LocalPort = ParsePort(BridgeLocalPort, 9100),
                EnableMultiplexing = true, CreateVirtualPorts = EnableVirtualSerialPorts,
                // The server stores only its allowlist; serial settings arrive from the client on Open.
                Channels = selected.Select(row => new SerialPortSettings { PortName = row.PortName }).ToArray()
            };
            _bridgeId = await _serialBridgeService.StartAsync(profile, cancellationToken);
            AddLog("映射", $"已发布 {selected.Length} 个 COM 映射，等待客户端下发参数打开", TransportEventKind.Connected);
        }
        catch (Exception ex) { AddLog("映射", $"启动失败: {ex.Message}", TransportEventKind.Error); }
        finally { _bridgeStarting = false; NotifyBridgeState(); }
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
    internal SerialPortRowViewModel CreateSerialPortRow(EmbedDesk.Core.Models.SerialPortDescriptor descriptor, bool isRemote = false)
    {
        var row = new SerialPortRowViewModel(descriptor) { IsRemote = isRemote, IsShared = !isRemote && _serialPortService is ISerialPortSharing sharing && sharing.IsShared(descriptor.PortName) };
        if (!isRemote) AppSettingsStore.ApplySerialSettings(_appSettings.GetSerialPreferences(row.PortName), row);
        row.SettingsChanged = _appSettings.SaveSerialPreferences;
        return row;
    }
    private void NotifyBridgeState()
    {
        OnPropertyChanged(nameof(BridgeStatus));
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
    private void OnDeviceConnectionsChanged(object? sender, EventArgs e) => RunOnUi(() =>
    {
        if (_disposed) return;
        var services = Connections.Node.RemoteServices.Where(s => s.Service.Kind == SharedServiceKind.Serial).ToArray();
        var changed = false;
        foreach (var row in SerialPorts.Where(r => r.PortName.StartsWith("NODE:", StringComparison.Ordinal) && services.All(s => s.PortName != r.PortName)).ToArray())
        { row.PropertyChanged -= OnSerialPortChanged; SerialPorts.Remove(row); changed = true; }
        foreach (var service in services.Where(s => SerialPorts.All(r => r.PortName != s.PortName)))
        {
            var row = CreateSerialPortRow(new(service.PortName, service.DisplayName, "设备共享串口 · 应用内端口", "EmbedDesk"), true);
            row.PropertyChanged += OnSerialPortChanged; SerialPorts.Add(row); changed = true;
        }
        foreach (var row in SerialPorts.Where(r => !r.IsRemote)) { var shared = Connections.Node.IsShared(row.PortName); if (row.IsShared != shared) { row.IsShared = shared; changed = true; } }
        if (changed) RefreshSerialSessions();
    });
    private void OnRemotePortsChanged(object? sender, EventArgs e) => RunOnUi(() =>
    {
        if (_disposed) return;
        var remotePorts = _remoteSerialClient.Ports;
        foreach (var row in SerialPorts.Where(row => row.IsRemote && !row.PortName.StartsWith("NODE:", StringComparison.Ordinal) && remotePorts.All(port => port.PortName != row.PortName)).ToArray())
        {
            row.IsOpen = false;
            row.PropertyChanged -= OnSerialPortChanged;
            SerialPorts.Remove(row);
        }
        foreach (var port in remotePorts.Where(port => SerialPorts.All(row => row.PortName != port.PortName)))
        {
            var row = CreateSerialPortRow(new SerialPortDescriptor(port.PortName, port.DisplayName, "网络串口 · 应用内虚拟端口", "EDBG"), isRemote: true);
            row.PropertyChanged += OnSerialPortChanged;
            SerialPorts.Add(row);
        }
        RefreshSerialSessions();
        NotifyBridgeState();
    });
}
