using System.Buffers;
using System.Net;
using System.Net.Sockets;
using EmbeddedDebugger.Core.Abstractions;
using EmbeddedDebugger.Core.Models;
using EmbeddedDebugger.Protocols;
using Microsoft.Extensions.Logging;

namespace EmbeddedDebugger.Infrastructure.Bridging;

public sealed class SerialBridgeService : ISerialBridgeService, IAsyncDisposable
{
    private readonly ISerialPortService _serialPortService;
    private readonly IVirtualSerialMapper _virtualSerialMapper;
    private readonly ILogger<SerialBridgeService> _logger;
    private readonly Dictionary<string, SerialBridgeRuntime> _runtimes = new();
    private readonly object _gate = new();

    public SerialBridgeService(
        ISerialPortService serialPortService,
        IVirtualSerialMapper virtualSerialMapper,
        ILogger<SerialBridgeService> logger)
    {
        _serialPortService = serialPortService;
        _virtualSerialMapper = virtualSerialMapper;
        _logger = logger;
    }

    public async Task<string> StartAsync(SerialBridgeProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var bridgeId = Guid.NewGuid().ToString("N");
        var runtime = new SerialBridgeRuntime(bridgeId, profile, _serialPortService, _virtualSerialMapper, _logger);
        try { await runtime.StartAsync(cancellationToken); }
        catch { await runtime.DisposeAsync(); throw; }
        lock (_gate) _runtimes.Add(bridgeId, runtime);
        return bridgeId;
    }

    public async Task StopAsync(string bridgeId, CancellationToken cancellationToken = default)
    {
        SerialBridgeRuntime? runtime;
        lock (_gate)
        {
            _runtimes.Remove(bridgeId, out runtime);
        }
        if (runtime is not null) await runtime.StopAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        SerialBridgeRuntime[] runtimes;
        lock (_gate) runtimes = _runtimes.Values.ToArray();
        foreach (var runtime in runtimes) await runtime.DisposeAsync();
        lock (_gate) _runtimes.Clear();
    }
}

internal sealed class SerialBridgeRuntime : IAsyncDisposable
{
    private readonly string _id;
    private readonly SerialBridgeProfile _profile;
    private readonly ISerialPortService _serialPortService;
    private readonly IVirtualSerialMapper _virtualSerialMapper;
    private readonly ILogger _logger;
    private readonly Dictionary<ushort, ISerialPortChannel> _channels = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private TcpListener? _listener;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private Task? _acceptTask;
    private Task? _receiveTask;
    private ushort _nextChannelId = 1;
    private IReadOnlyList<string> _virtualPorts = Array.Empty<string>();

    public SerialBridgeRuntime(
        string id,
        SerialBridgeProfile profile,
        ISerialPortService serialPortService,
        IVirtualSerialMapper virtualSerialMapper,
        ILogger logger)
    {
        _id = id;
        _profile = profile;
        _serialPortService = serialPortService;
        _virtualSerialMapper = virtualSerialMapper;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_profile.Channels.Count == 0)
        {
            throw new InvalidOperationException("至少选择一个串口通道。");
        }

        foreach (var settings in _profile.Channels)
        {
            var channel = _serialPortService.CreateChannel(settings);
            var channelId = _nextChannelId++;
            _channels[channelId] = channel;
            channel.DataReceived += (_, data) => _ = PublishChannelDataAsync(channelId, data, _lifetime.Token);
            await channel.OpenAsync(cancellationToken);
        }

        if (_profile.CreateVirtualPorts)
        {
            _virtualPorts = await _virtualSerialMapper.CreatePairAsync(_profile.Name, cancellationToken);
        }

        if (_profile.Mode == SerialBridgeMode.Server)
        {
            var address = ParseAddress(_profile.LocalHost);
            _listener = new TcpListener(address, _profile.LocalPort);
            _listener.Start();
            _acceptTask = AcceptLoopAsync(_lifetime.Token);
        }
        else
        {
            _client = new TcpClient { NoDelay = true };
            await _client.ConnectAsync(_profile.RemoteHost, _profile.RemotePort, cancellationToken);
            _stream = _client.GetStream();
            _receiveTask = ReceiveLoopAsync(_stream, _lifetime.Token);
            await SendOpenFramesAsync(cancellationToken);
        }

        _logger.LogInformation("Serial bridge {BridgeId} started with {ChannelCount} channels", _id, _channels.Count);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _lifetime.Cancel();
        _listener?.Stop();
        _stream?.Dispose();
        _client?.Dispose();
        foreach (var channel in _channels.Values)
        {
            await channel.CloseAsync(cancellationToken);
            await channel.DisposeAsync();
        }
        if (_virtualPorts.Count > 0)
        {
            await _virtualSerialMapper.RemovePairAsync(_virtualPorts, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _writeLock.Dispose();
        _lifetime.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        if (_listener is null) return;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                client.NoDelay = true;
                _client?.Dispose();
                _client = client;
                _stream = client.GetStream();
                _receiveTask = ReceiveLoopAsync(_stream, cancellationToken);
                await SendOpenFramesAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task SendOpenFramesAsync(CancellationToken cancellationToken)
    {
        foreach (var channelId in _channels.Keys)
        {
            var settings = _channels[channelId].Settings;
            var description = System.Text.Encoding.UTF8.GetBytes($"{settings.PortName} @ {settings.BaudRate}");
            await SendFrameAsync(new MultiplexFrame(channelId, MultiplexFrameFlags.Open, description), cancellationToken);
        }
    }

    private async Task PublishChannelDataAsync(ushort channelId, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (_profile.EnableMultiplexing)
        {
            await SendFrameAsync(new MultiplexFrame(channelId, MultiplexFrameFlags.Data, data), cancellationToken);
        }
        else
        {
            await SendFrameAsync(new MultiplexFrame(1, MultiplexFrameFlags.Data, data), cancellationToken);
        }
    }

    private async Task SendFrameAsync(MultiplexFrame frame, CancellationToken cancellationToken)
    {
        var stream = _stream;
        if (stream is null || !stream.CanWrite) return;
        var bytes = MultiplexFrameCodec.Encode(frame);
        await _writeLock.WaitAsync(cancellationToken);
        try { await stream.WriteAsync(bytes, cancellationToken); }
        catch (IOException) { }
        catch (SocketException) { }
        finally { _writeLock.Release(); }
    }

    private async Task ReceiveLoopAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var readBuffer = new byte[16 * 1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var count = await stream.ReadAsync(readBuffer, cancellationToken);
                if (count == 0) break;
                var target = buffer.GetSpan(count);
                readBuffer.AsSpan(0, count).CopyTo(target);
                buffer.Advance(count);
                while (MultiplexFrameCodec.TryDecode(ref buffer, out var frame) && frame is not null)
                {
                    if (frame.Flags == MultiplexFrameFlags.Data)
                    {
                        var channel = ResolveChannel(frame.ChannelId);
                        if (channel is not null) await channel.SendAsync(frame.Payload, cancellationToken);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (InvalidDataException ex)
        {
            _logger.LogWarning(ex, "Invalid serial bridge frame for {BridgeId}", _id);
        }
    }

    private ISerialPortChannel? ResolveChannel(ushort channelId)
    {
        if (_channels.TryGetValue(channelId, out var channel)) return channel;
        return _channels.Values.FirstOrDefault();
    }

    private static IPAddress ParseAddress(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host == "*") return IPAddress.Any;
        if (IPAddress.TryParse(host, out var address)) return address;
        return Dns.GetHostAddresses(host).First(address => address.AddressFamily == AddressFamily.InterNetwork);
    }
}

