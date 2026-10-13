using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Sockets;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbedDesk.Core.Abstractions;
using EmbedDesk.Core.Models;

namespace EmbedDesk.App.ViewModels;

public sealed partial class RemoteWorkspaceViewModel : ObservableObject, IAsyncDisposable, IRemoteTabViewModel
{
    private readonly IRemoteDebugService _service;
    private IRemoteDebugSession? _session;
    private RemoteConnectionProfile _profile = new();
    private RemoteDirectoryEntryViewModel? _selectedEntry;
    private string _path = "/";
    private string _connectionStatus = "未连接";
    private string _connectionDetail = "SSH · 127.0.0.1:22";
    private string _lastError = string.Empty;
    private bool _isBusy;
    private double? _cpuPercent;
    private double? _memoryPercent;
    private double? _loadAverage;
    private long? _receiveBytes;
    private long? _transmitBytes;
    private string _uptime = "-";
    private string _performanceSource = "等待连接";
    private bool _isDirectoryPaneExpanded;
    private string _connectionLogText = string.Empty;
    private int? _processorCount;
    private string _connectionMessage = "等待开始连接…";
    private bool _isTerminalFullscreen;
    private bool _workspaceInitialized;
    private int _terminalColumns = 120;
    private int _terminalRows = 40;
    private string _customTitle = string.Empty;

    public RemoteWorkspaceViewModel(IRemoteDebugService service, RemoteConnectionProfile? profile = null)
    {
        _service = service;
        Profiles = new ObservableCollection<RemoteConnectionProfile>
        {
            profile ?? new RemoteConnectionProfile { Name = "本机 Linux", Host = "127.0.0.1", Port = 22, Username = "root" }
        };
        _profile = Profiles[0];
        DirectoryEntries = new ObservableCollection<RemoteDirectoryEntryViewModel>();
        ProtocolOptions = Enum.GetValues<RemoteProtocol>();
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsConnected && !IsBusy);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => IsConnected || IsBusy);
        RefreshDirectoryCommand = new AsyncRelayCommand(RefreshDirectoryAsync, () => IsConnected && !IsBusy && SupportsDirectoryManagement);
        GoUpCommand = new AsyncRelayCommand(GoUpAsync, () => IsConnected && !IsBusy && SupportsDirectoryManagement && ParentPath != Path);
        CreateDirectoryCommand = new AsyncRelayCommand(CreateDirectoryAsync, () => IsConnected && !IsBusy && SupportsDirectoryManagement);
        DeleteEntryCommand = new AsyncRelayCommand(DeleteEntryAsync, () => SelectedEntry is not null && !SelectedEntry.IsDirectory && IsConnected && !IsBusy && SupportsDirectoryManagement);
        SaveProfileCommand = new RelayCommand(SaveProfile);
        ToggleDirectoryPaneCommand = new RelayCommand(() =>
        {
            IsDirectoryPaneExpanded = !IsDirectoryPaneExpanded;
            if (IsDirectoryPaneExpanded) IsTerminalFullscreen = false;
        });
    }

    public ObservableCollection<RemoteConnectionProfile> Profiles { get; }
    public ObservableCollection<RemoteDirectoryEntryViewModel> DirectoryEntries { get; }
    public IReadOnlyList<RemoteProtocol> ProtocolOptions { get; }
    public TerminalBufferViewModel Terminal { get; } = new();
    public NetworkStatusViewModel NetworkStatus { get; } = new();
    public IAsyncRelayCommand ConnectCommand { get; }
    public IAsyncRelayCommand DisconnectCommand { get; }
    public IAsyncRelayCommand RefreshDirectoryCommand { get; }
    public IAsyncRelayCommand GoUpCommand { get; }
    public IAsyncRelayCommand CreateDirectoryCommand { get; }
    public IAsyncRelayCommand DeleteEntryCommand { get; }
    public IRelayCommand SaveProfileCommand { get; }
    public IRelayCommand ToggleDirectoryPaneCommand { get; }

    public RemoteConnectionProfile Profile
    {
        get => _profile;
        set
        {
            if (ReferenceEquals(_profile, value)) return;
            SetProperty(ref _profile, value);
            OnPropertyChanged(nameof(Protocol));
            OnPropertyChanged(nameof(Host));
            OnPropertyChanged(nameof(Port));
            OnPropertyChanged(nameof(Username));
            OnPropertyChanged(nameof(Password));
            OnPropertyChanged(nameof(AcceptUnknownHostKey));
            OnPropertyChanged(nameof(ConnectionDetail));
            OnPropertyChanged(nameof(DisplayTitle));
            OnPropertyChanged(nameof(TabTitle));
            OnPropertyChanged(nameof(SupportsDirectoryManagement));
            OnPropertyChanged(nameof(DirectoryStatus));
        }
    }

    public RemoteConnectionProfile? SelectedProfile
    {
        get => Profiles.FirstOrDefault(profile => ReferenceEquals(profile, Profile));
        set
        {
            if (value is null) return;
            Profile = value;
            OnPropertyChanged();
        }
    }

    public RemoteDirectoryEntryViewModel? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (SetProperty(ref _selectedEntry, value)) DeleteEntryCommand.NotifyCanExecuteChanged();
        }
    }

    public RemoteProtocol Protocol
    {
        get => Profile.Protocol;
        set
        {
            if (Profile.Protocol == value) return;
            var oldDefaultPort = Profile.Protocol == RemoteProtocol.Ssh ? 22 : 23;
            Profile.Protocol = value;
            if (Profile.Port == oldDefaultPort)
            {
                Profile.Port = value == RemoteProtocol.Ssh ? 22 : 23;
                OnPropertyChanged(nameof(Port));
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(ConnectionDetail));
            OnPropertyChanged(nameof(DisplayTitle));
            OnPropertyChanged(nameof(TabTitle));
            OnPropertyChanged(nameof(SupportsDirectoryManagement));
            OnPropertyChanged(nameof(DirectoryStatus));
            NotifyCommands();
        }
    }
    public string Host { get => Profile.Host; set { if (Profile.Host == value) return; Profile.Host = value; OnPropertyChanged(); OnPropertyChanged(nameof(ConnectionDetail)); OnPropertyChanged(nameof(DisplayTitle)); OnPropertyChanged(nameof(TabTitle)); } }
    public int Port { get => Profile.Port; set { if (Profile.Port == value) return; Profile.Port = value; OnPropertyChanged(); OnPropertyChanged(nameof(ConnectionDetail)); OnPropertyChanged(nameof(DisplayTitle)); OnPropertyChanged(nameof(TabTitle)); } }
    public string Username { get => Profile.Username; set { if (Profile.Username == value) return; Profile.Username = value; OnPropertyChanged(); } }
    public string Password { get => Profile.Password; set { if (Profile.Password == value) return; Profile.Password = value; OnPropertyChanged(); } }
    public bool AcceptUnknownHostKey { get => Profile.AcceptUnknownHostKey; set { if (Profile.AcceptUnknownHostKey == value) return; Profile.AcceptUnknownHostKey = value; OnPropertyChanged(); } }

    public bool IsConnected => _session?.State == RemoteSessionState.Connected;
    public bool IsWorkspaceReady => IsConnected && _workspaceInitialized;
    public string HostPlatform => _session?.HostPlatform ?? "unknown";
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) { NotifyCommands(); OnPropertyChanged(nameof(DirectoryStatus)); } } }
    public string ConnectionStatus { get => _connectionStatus; private set => SetProperty(ref _connectionStatus, value); }
    public string ConnectionLogText { get => _connectionLogText; private set => SetProperty(ref _connectionLogText, value); }
    public string ConnectionMessage { get => _connectionMessage; private set => SetProperty(ref _connectionMessage, value); }
    public bool IsDirectoryPaneExpanded { get => _isDirectoryPaneExpanded; set => SetProperty(ref _isDirectoryPaneExpanded, value); }
    public bool IsTerminalFullscreen { get => _isTerminalFullscreen; set => SetProperty(ref _isTerminalFullscreen, value); }
    public string ConnectionDetail { get => _connectionDetail; private set => SetProperty(ref _connectionDetail, value); }
    public string DisplayTitle => string.IsNullOrWhiteSpace(Profile.Name) ? $"{ProtocolLabel} · {Host}" : Profile.Name;
    public string CustomTitle
    {
        get => _customTitle;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (!SetProperty(ref _customTitle, normalized)) return;
            OnPropertyChanged(nameof(TabTitle));
        }
    }
    public string TabTitle => string.IsNullOrWhiteSpace(CustomTitle) ? DisplayTitle : CustomTitle;
    public string LastError { get => _lastError; private set => SetProperty(ref _lastError, value); }
    public string Path { get => _path; private set { if (SetProperty(ref _path, value)) GoUpCommand.NotifyCanExecuteChanged(); } }
    public string ParentPath => GetParentPath(Path);
    public bool SupportsDirectoryManagement => _session?.SupportsDirectoryManagement == true;
    public string DirectoryStatus => Protocol == RemoteProtocol.Telnet
        ? "Telnet 不支持目录管理"
        : !IsConnected
            ? "连接后可用"
            : SupportsDirectoryManagement ? string.Empty : IsBusy ? "正在初始化文件服务…" : "该 SSH 服务未启用 SFTP";
    public double? CpuPercent { get => _cpuPercent; private set => SetProperty(ref _cpuPercent, value); }
    public double? MemoryPercent { get => _memoryPercent; private set => SetProperty(ref _memoryPercent, value); }
    public double? LoadAverage { get => _loadAverage; private set => SetProperty(ref _loadAverage, value); }
    public long? ReceiveBytes { get => _receiveBytes; private set => SetProperty(ref _receiveBytes, value); }
    public long? TransmitBytes { get => _transmitBytes; private set => SetProperty(ref _transmitBytes, value); }
    public string Uptime { get => _uptime; private set => SetProperty(ref _uptime, value); }
    public string PerformanceSource { get => _performanceSource; private set => SetProperty(ref _performanceSource, value); }
    public string CpuLabel => CpuPercent is double value ? $"{value:0.0}%" : "-";
    public string MemoryLabel => MemoryPercent is double value ? $"{value:0.0}%" : "-";
    public string LoadLabel => LoadAverage is double value ? value.ToString("0.00") : "-";
    public double LoadPercent => LoadAverage is double load && _processorCount is > 0 ? Math.Clamp(load * 100 / _processorCount.Value, 0, 100) : 0;
    public string LoadPercentLabel => LoadAverage is double load && _processorCount is > 0 ? $"{load * 100 / _processorCount.Value:0.0}%" : "-";
    public string LoadTooltip => $"1 分钟平均负载：{LoadLabel} · {_processorCount?.ToString() ?? "-"} 个逻辑 CPU（百分比按 CPU 数归一化）";
    public string NetworkLabel => ReceiveBytes is long rx && TransmitBytes is long tx ? $"↓ {FormatBytes(rx)}  ↑ {FormatBytes(tx)}" : "-";

    public async Task SendTerminalInputAsync(string text)
    {
        if (_session is null || !IsConnected || string.IsNullOrEmpty(text)) return;
        try
        {
            await _session.SendAsync(text);
            Terminal.Add(DateTimeOffset.Now, false, System.Text.Encoding.UTF8.GetBytes(text));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or SocketException)
        {
            LastError = $"发送失败：{ex.Message}";
        }
    }

    public async Task ResizeTerminalAsync(int columns, int rows)
    {
        if (columns < 2 || rows < 1) return;
        _terminalColumns = columns;
        _terminalRows = rows;
        if (_session is null || Protocol != RemoteProtocol.Ssh) return;
        try { await _session.ResizeAsync(columns, rows); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or SocketException)
        {
            LastError = $"终端调整失败：{ex.Message}";
        }
    }

    public async Task OpenEntryAsync(RemoteDirectoryEntryViewModel? entry)
    {
        if (entry is null || !IsConnected || IsBusy) return;
        if (!entry.IsDirectory) { await OpenFileAsync(entry); return; }
        await NavigateRemoteAsync(entry.FullPath);
    }

    public async Task UploadAsync(string localPath)
    {
        if (_session is null || !IsConnected || !SupportsDirectoryManagement || string.IsNullOrWhiteSpace(localPath)) return;
        await RunBusyAsync(async () =>
        {
            var remotePath = CombinePath(Path, System.IO.Path.GetFileName(localPath));
            await _session.UploadFileAsync(localPath, remotePath);
            await RefreshDirectoryCoreAsync();
        });
    }

    public async Task DownloadAsync(RemoteDirectoryEntryViewModel? entry, string localPath)
    {
        if (_session is null || entry is null || entry.IsDirectory || !IsConnected || !SupportsDirectoryManagement || string.IsNullOrWhiteSpace(localPath)) return;
        await RunBusyAsync(() => _session.DownloadFileAsync(entry.FullPath, localPath));
    }

    private async Task ConnectAsync()
    {
        LastError = string.Empty;
        SetWorkspaceInitialized(false);
        ConnectionMessage = $"正在准备 {ProtocolLabel} 会话…";
        await RunBusyAsync(async () =>
        {
            await DisposeSessionAsync();
            _session = _service.CreateSession(Profile);
            ConnectionLogText = string.Empty;
            ResetPerformance();
            _session.ConnectionLogReceived += OnConnectionLogReceived;
            _session.TextReceived += OnTextReceived;
            _session.PerformanceChanged += OnPerformanceChanged;
            _session.StateChanged += OnStateChanged;
            ConnectionDetail = $"{ProtocolLabel} · {Host}:{Port}";
            Terminal.ClearCommand.Execute(null);
            await _session.ResizeAsync(_terminalColumns, _terminalRows);
            await _session.ConnectAsync();
            await ResizeTerminalAsync(_terminalColumns, _terminalRows);
            SetWorkspaceInitialized(true);
            ConnectionStatus = "已连接";
            OnPropertyChanged(nameof(HostPlatform));
            OnPropertyChanged(nameof(SupportsDirectoryManagement));
            OnPropertyChanged(nameof(DirectoryStatus));
            Path = string.IsNullOrWhiteSpace(_session.HomeDirectory) ? "/" : _session.HomeDirectory!;
            await RefreshDirectoryCoreAsync();
        });
    }

    private async Task DisconnectAsync()
    {
        await RunBusyAsync(async () =>
        {
            SetWorkspaceInitialized(false);
            await DisposeSessionAsync();
            OnPropertyChanged(nameof(IsConnected));
            ConnectionStatus = "未连接";
            DirectoryEntries.Clear();
            OnPropertyChanged(nameof(SupportsDirectoryManagement));
            OnPropertyChanged(nameof(DirectoryStatus));
            Terminal.ClearCommand.Execute(null);
            ResetPerformance();
        });
    }

    private async Task RefreshDirectoryAsync() => await RunBusyAsync(RefreshDirectoryCoreAsync);

    private async Task RefreshDirectoryCoreAsync()
    {
        if (_session is null || !IsConnected || !SupportsDirectoryManagement) return;
        var entries = await _session.ListDirectoryAsync(Path);
        RunOnUi(() =>
        {
            DirectoryEntries.Clear();
            if (Path != ParentPath) DirectoryEntries.Add(new RemoteDirectoryEntryViewModel(new RemoteDirectoryEntry("..", ParentPath, true, 0, null, string.Empty)));
            foreach (var entry in entries) DirectoryEntries.Add(new RemoteDirectoryEntryViewModel(entry));
            SelectedEntry = null;
        });
    }

    private async Task GoUpAsync()
    {
        if (Path == ParentPath) return;
        Path = ParentPath;
        await RefreshDirectoryAsync();
    }

    private async Task CreateDirectoryAsync()
    {
        if (_session is null || !IsConnected || !SupportsDirectoryManagement) return;
        var name = "new-folder-" + DateTime.Now.ToString("HHmmss");
        await RunBusyAsync(async () =>
        {
            await _session.CreateDirectoryAsync(CombinePath(Path, name));
            await RefreshDirectoryCoreAsync();
        });
    }

    private async Task DeleteEntryAsync()
    {
        if (_session is null || SelectedEntry is null || !IsConnected || !SupportsDirectoryManagement) return;
        var entry = SelectedEntry;
        await RunBusyAsync(async () =>
        {
            await _session.DeleteAsync(entry.FullPath, entry.IsDirectory);
            await RefreshDirectoryCoreAsync();
        });
    }

    private void SaveProfile()
    {
        if (!Profiles.Contains(Profile)) Profiles.Add(Profile);
        OnPropertyChanged(nameof(SelectedProfile));
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await action(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex.Message;
            if (!IsConnected) ConnectionLogText += $"[{DateTime.Now:HH:mm:ss.fff}] {ex.Message}\n";
            ConnectionStatus = _session?.State == RemoteSessionState.Faulted ? "连接失败" : ConnectionStatus;
        }
        finally { IsBusy = false; NotifyCommands(); }
    }

    private void SetWorkspaceInitialized(bool initialized)
    {
        if (!initialized) IsTerminalFullscreen = false;
        if (_workspaceInitialized == initialized) return;
        _workspaceInitialized = initialized;
        OnPropertyChanged(nameof(IsWorkspaceReady));
    }

    private void OnConnectionLogReceived(object? sender, string text)
    {
        void Append() { if (ReferenceEquals(sender, _session)) ConnectionLogText += text + "\n"; }
        // Queue UI-thread messages too, preserving the library's cross-thread order.
        if (Application.Current?.Dispatcher is { } dispatcher) dispatcher.BeginInvoke((Action)Append);
        else Append();
    }
    private void OnTextReceived(object? sender, string text) => RunOnUi(() =>
    {
        if (ReferenceEquals(sender, _session)) Terminal.Add(DateTimeOffset.Now, true, System.Text.Encoding.UTF8.GetBytes(text));
    });
    private void OnPerformanceChanged(object? sender, RemotePerformanceSnapshot snapshot) => RunOnUi(() =>
    {
        if (!ReferenceEquals(sender, _session)) return;
        _processorCount = snapshot.ProcessorCount;
        CpuPercent = snapshot.CpuPercent; MemoryPercent = snapshot.MemoryPercent; LoadAverage = snapshot.LoadAverage; ReceiveBytes = snapshot.ReceiveBytes; TransmitBytes = snapshot.TransmitBytes; Uptime = snapshot.Uptime ?? "-"; PerformanceSource = snapshot.Source ?? "远程主机";
        OnPropertyChanged(nameof(CpuLabel)); OnPropertyChanged(nameof(MemoryLabel)); OnPropertyChanged(nameof(LoadLabel)); OnPropertyChanged(nameof(NetworkLabel));
        OnPropertyChanged(nameof(LoadPercent)); OnPropertyChanged(nameof(LoadPercentLabel)); OnPropertyChanged(nameof(LoadTooltip));
    });
    private void OnStateChanged(object? sender, RemoteSessionState state) => RunOnUi(() =>
    {
        if (!ReferenceEquals(sender, _session)) return;
        if (state == RemoteSessionState.Connected) NetworkStatus.Track(new(Host, Port), $"{ProtocolLabel} · {Host}:{Port}");
        else NetworkStatus.Stop(state == RemoteSessionState.Connecting ? "等待连接" : "探测已停止");
        SetWorkspaceInitialized(state == RemoteSessionState.Connected);
        if (state == RemoteSessionState.Connected) PerformanceSource = Protocol == RemoteProtocol.Ssh ? "正在采样…" : "Telnet 不支持性能采集";
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsWorkspaceReady));
        OnPropertyChanged(nameof(HostPlatform));
        OnPropertyChanged(nameof(SupportsDirectoryManagement));
        OnPropertyChanged(nameof(DirectoryStatus));
        ConnectionStatus = state switch { RemoteSessionState.Connecting => "连接中", RemoteSessionState.Connected => "已连接", RemoteSessionState.Disconnecting => "断开中", RemoteSessionState.Faulted => "连接失败", _ => "未连接" };
        ConnectionMessage = state switch
        {
            RemoteSessionState.Connecting => $"正在连接 {ConnectionDetail}…\n正在进行身份验证，请稍候",
            RemoteSessionState.Connected => "SSH 握手完成，正在初始化终端…",
            RemoteSessionState.Faulted => string.IsNullOrWhiteSpace(LastError) ? "连接未能建立" : LastError,
            RemoteSessionState.Disconnecting => "正在断开连接…",
            _ => "等待开始连接…"
        };
        NotifyCommands();
    });

    private async Task DisposeSessionAsync()
    {
        NetworkStatus.Stop("探测已停止");
        if (_session is null) return;
        _session.ConnectionLogReceived -= OnConnectionLogReceived;
        _session.TextReceived -= OnTextReceived;
        _session.PerformanceChanged -= OnPerformanceChanged;
        _session.StateChanged -= OnStateChanged;
        await _session.DisposeAsync();
        _session = null;
    }

    private void ResetPerformance()
    {
        _processorCount = null;
        CpuPercent = null; MemoryPercent = null; LoadAverage = null; ReceiveBytes = null; TransmitBytes = null; Uptime = "-"; PerformanceSource = "等待连接";
        OnPropertyChanged(nameof(CpuLabel)); OnPropertyChanged(nameof(MemoryLabel)); OnPropertyChanged(nameof(LoadLabel)); OnPropertyChanged(nameof(NetworkLabel));
        OnPropertyChanged(nameof(LoadPercent)); OnPropertyChanged(nameof(LoadPercentLabel)); OnPropertyChanged(nameof(LoadTooltip));
    }

    private void NotifyCommands()
    {
        ConnectCommand.NotifyCanExecuteChanged(); DisconnectCommand.NotifyCanExecuteChanged(); RefreshDirectoryCommand.NotifyCanExecuteChanged(); GoUpCommand.NotifyCanExecuteChanged(); CreateDirectoryCommand.NotifyCanExecuteChanged(); DeleteEntryCommand.NotifyCanExecuteChanged();
    }

    private string ProtocolLabel => Protocol == RemoteProtocol.Ssh ? "SSH" : "Telnet";
    private static string CombinePath(string parent, string child) => parent == "/" ? "/" + child : parent.TrimEnd('/') + "/" + child;
    private static string GetParentPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/") return "/";
        var trimmed = path.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash <= 0 ? "/" : trimmed[..slash];
    }
    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.0} MB" : bytes >= 1024 ? $"{bytes / 1024d:0.0} KB" : $"{bytes} B";
    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action(); else dispatcher.BeginInvoke(action);
    }

    public async ValueTask DisposeAsync()
    {
        NetworkStatus.Dispose();
        _fileOperations.Cancel();
        await _previewGate.WaitAsync();
        try
        {
            foreach (var document in _documents.ToArray())
            {
                if (document.SaveCommand.ExecutionTask is { } saving) await saving;
                document.Dispose();
            }
            _documents.Clear();
            await DisposeSessionAsync();
        }
        finally { _previewGate.Release(); }
    }
}
