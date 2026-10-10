using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows;
using EmbeddedDebugger.Core.Abstractions;
using EmbeddedDebugger.Core.Models;

namespace EmbeddedDebugger.App.ViewModels;

public sealed class NetworkSessionViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ITransportSession _session;
    private readonly Action<string, string, TransportEventKind> _log;
    private string _state;
    private string _lastMessage = string.Empty;
    private string _httpStartLine = string.Empty;
    private string _httpHeadersText = string.Empty;

    public NetworkSessionViewModel(ITransportSession session, Action<string, string, TransportEventKind> log)
    {
        _session = session;
        _log = log;
        _state = session.State.ToString();
        _session.MessageReceived += OnMessageReceived;
        Terminal = new TerminalBufferViewModel();
    }

    public string Id => _session.Id;
    public string Protocol => _session.Options.Protocol.ToString().ToUpperInvariant();
    public string Mode => _session.Options.Mode == TransportMode.Client ? "客户端" : "服务端";
    public string Endpoint => _session.Options.Mode == TransportMode.Client
        ? $"{_session.Options.RemoteHost}:{_session.Options.RemotePort}"
        : $"{_session.Options.LocalHost}:{_session.Options.LocalPort}";
    public string Detail => $"{Protocol} / {Mode} / {Endpoint}";
    public string State
    {
        get => _state;
        private set { if (SetProperty(ref _state, value)) OnPropertyChanged(nameof(CanSend)); }
    }
    public string LastMessage
    {
        get => _lastMessage;
        private set => SetProperty(ref _lastMessage, value);
    }
    public string HttpStartLine { get => _httpStartLine; private set => SetProperty(ref _httpStartLine, value); }
    public string HttpHeadersText { get => _httpHeadersText; private set => SetProperty(ref _httpHeadersText, value); }
    public bool TreatAsHttp => _session.Options.TreatAsHttp;
    public TerminalBufferViewModel Terminal { get; }
    public bool CanSend => _session.State == TransportSessionState.Connected ||
        (_session.Options.Protocol == TransportProtocol.Udp && _session.State == TransportSessionState.Listening);

    public Task StartAsync(CancellationToken cancellationToken = default) => _session.StartAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken = default) => _session.StopAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        _session.MessageReceived -= OnMessageReceived;
        await _session.DisposeAsync();
    }

    public Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) => _session.SendAsync(payload, cancellationToken);

    private void OnMessageReceived(object? sender, TransportMessage message)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) HandleMessage(message);
        else dispatcher.BeginInvoke(new Action(() => HandleMessage(message)));
    }

    private void HandleMessage(TransportMessage message)
    {
        State = message.State switch
        {
            TransportSessionState.Created => "未启动",
            TransportSessionState.Starting => "启动中",
            TransportSessionState.Listening => "监听中",
            TransportSessionState.Connecting => "连接中",
            TransportSessionState.Connected => "已连接",
            TransportSessionState.Stopping => "停止中",
            TransportSessionState.Stopped => "已停止",
            _ => "连接失败"
        };
        if (message.Kind == TransportEventKind.Received)
        {
            Terminal.Add(message.Timestamp, true, message.Data);
            LastMessage = message.Text ?? Convert.ToHexString(message.Data.Span);
            if (TreatAsHttp)
            {
                var lines = LastMessage.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                HttpStartLine = lines.FirstOrDefault() ?? string.Empty;
                HttpHeadersText = string.Join(Environment.NewLine, lines.Skip(1));
            }
            _log(Protocol, $"RX {message.RemoteEndPoint ?? "-"}  {LastMessage}", TransportEventKind.Received);
        }
        else if (message.Kind == TransportEventKind.Sent)
        {
            Terminal.Add(message.Timestamp, false, message.Data);
            _log(Protocol, $"TX {message.Data.Length} bytes -> {message.RemoteEndPoint ?? "-"}", TransportEventKind.Sent);
        }
        else if (message.Kind == TransportEventKind.Error)
        {
            _log(Protocol, message.Error ?? message.Text ?? "未知错误", TransportEventKind.Error);
        }
        else if (message.Kind is TransportEventKind.Connected or TransportEventKind.Disconnected)
        {
            _log(Protocol, $"{message.Kind}: {message.RemoteEndPoint ?? "-"}", message.Kind);
        }
    }
}




