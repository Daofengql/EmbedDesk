using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using EmbedDesk.Core.Models;
using EmbedDesk.Infrastructure.Connections;
using EmbedDesk.Infrastructure.Serial;

namespace EmbedDesk.App.ViewModels;

public sealed class ConnectionCenterViewModel : ObservableObject, IAsyncDisposable
{
    private readonly AppSettingsStore _settings;
    private readonly string _credentialPrefix;
    private string _password = string.Empty, _error = string.Empty;
    private string _relayName = string.Empty;
    private string _selectionStatus = string.Empty;
    private CancellationTokenSource? _startLife;
    private Task _startTask = Task.CompletedTask;
    private bool _disposed;
    private bool _busy;
    private bool _connecting;
    private CancellationTokenSource? _connectLife;
    private Task _connectTask = Task.CompletedTask;
    private Task _operationTask = Task.CompletedTask;
    private readonly System.Windows.Threading.DispatcherTimer _statistics;
    private readonly Dictionary<string, VirtualSerialBridge> _virtualMappings = new();
    private readonly Func<VirtualSerialPair, IVirtualSerialEndpoint> _openVirtualEndpoint;
    internal ConnectionCenterViewModel(DeviceNode node, AppSettingsStore settings, Func<VirtualSerialPair, IVirtualSerialEndpoint>? openVirtualEndpoint = null)
    {
        Node = node; _settings = settings;
        _openVirtualEndpoint = openVirtualEndpoint ?? Com0ComEndpoint.Open;
        _statistics = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; _statistics.Tick += (_, _) => RefreshMappingStates(); _statistics.Start();
        _credentialPrefix = "EmbedDesk/device/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings.FilePath))).Substring(0, 24);
        RebuildLocalServiceViews(settings.Snapshot().SharedServices);
        foreach (var id in settings.Snapshot().RecentDeviceIds) RecentIds.Add(id);
        foreach (var rule in settings.Snapshot().PortMappingRules) MappingRules.Add(new(rule));
        node.Changed += OnChanged;
        node.OperationFailed += OnFailed;
        Peers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsRemoteMappingMode));
        try { node.ConfigureServices(LocalServices); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { Error = ex.Message; }
    }
    public DeviceNode Node { get; }
    public ObservableCollection<SharedDeviceService> LocalServices { get; } = [];
    public ObservableCollection<SharedDeviceService> LocalSerialServices { get; } = [];
    public ObservableCollection<SharedDeviceService> LocalNetworkServices { get; } = [];
    public ObservableCollection<SerialPortRowViewModel> LocalSerialDevices { get; } = [];
    public ObservableCollection<DevicePeerInfo> Peers { get; } = [];
    public ObservableCollection<DeviceApproval> Approvals { get; } = [];
    public ObservableCollection<DeviceRemoteService> RemoteServices { get; } = [];
    public ObservableCollection<DevicePortMapping> Mappings { get; } = [];
    public ObservableCollection<PortMappingRuleViewModel> MappingRules { get; } = [];
    public PortMappingRuleViewModel? SelectedRule { get; set; }
    public ObservableCollection<string> RecentIds { get; } = [];
    public string DeviceId => Node.DeviceId.Length == 0 ? "等待登记" : Node.DeviceId;
    public string Status => _selectionStatus.Length > 0 ? _selectionStatus : Node.Status;
    public bool IsOnline => Node.IsOnline;
    public bool IsRemoteMappingMode => Peers.Any(peer => !peer.IsOwner && string.Equals(peer.State, "已连接", StringComparison.Ordinal));
    public bool IsBusy { get => _busy; private set => SetProperty(ref _busy, value); }
    public bool IsConnecting { get => _connecting; private set => SetProperty(ref _connecting, value); }
    public string Password { get => _password; private set { if (SetProperty(ref _password, value)) OnPropertyChanged(nameof(PasswordDisplay)); } }
    public string PasswordDisplay => Password.Length == 0 ? "—" : Password;
    public string Error { get => _error; private set => SetProperty(ref _error, value); }
    public string RelayName { get => _relayName; private set => SetProperty(ref _relayName, value); }
    public string TargetId { get; set; } = string.Empty;
    public SharedDeviceService? SelectedLocalSerialService { get; set; }
    public SharedDeviceService? SelectedLocalNetworkService { get; set; }
    private DeviceRemoteService? _selectedRemoteService;
    public DeviceRemoteService? SelectedRemoteService { get => _selectedRemoteService; set { if (SetProperty(ref _selectedRemoteService, value)) OnPropertyChanged(nameof(IsSerialServiceSelected)); } }
    public bool IsSerialServiceSelected => SelectedRemoteService?.Service.Kind == SharedServiceKind.Serial;
    public DevicePeerInfo? SelectedPeer { get; set; }
    public DevicePortMapping? SelectedMapping { get; set; }
    internal ConnectionPreferences Preferences => _settings.Snapshot().ConnectionPreferences;
    internal string ReadSecret(string name) => WindowsCredentialStore.Read(_credentialPrefix + "/" + name)?.Secret ?? string.Empty;
    internal void SaveSecret(string name, string value)
    {
        if (value.Length == 0) WindowsCredentialStore.Delete(_credentialPrefix + "/" + name);
        else WindowsCredentialStore.Save(_credentialPrefix + "/" + name, value, string.Empty, "EmbedDesk 连接中心");
    }
    public async Task InitializeAsync()
    {
        if (Preferences.RelayServers.Count > 0 || Preferences.ServerUrl.Length > 0) await StartAsync();
    }
    public Task StartAsync()
    {
        if (_disposed || IsBusy) return Task.CompletedTask;
        _startLife = new();
        return _startTask = StartCoreAsync(_startLife);
    }
    private async Task StartCoreAsync(CancellationTokenSource lifetime)
    {
        try { await RunAsync(async () =>
        {
        var preferences = Preferences;
        CancelConnect();
        _selectionStatus = "正在选择中继…"; OnPropertyChanged(nameof(Status));
        var transport = new DeviceNodeOptions
        {
            ProxyMode = preferences.ProxyMode, ProxyUrl = preferences.ProxyUrl, ProxyUser = preferences.ProxyUser,
            ProxyPassword = ReadSecret("proxy"), TrustedRootFile = preferences.TrustedRootFile
        };
        var serverProfile = await SelectRelayServerAsync(preferences, transport, lifetime.Token);
        lifetime.Token.ThrowIfCancellationRequested();
        if (serverProfile is null) throw new ArgumentException("请先在设置的“中继服务器”页面添加可用地址。");
        var server = new Uri(serverProfile.Url);
        RelayName = serverProfile.Name;
        var identityName = "identity-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server.AbsoluteUri.TrimEnd('/')))).Substring(0, 24);
        var credential = ReadSecret(identityName);
        if (credential.Length == 0) { credential = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); SaveSecret(identityName, credential); }
        Password = preferences.ManualApprovalOnly ? "仅人工授权" : preferences.StaticPassword ? ReadSecret("password") : Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
        if (!preferences.ManualApprovalOnly && Password.Length < 8) throw new ArgumentException("请在设置中配置至少 8 个字符的静态密码。");
        await Node.StartAsync(transport with
        {
            ServerUrl = serverProfile.Url, Credential = credential, AdmissionKey = ReadSecret("admission"), Password = preferences.ManualApprovalOnly ? string.Empty : Password,
            ManualApprovalOnly = preferences.ManualApprovalOnly, PreferDirect = preferences.PreferDirect, TimeoutSeconds = preferences.TimeoutSeconds, RetrySeconds = preferences.RetrySeconds,
        }, lifetime.Token);
        }); }
        finally
        {
            _selectionStatus = string.Empty; OnPropertyChanged(nameof(Status));
            _startLife = null; lifetime.Dispose();
        }
    }
    internal static async Task<RelayServerProfile?> SelectRelayServerAsync(ConnectionPreferences preferences, DeviceNodeOptions transport, CancellationToken token)
    {
        var candidates = preferences.RelayServers.Where(profile => Uri.TryCreate(profile.Url, UriKind.Absolute, out _)).ToArray();
        if (candidates.Length == 0 && Uri.TryCreate(preferences.ServerUrl, UriKind.Absolute, out var legacy))
            candidates = [new RelayServerProfile { Name = "自定义中继", Url = legacy.ToString().TrimEnd('/') }];
        if (candidates.Length == 0) return null;
        var defaultServer = candidates.FirstOrDefault(profile => string.Equals(profile.Id, preferences.DefaultRelayServerId, StringComparison.OrdinalIgnoreCase)) ?? candidates[0];
        if ((await RelayHealthProbe.CheckAsync(transport with { ServerUrl = defaultServer.Url }, token)).Available) return defaultServer;
        var probes = await Task.WhenAll(candidates.Where(profile => profile.Id != defaultServer.Id).Select(async profile =>
            (Profile: profile, Health: await RelayHealthProbe.CheckAsync(transport with { ServerUrl = profile.Url }, token))));
        return probes.Where(result => result.Health.Available)
            .OrderByDescending(result => result.Profile.BuiltIn)
            .ThenBy(result => result.Health.Milliseconds)
            .Select(result => result.Profile)
            .FirstOrDefault() ?? defaultServer;
    }

    public async Task StopAsync()
    {
        CancelConnect(); _startLife?.Cancel(); await _startTask;
        await RunAsync(async () => { await StopVirtualMappingsAsync(); await Node.StopAsync(); });
    }
    public void CancelConnect() => _connectLife?.Cancel();
    public Task ConnectAsync(string password)
    {
        if (IsConnecting || IsBusy) return Task.CompletedTask;
        _connectLife = new(); IsConnecting = true; Error = string.Empty;
        return _connectTask = ConnectCoreAsync(TargetId.Trim().ToUpperInvariant(), password, _connectLife);
    }
    private async Task ConnectCoreAsync(string id, string password, CancellationTokenSource lifetime)
    {
        try
        {
            await Node.ConnectPeerAsync(id, password, lifetime.Token);
            _settings.Update(s => { s.RecentDeviceIds.Remove(id); s.RecentDeviceIds.Insert(0, id); s.RecentDeviceIds = s.RecentDeviceIds.Take(20).ToList(); });
            RecentIds.Clear(); foreach (var recent in _settings.Snapshot().RecentDeviceIds) RecentIds.Add(recent);
        }
        catch (OperationCanceledException) { Error = "连接请求已取消。"; }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or UnauthorizedAccessException or TimeoutException or System.Net.WebSockets.WebSocketException or CryptographicException) { Error = ex.Message; }
        finally { IsConnecting = false; _connectLife = null; lifetime.Dispose(); }
    }
    public Task ApproveAsync(DeviceApproval approval, bool allow) => RunAsync(() => Node.ApproveAsync(approval.SessionId, allow));
    public Task DisconnectAsync(DevicePeerInfo peer) => RunAsync(() => Node.DisconnectPeerAsync(peer.SessionId));
    public Task MapAsync(int port) => RunAsync(async () =>
    {
        var service = SelectedRemoteService ?? throw new ArgumentException("请选择对端发布的 TCP 或 UDP 服务。");
        if (service.Service.Kind is not (SharedServiceKind.Tcp or SharedServiceKind.Udp) || port is < 1 or > 65535) throw new ArgumentException("请选择 TCP 或 UDP 服务并输入有效端口。");
        var rule = MappingRules.FirstOrDefault(r => r.Rule.DeviceId == service.DeviceId && r.Rule.ServiceId == service.Service.Id && r.Rule.LocalPort == port && r.Rule.Kind == service.Service.Kind);
        if (rule is null)
        {
            if (MappingRules.Count >= 128) throw new InvalidOperationException("映射规则已达到 128 条上限。");
            rule = new(new(Guid.NewGuid().ToString("N"), service.DeviceId, service.Service.Id, service.Service.Name, port, service.Service.Kind)); MappingRules.Add(rule); SaveRules();
        }
        await EnableRuleCoreAsync(rule);
    });
    public Task MapVirtualSerialAsync(VirtualSerialPair pair) => RunAsync(async () =>
    {
        var service = SelectedRemoteService ?? throw new ArgumentException("请选择远端串口。");
        if (service.Service.Kind != SharedServiceKind.Serial) throw new ArgumentException("请选择远端串口。");
        var rule = MappingRules.FirstOrDefault(r => r.Rule.DeviceId == service.DeviceId && r.Rule.ServiceId == service.Service.Id
            && r.Rule.Kind == SharedServiceKind.Serial && string.Equals(r.Rule.VirtualPort, pair.PublicPort, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.Rule.BridgePort, pair.BridgePort, StringComparison.OrdinalIgnoreCase));
        if (rule is null)
        {
            if (MappingRules.Count >= 128) throw new InvalidOperationException("映射规则已达到 128 条上限。");
            rule = new(new(Guid.NewGuid().ToString("N"), service.DeviceId, service.Service.Id, service.Service.Name, 0, SharedServiceKind.Serial, pair.PublicPort, pair.BridgePort));
            MappingRules.Add(rule); SaveRules();
        }
        await EnableRuleCoreAsync(rule);
    });
    public Task EnableRuleAsync(PortMappingRuleViewModel rule) => RunAsync(() => EnableRuleCoreAsync(rule));
    private async Task EnableRuleCoreAsync(PortMappingRuleViewModel rule)
    {
        if (rule.MappingId is not null) return;
        var service = Node.RemoteServices.FirstOrDefault(s => s.DeviceId == rule.Rule.DeviceId && s.Service.Id == rule.Rule.ServiceId && s.Service.Kind == rule.Rule.Kind) ?? throw new IOException("请先连接该设备；对端需要继续发布规则对应协议的服务。");
        if (rule.Rule.Kind == SharedServiceKind.Serial)
        {
            try
            {
                if (_virtualMappings.TryGetValue(rule.Rule.Id, out var old)) { await old.DisposeAsync(); _virtualMappings.Remove(rule.Rule.Id); }
                if (_virtualMappings.Count(p => p.Value.IsRunning) >= 16) throw new InvalidOperationException("同时启用的虚拟串口映射已达 16 条上限。");
                if (MappingRules.Any(r => r != rule && r.MappingId is not null && r.Rule.Kind == SharedServiceKind.Serial
                    && (string.Equals(r.Rule.VirtualPort, rule.Rule.VirtualPort, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(r.Rule.BridgePort, rule.Rule.BridgePort, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(r.Rule.VirtualPort, rule.Rule.BridgePort, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(r.Rule.BridgePort, rule.Rule.VirtualPort, StringComparison.OrdinalIgnoreCase)
                        || r.Rule.DeviceId == rule.Rule.DeviceId && r.Rule.ServiceId == rule.Rule.ServiceId)))
                    throw new InvalidOperationException("此串口对或远端串口已用于其他映射。");
                if (Node.IsShared(rule.Rule.VirtualPort) || Node.IsShared(rule.Rule.BridgePort)) throw new InvalidOperationException("此串口对已向外共享，请先取消共享。");
                var pair = new VirtualSerialPair(rule.Rule.VirtualPort, rule.Rule.BridgePort, string.Empty, string.Empty);
                var endpoint = await Task.Run(() => _openVirtualEndpoint(pair));
                if (_disposed) { await endpoint.DisposeAsync(); throw new OperationCanceledException(); }
                _virtualMappings[rule.Rule.Id] = new(endpoint, settings => Node.CreateSerialChannel(service.PortName, settings),
                    () => Node.RemoteServices.Any(s => s.PortName == service.PortName));
                rule.MappingId = rule.Rule.Id; RefreshMappingStates();
            }
            catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException)
            { rule.Status = "失败：" + ex.Message; throw; }
            return;
        }
        try { rule.MappingId = (await Node.MapPortAsync(service.SessionId, service.Service.Id, rule.Rule.LocalPort)).Id; Refresh(); }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or ArgumentException) { rule.Status = "失败：" + ex.Message; throw; }
    }
    private async Task StopRuleCoreAsync(PortMappingRuleViewModel rule)
    {
        if (_virtualMappings.Remove(rule.Rule.Id, out var bridge)) await bridge.DisposeAsync();
        else if (rule.MappingId is { } id) await Node.UnmapPortAsync(id);
        rule.MappingId = null; rule.Status = "未启用";
    }
    private async Task StopVirtualMappingsAsync()
    { foreach (var rule in MappingRules.Where(r => r.Rule.Kind == SharedServiceKind.Serial).ToArray()) await StopRuleCoreAsync(rule); }
    public Task DisableRuleAsync(PortMappingRuleViewModel rule) => RunAsync(() => StopRuleCoreAsync(rule));
    public Task DeleteRuleAsync(PortMappingRuleViewModel rule) => RunAsync(async () => { await StopRuleCoreAsync(rule); MappingRules.Remove(rule); SaveRules(); });
    private void SaveRules() => _settings.Update(s => s.PortMappingRules = MappingRules.Select(r => r.Rule).ToList());
    public Task UnmapAsync(DevicePortMapping mapping) => RunAsync(() => Node.UnmapPortAsync(mapping.Id));
    public void SaveService(SharedDeviceService service)
    {
        service = WithSerialDescription(service);
        var next = LocalServices.Where(s => s.Id != service.Id).Append(service).ToArray();
        Node.ConfigureServices(next); RebuildLocalServiceViews(next);
        _settings.Update(s => s.SharedServices = next.ToList());
    }
    public void RemoveService(SharedDeviceService service)
    {
        var next = LocalServices.Where(s => s.Id != service.Id).ToArray();
        Node.ConfigureServices(next); RebuildLocalServiceViews(next); _settings.Update(s => s.SharedServices = next.ToList());
    }
    private void RebuildLocalServiceViews(IEnumerable<SharedDeviceService> services)
    {
        var items = services.Select(WithSerialDescription).ToArray();
        var selectedSerial = SelectedLocalSerialService?.Id;
        var selectedNetwork = SelectedLocalNetworkService?.Id;
        LocalServices.Clear(); LocalSerialServices.Clear(); LocalNetworkServices.Clear();
        foreach (var service in items)
        {
            LocalServices.Add(service);
            if (service.Kind == SharedServiceKind.Serial) LocalSerialServices.Add(service);
            else LocalNetworkServices.Add(service);
        }
        SelectedLocalSerialService = LocalSerialServices.FirstOrDefault(s => s.Id == selectedSerial) ?? LocalSerialServices.FirstOrDefault();
        SelectedLocalNetworkService = LocalNetworkServices.FirstOrDefault(s => s.Id == selectedNetwork) ?? LocalNetworkServices.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedLocalSerialService)); OnPropertyChanged(nameof(SelectedLocalNetworkService));
    }
    internal void UpdateLocalSerialDevices(IEnumerable<SerialPortDescriptor> descriptors)
    {
        LocalSerialDevices.Clear();
        foreach (var descriptor in descriptors) LocalSerialDevices.Add(new(descriptor));
        RebuildLocalServiceViews(LocalServices.ToArray());
    }
    private SharedDeviceService WithSerialDescription(SharedDeviceService service)
    {
        if (service.Kind != SharedServiceKind.Serial) return service;
        var device = LocalSerialDevices.FirstOrDefault(p => string.Equals(p.PortName, service.PortName, StringComparison.OrdinalIgnoreCase));
        return service with { Description = device is null ? "设备当前不在线" : $"{device.Description} · {device.Manufacturer}" };
    }
    private Task RunAsync(Func<Task> operation)
    {
        if (_disposed || IsBusy) return Task.CompletedTask;
        IsBusy = true; Error = string.Empty;
        return _operationTask = RunOperationAsync(operation);
    }
    private async Task RunOperationAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException or TimeoutException or Win32Exception or System.Net.Sockets.SocketException or CryptographicException or OperationCanceledException or System.Net.WebSockets.WebSocketException)
        { Error = ex.Message; }
        finally { IsBusy = false; }
    }
    private void OnFailed(object? sender, string error) => Dispatch(() => Error = error);
    private void OnChanged(object? sender, EventArgs e) => Dispatch(Refresh);
    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action(); else dispatcher.BeginInvoke(action);
    }
    private void Refresh()
    {
        OnPropertyChanged(nameof(DeviceId)); OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(IsOnline)); OnPropertyChanged(nameof(IsRemoteMappingMode));
        Replace(Peers, Node.Peers); Replace(Approvals, Node.PendingApprovals);
        var remote = SelectedRemoteService?.PortName;
        Replace(RemoteServices, Node.RemoteServices);
        SelectedRemoteService = RemoteServices.FirstOrDefault(s => s.PortName == remote);
        Replace(Mappings, Node.Mappings);
        RefreshMappingStates();
    }
    private void RefreshMappingStates()
    {
        foreach (var rule in MappingRules)
        {
            if (rule.Rule.Kind == SharedServiceKind.Serial)
            {
                if (_virtualMappings.TryGetValue(rule.Rule.Id, out var bridge))
                {
                    rule.MappingId = bridge.IsRunning ? rule.Rule.Id : null;
                    var currentTime = Environment.TickCount64; var totalBytes = bridge.SentBytes + bridge.ReceivedBytes;
                    var speed = currentTime > rule.PreviousTime ? Math.Max(0, totalBytes - rule.PreviousBytes) / 1024d * 1000 / (currentTime - rule.PreviousTime) : 0;
                    rule.PreviousTime = currentTime; rule.PreviousBytes = totalBytes;
                    rule.Status = bridge.Status + (bridge.IsRunning ? $" · {Node.Peers.FirstOrDefault(p => p.DeviceId == rule.Rule.DeviceId)?.Path} · {speed:F1} KiB/s" : string.Empty)
                        + $" · TX {bridge.SentBytes / 1024:N0} / RX {bridge.ReceivedBytes / 1024:N0} KiB";
                }
                continue;
            }
            if (rule.MappingId is not { } id) continue;
            var mapping = Node.Mappings.FirstOrDefault(m => m.Id == id);
            if (mapping is null) { rule.MappingId = null; rule.Status = "未启用"; }
            else
            {
                var now = Environment.TickCount64; var total = mapping.SentBytes + mapping.ReceivedBytes;
                var rate = now > rule.PreviousTime ? Math.Max(0, total - rule.PreviousBytes) / 1024d * 1000 / (now - rule.PreviousTime) : 0;
                rule.PreviousTime = now; rule.PreviousBytes = total;
                rule.Status = mapping.Error.Length > 0 ? "转发失败：" + mapping.Error : $"监听中 · {Node.Peers.FirstOrDefault(p => p.SessionId == mapping.SessionId)?.Path} · {rate:F1} KiB/s · TX {mapping.SentBytes / 1024:N0} / RX {mapping.ReceivedBytes / 1024:N0} KiB"
                    + (mapping.DroppedDatagrams > 0 ? $" · UDP 丢弃 {mapping.DroppedDatagrams:N0} 报文" : string.Empty);
            }
        }
    }
    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> next)
    {
        var items = next.ToArray(); if (collection.SequenceEqual(items)) return;
        collection.Clear(); foreach (var item in items) collection.Add(item);
    }
    public async ValueTask DisposeAsync()
    {
        _disposed = true; _statistics.Stop(); Node.Changed -= OnChanged; Node.OperationFailed -= OnFailed;
        CancelConnect(); _startLife?.Cancel(); await _startTask; await _operationTask; await StopVirtualMappingsAsync(); await Node.DisposeAsync(); await _connectTask;
    }
}
