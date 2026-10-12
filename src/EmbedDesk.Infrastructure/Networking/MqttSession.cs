using System.Text;
using EmbedDesk.Core.Abstractions;
using EmbedDesk.Core.Models;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace EmbedDesk.Infrastructure.Networking;

/// <summary>
/// MQTT 3/5 client transport used by the network debugger. The debugger treats
/// the configured topic as both the subscription and publish topic so the
/// regular receive/send panes remain useful for broker loopback tests.
/// </summary>
internal sealed class MqttSession : ITransportSession
{
    private readonly object _gate = new();
    private readonly TransportOptions _options;
    private readonly MqttFactory _factory = new();
    private CancellationTokenSource? _lifetime;
    private Task? _worker;
    private IMqttClient? _client;
    private TransportSessionState _state = TransportSessionState.Created;

    public MqttSession(TransportOptions options)
    {
        _options = options;
        Id = Guid.NewGuid().ToString("N");
    }

    public string Id { get; }
    public TransportOptions Options => _options;
    public string? BoundLocalEndpoint { get; private set; }
    public TransportSessionState State { get { lock (_gate) return _state; } }
    public event EventHandler<TransportMessage>? MessageReceived;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_state is not (TransportSessionState.Created or TransportSessionState.Stopped or TransportSessionState.Faulted)) return Task.CompletedTask;
            _state = TransportSessionState.Starting;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            BoundLocalEndpoint = null;
            _worker = RunAsync(_lifetime.Token);
        }
        Publish(TransportEventKind.StateChanged, null, ReadOnlyMemory<byte>.Empty, null, null);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? lifetime;
        IMqttClient? client;
        lock (_gate)
        {
            if (_state is TransportSessionState.Created or TransportSessionState.Stopped) return;
            _state = TransportSessionState.Stopping;
            lifetime = _lifetime;
            client = _client;
        }
        Publish(TransportEventKind.StateChanged, null, ReadOnlyMemory<byte>.Empty, null, null);
        lifetime?.Cancel();
        if (client?.IsConnected == true)
        {
            try { await client.DisconnectAsync(_factory.CreateClientDisconnectOptionsBuilder().Build(), cancellationToken); }
            catch (Exception) when (lifetime?.IsCancellationRequested == true) { }
        }
        if (_worker is not null)
        {
            try { await _worker.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (lifetime?.IsCancellationRequested == true) { }
        }
        lock (_gate) _client = null;
        lifetime?.Dispose();
        lock (_gate) _lifetime = null;
        SetState(TransportSessionState.Stopped);
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (data.IsEmpty) return;
        IMqttClient? client;
        lock (_gate) client = _client;
        if (client?.IsConnected != true) throw new InvalidOperationException("MQTT 尚未连接到代理服务器。");
        var message = _factory.CreateApplicationMessageBuilder()
            .WithTopic(_options.MqttTopic)
            .WithPayload(data.ToArray())
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
            .Build();
        await client.PublishAsync(message, cancellationToken);
        Publish(TransportEventKind.Sent, _options.MqttTopic, data, null, null);
    }

    public Task SendToAsync(ReadOnlyMemory<byte> data, string remoteEndpoint, CancellationToken cancellationToken = default)
        => SendAsync(data, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        lock (_gate) _client = null;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var client = _factory.CreateMqttClient();
            var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.ApplicationMessageReceivedAsync += args =>
            {
                var payload = args.ApplicationMessage.PayloadSegment.ToArray();
                Publish(TransportEventKind.Received, args.ApplicationMessage.Topic, payload, Encoding.UTF8.GetString(payload), null);
                return Task.CompletedTask;
            };
            client.DisconnectedAsync += args =>
            {
                if (args.ClientWasConnected && !cancellationToken.IsCancellationRequested)
                    Publish(TransportEventKind.Disconnected, Endpoint, ReadOnlyMemory<byte>.Empty, args.ReasonString, args.Exception);
                ended.TrySetResult(true);
                return Task.CompletedTask;
            };
            lock (_gate) _client = client;
            try
            {
                SetState(TransportSessionState.Connecting);
                var result = await client.ConnectAsync(BuildOptions(), cancellationToken);
                if (result.ResultCode != MqttClientConnectResultCode.Success)
                    throw new InvalidOperationException($"MQTT 连接被代理拒绝：{result.ResultCode} {result.ReasonString}".Trim());
                BoundLocalEndpoint = Endpoint;
                var subscription = _factory.CreateSubscribeOptionsBuilder()
                    .WithTopicFilter(_options.MqttTopic, MqttQualityOfServiceLevel.AtMostOnce, false, false, MqttRetainHandling.SendAtSubscribe)
                    .Build();
                await client.SubscribeAsync(subscription, cancellationToken);
                SetState(TransportSessionState.Connected);
                Publish(TransportEventKind.Connected, Endpoint, ReadOnlyMemory<byte>.Empty, null, null);
                await ended.Task.WaitAsync(cancellationToken);
                if (!cancellationToken.IsCancellationRequested)
                    SetState(_options.AutoReconnect ? TransportSessionState.Connecting : TransportSessionState.Faulted);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                SetState(TransportSessionState.Faulted);
                Publish(TransportEventKind.Error, Endpoint, ReadOnlyMemory<byte>.Empty, ex.Message, ex);
                if (!_options.AutoReconnect) break;
                try { await Task.Delay(_options.ReconnectDelay, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            }
            finally
            {
                if (client.IsConnected)
                {
                    try { await client.DisconnectAsync(_factory.CreateClientDisconnectOptionsBuilder().Build(), CancellationToken.None); }
                    catch (Exception) { }
                }
                lock (_gate)
                {
                    if (ReferenceEquals(_client, client)) _client = null;
                }
            }
            if (!_options.AutoReconnect) break;
        }
    }

    private MqttClientOptions BuildOptions()
    {
        var builder = _factory.CreateClientOptionsBuilder()
            .WithClientId(string.IsNullOrWhiteSpace(_options.MqttClientId) ? $"embeddesk-{Guid.NewGuid():N}" : _options.MqttClientId)
            .WithTcpServer(_options.RemoteHost, _options.RemotePort)
            .WithCleanSession();
        if (!string.IsNullOrWhiteSpace(_options.MqttUsername)) builder.WithCredentials(_options.MqttUsername, _options.MqttPassword ?? string.Empty);
        if (_options.MqttUseTls) builder.WithTlsOptions(_ => { });
        return builder.Build();
    }

    private string Endpoint => $"{_options.RemoteHost}:{_options.RemotePort}";

    private void SetState(TransportSessionState state)
    {
        lock (_gate) _state = state;
        Publish(TransportEventKind.StateChanged, null, ReadOnlyMemory<byte>.Empty, null, null);
    }

    private void Publish(TransportEventKind kind, string? remote, ReadOnlyMemory<byte> data, string? text, Exception? error)
    {
        MessageReceived?.Invoke(this, new TransportMessage(DateTimeOffset.Now, Id, kind, State, remote, data, text, error?.ToString()));
    }
}
