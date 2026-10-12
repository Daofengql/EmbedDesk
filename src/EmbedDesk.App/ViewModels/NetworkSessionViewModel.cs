using System.Collections.ObjectModel;
using System.Text;
using System.Net;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbedDesk.Core.Abstractions;
using EmbedDesk.Core.Models;

namespace EmbedDesk.App.ViewModels;

public sealed class NetworkSessionViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ITransportSession _session;
    private readonly Action<string, string, TransportEventKind> _log;
    private readonly HashSet<string> _tcpPeers = new(StringComparer.Ordinal);
    private IPEndPoint? _probePeer;
    private string _state = "未启动", _lastMessage = "就绪", _httpStartLine = string.Empty, _httpHeadersText = string.Empty;
    private string _sendText = string.Empty, _interval = "1000", _jobStatus = "未运行";
    private NetworkPeerTarget _selectedPeerTarget = NetworkPeerTarget.Broadcast;
    private bool _sendAsHex, _appendCrLf, _hexDisplay, _showTime = true, _showSent = true, _autoScroll = true, _queueRepeat, _isSending, _disposed;
    public NetworkSessionViewModel(ITransportSession session, Action<string, string, TransportEventKind> log)
    {
        _session = session; _log = log;
        _session.MessageReceived += OnMessageReceived;
        StartStopCommand = new AsyncRelayCommand(ToggleAsync);
        SendCommand = new AsyncRelayCommand(SendCurrentAsync, () => CanSend && SendText.Length > 0);
        RepeatSendCommand = new AsyncRelayCommand(ct => RunJobAsync(false, ct), () => CanSend && !IsSending && SendText.Length > 0);
        RunQueueCommand = new AsyncRelayCommand(ct => RunJobAsync(true, ct), () => CanSend && !IsSending);
        StopSendingCommand = new RelayCommand(StopSending);
        AddQueueItemCommand = new RelayCommand(() => SendQueue.Add(new SerialSendQueueItem { Payload = SendText }));
        RemoveQueueItemCommand = new RelayCommand(() => { if (SelectedQueueItem is not null) SendQueue.Remove(SelectedQueueItem); });
    }
    public string Id => _session.Id;
    public string Protocol => _session.Options.Protocol.ToString().ToUpperInvariant();
    public string Mode => _session.Options.Mode == TransportMode.Client ? "客户端" : "服务端";
    public string Endpoint => _session.Options.Mode == TransportMode.Client ? $"{_session.Options.RemoteHost}:{_session.Options.RemotePort}" : LocalEndpoint;
    public string Detail => _session.Options.Protocol == TransportProtocol.Mqtt
        ? $"{Protocol} · {Endpoint} · {_session.Options.MqttTopic}"
        : $"{Protocol} · {Mode} · {Endpoint}";
    public string LocalEndpoint => _session.BoundLocalEndpoint ?? $"{_session.Options.LocalHost}:{_session.Options.LocalPort}";
    public string SendTargetHint => _session.Options.Mode == TransportMode.Client
        ? _session.Options.Protocol == TransportProtocol.Mqtt ? $"发布到 MQTT 主题 {_session.Options.MqttTopic}" : $"发送到 {Endpoint}"
        : SelectedPeerTarget.IsBroadcast
            ? _session.Options.Protocol == TransportProtocol.Tcp ? "发送到全部已连接的 TCP 客户端" : $"发送到最近的 UDP 对端（尚未收到数据时使用 {_session.Options.RemoteHost}:{_session.Options.RemotePort}）"
            : $"发送到客户端 {SelectedPeerTarget.DisplayName}";
    public bool IsServer => _session.Options.Mode == TransportMode.Server;
    public bool SupportsPeerTargeting => IsServer;
    public ObservableCollection<NetworkPeerTarget> PeerTargets { get; } = [NetworkPeerTarget.Broadcast];
    public NetworkPeerTarget SelectedPeerTarget
    {
        get => _selectedPeerTarget;
        set
        {
            if (value is null || !SetProperty(ref _selectedPeerTarget, value)) return;
            OnPropertyChanged(nameof(SendTargetHint));
        }
    }
    public string State { get => _state; private set => SetProperty(ref _state, value); }
    public string LastMessage { get => _lastMessage; private set => SetProperty(ref _lastMessage, value); }
    public string HttpStartLine { get => _httpStartLine; private set => SetProperty(ref _httpStartLine, value); }
    public string HttpHeadersText { get => _httpHeadersText; private set => SetProperty(ref _httpHeadersText, value); }
    public bool TreatAsHttp => _session.Options.TreatAsHttp;
    public bool AutoReconnect => _session.Options.AutoReconnect;
    public TerminalBufferViewModel Terminal { get; } = new();
    public NetworkStatusViewModel NetworkStatus { get; } = new();
    public bool CanSend => !_disposed && (_session.State == TransportSessionState.Connected || _session.Options.Protocol == TransportProtocol.Udp && _session.State == TransportSessionState.Listening);
    public bool CanStart => _session.State is TransportSessionState.Created or TransportSessionState.Stopped or TransportSessionState.Faulted;
    public string StartStopLabel => CanStart ? "启动" : "停止";
    public string StartStopGlyph => CanStart ? "\uE768" : "\uE71A";
    public string SendText { get => _sendText; set { if (SetProperty(ref _sendText, value)) NotifyCommands(); } }
    public bool SendAsHex { get => _sendAsHex; set => SetProperty(ref _sendAsHex, value); }
    public bool AppendCrLf { get => _appendCrLf; set => SetProperty(ref _appendCrLf, value); }
    public bool HexDisplay { get => _hexDisplay; set => SetProperty(ref _hexDisplay, value); }
    public bool ShowTime { get => _showTime; set => SetProperty(ref _showTime, value); }
    public bool ShowSent { get => _showSent; set => SetProperty(ref _showSent, value); }
    public bool AutoScroll { get => _autoScroll; set => SetProperty(ref _autoScroll, value); }
    public string IntervalMs { get => _interval; set => SetProperty(ref _interval, value); }
    public bool QueueRepeat { get => _queueRepeat; set => SetProperty(ref _queueRepeat, value); }
    public bool IsSending { get => _isSending; private set { if (SetProperty(ref _isSending, value)) NotifyCommands(); } }
    public string JobStatus { get => _jobStatus; private set => SetProperty(ref _jobStatus, value); }
    public ObservableCollection<SerialSendQueueItem> SendQueue { get; } = [];
    public SerialSendQueueItem? SelectedQueueItem { get; set; }
    public IAsyncRelayCommand StartStopCommand { get; }
    public IAsyncRelayCommand SendCommand { get; }
    public IAsyncRelayCommand RepeatSendCommand { get; }
    public IAsyncRelayCommand RunQueueCommand { get; }
    public IRelayCommand StopSendingCommand { get; }
    public IRelayCommand AddQueueItemCommand { get; }
    public IRelayCommand RemoveQueueItemCommand { get; }
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _session.StartAsync(cancellationToken);
    }
    public async Task StopAsync(CancellationToken cancellationToken = default) { StopSending(); NetworkStatus.Stop("探测已停止"); await _session.StopAsync(cancellationToken); }
    public Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) => _session.SendAsync(payload, cancellationToken);
    private async Task ToggleAsync()
    {
        try { if (CanStart) { await _session.StopAsync(); await StartAsync(); } else await StopAsync(); }
        catch (Exception ex) { ReportError(ex); }
    }
    private async Task SendCurrentAsync(CancellationToken ct)
    {
        try { await SendPayloadAsync(SendPayload.Encode(SendText, SendAsHex, AppendCrLf), ct); }
        catch (Exception ex) { ReportError(ex); }
    }
    private async Task RunJobAsync(bool queue, CancellationToken ct)
    {
        if (IsSending || _disposed) return;
        IsSending = true;
        try
        {
            if (!int.TryParse(IntervalMs, out var interval) || interval is < 50 or > 86400000) throw new ArgumentException("发送间隔应为 50–86400000 毫秒。");
            var items = (queue ? SendQueue.Where(item => item.Enabled).Select(item => item.Payload) : new[] { SendText }).ToArray();
            if (items.Length == 0 || items.Any(string.IsNullOrEmpty)) throw new ArgumentException("请填写非空发送内容，或启用至少一个队列项目。");
            var payloads = items.Select(text => SendPayload.Encode(text, SendAsHex, AppendCrLf)).ToArray();
            var count = 0;
            do
            {
                foreach (var payload in payloads)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!CanSend) throw new InvalidOperationException("连接已断开，发送任务已停止。");
                    await SendPayloadAsync(payload, ct);
                    JobStatus = $"已发送 {++count} 次";
                    await Task.Delay(interval, ct);
                }
            } while (!queue || QueueRepeat);
            JobStatus = $"队列完成，共 {count} 次";
        }
        catch (OperationCanceledException) { JobStatus = "已停止"; }
        catch (Exception ex) { JobStatus = ex.Message; ReportError(ex); }
        finally { IsSending = false; }
    }
    private void StopSending() { RepeatSendCommand.Cancel(); RunQueueCommand.Cancel(); }
    private void NotifyCommands()
    {
        SendCommand.NotifyCanExecuteChanged(); RepeatSendCommand.NotifyCanExecuteChanged(); RunQueueCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSend)); OnPropertyChanged(nameof(CanStart)); OnPropertyChanged(nameof(StartStopLabel)); OnPropertyChanged(nameof(StartStopGlyph));
        OnPropertyChanged(nameof(LocalEndpoint)); OnPropertyChanged(nameof(Endpoint)); OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(SendTargetHint));
    }
    private void ReportError(Exception ex) { LastMessage = ex.Message; _log("网络", ex.Message, TransportEventKind.Error); }
    private void OnMessageReceived(object? sender, TransportMessage message)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) HandleMessage(message);
        else dispatcher.BeginInvoke(new Action(() => HandleMessage(message)));
    }
    private void HandleMessage(TransportMessage message)
    {
        if (_disposed) return;
        var nextState = message.State switch
        {
            TransportSessionState.Created => "未启动", TransportSessionState.Starting => "启动中", TransportSessionState.Listening => "监听中",
            TransportSessionState.Connecting => "连接中", TransportSessionState.Connected => "已连接", TransportSessionState.Stopping => "停止中",
            TransportSessionState.Stopped => "已停止", _ => "连接失败"
        };
        if (State != nextState)
        {
            State = nextState;
            NotifyCommands();
        }
        if (message.Kind is TransportEventKind.StateChanged or TransportEventKind.Connected or TransportEventKind.Disconnected || _session.Options.Protocol == TransportProtocol.Udp)
            UpdateNetworkTarget(message);
        UpdatePeerTargets(message);
        if (!CanSend) StopSending();
        if (message.Kind is TransportEventKind.Received or TransportEventKind.Sent)
        {
            var received = message.Kind == TransportEventKind.Received;
            Terminal.Add(message.Timestamp, received, message.Data);
            LastMessage = $"{(received ? "RX" : "TX")} {message.Data.Length} B · {message.RemoteEndPoint}";
            if (received && TreatAsHttp)
            {
                var raw = Encoding.UTF8.GetString(message.Data.Span);
                var split = raw.IndexOf('\n');
                HttpStartLine = split >= 0 ? raw[..split].TrimEnd('\r') : raw;
                HttpHeadersText = split >= 0 ? raw[(split + 1)..] : string.Empty;
            }
            _log(Protocol, LastMessage, message.Kind);
        }
        else if (message.Kind == TransportEventKind.Error)
        {
            LastMessage = message.Text ?? message.Error ?? "未知错误";
            _log(Protocol, LastMessage, message.Kind);
        }
        else if (message.Kind is TransportEventKind.Connected or TransportEventKind.Disconnected)
        {
            LastMessage = $"{(message.Kind == TransportEventKind.Connected ? "对端已连接" : "对端已断开")} · {message.RemoteEndPoint}";
            _log(Protocol, LastMessage, message.Kind);
        }
    }
    private void UpdateNetworkTarget(TransportMessage message)
    {
        var options = _session.Options;
        if (message.State is TransportSessionState.Starting or TransportSessionState.Connecting || _session.State is not (TransportSessionState.Connected or TransportSessionState.Listening))
        {
            _tcpPeers.Clear();
            _probePeer = null;
            NetworkStatus.Stop(_session.State is TransportSessionState.Starting or TransportSessionState.Connecting ? "等待连接" : "探测已停止");
            return;
        }
        if (options.Protocol == TransportProtocol.Tcp)
        {
            if (message.Kind == TransportEventKind.Connected && message.RemoteEndPoint is not null && IPEndPoint.TryParse(message.RemoteEndPoint, out var connected))
            {
                _probePeer = connected;
                _tcpPeers.Add(connected.ToString());
            }
            if (options.Mode == TransportMode.Client)
            {
                if (_probePeer is not null) NetworkStatus.Track(new(_probePeer.Address.ToString(), _probePeer.Port), $"TCP 服务 · {Endpoint}（{_probePeer}）");
                return;
            }
            if (message.Kind == TransportEventKind.Disconnected && message.RemoteEndPoint is not null) _tcpPeers.Remove(message.RemoteEndPoint);
            if (_tcpPeers.Count == 1 && IPEndPoint.TryParse(_tcpPeers.Single(), out var peer))
                NetworkStatus.Track(new(peer.Address.ToString()), $"TCP 客户端 · {peer}（客户端源端口不能用于 TCPing）");
            else NetworkStatus.Stop(_tcpPeers.Count > 1 ? $"{_tcpPeers.Count} 个对端" : "等待对端");
        }
        else if (options.Protocol == TransportProtocol.Mqtt)
            NetworkStatus.Track(new(options.RemoteHost), $"MQTT 代理 · {Endpoint}");
        else if (options.Mode == TransportMode.Client)
            NetworkStatus.Track(new(options.RemoteHost), $"UDP 目标 · {Endpoint}");
        else
        {
            if (message.Kind is TransportEventKind.Received or TransportEventKind.Sent && message.RemoteEndPoint is not null && IPEndPoint.TryParse(message.RemoteEndPoint, out var peer)) _probePeer = peer;
            if (_probePeer is not null) NetworkStatus.Track(new(_probePeer.Address.ToString()), $"UDP 最近对端 · {_probePeer}");
            else NetworkStatus.Stop("等待对端");
        }
    }

    private void UpdatePeerTargets(TransportMessage message)
    {
        if (!SupportsPeerTargeting || string.IsNullOrWhiteSpace(message.RemoteEndPoint)) return;
        var endpoint = message.RemoteEndPoint!;
        if (message.Kind is TransportEventKind.Connected or TransportEventKind.Received)
        {
            if (PeerTargets.All(peer => peer.Endpoint != endpoint))
                PeerTargets.Add(new NetworkPeerTarget(endpoint, endpoint));
        }
        else if (message.Kind == TransportEventKind.Disconnected)
        {
            var removed = PeerTargets.FirstOrDefault(peer => peer.Endpoint == endpoint);
            if (removed is not null) PeerTargets.Remove(removed);
            if (SelectedPeerTarget.Endpoint == endpoint) SelectedPeerTarget = NetworkPeerTarget.Broadcast;
        }
        OnPropertyChanged(nameof(PeerTargets));
    }

    private Task SendPayloadAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        return SupportsPeerTargeting && !SelectedPeerTarget.IsBroadcast
            ? _session.SendToAsync(payload, SelectedPeerTarget.Endpoint, cancellationToken)
            : SendAsync(payload, cancellationToken);
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; StopSending();
        NetworkStatus.Dispose();
        _session.MessageReceived -= OnMessageReceived;
        await _session.DisposeAsync();
        foreach (var task in new[] { RepeatSendCommand.ExecutionTask, RunQueueCommand.ExecutionTask }.OfType<Task>()) await task;
    }
}

public sealed record NetworkPeerTarget(string Endpoint, string DisplayName, bool IsBroadcast = false)
{
    public static NetworkPeerTarget Broadcast { get; } = new("*", "全部客户端", true);
}
