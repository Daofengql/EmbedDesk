using System.Net;
using System.Net.Sockets;
using EmbeddedDebugger.Core.Abstractions;
using EmbeddedDebugger.Core.Models;
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
        if (runtime is not null) await runtime.DisposeAsync();
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
    private readonly SerialBridgeProfile _profile;
    private readonly ISerialPortService _serialPortService;
    private readonly IVirtualSerialMapper _virtualSerialMapper;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<ushort, string> _mappings = new();
    private TcpListener? _listener;
    private SerialBridgeConnection? _connection;
    private Task? _worker;
    private IReadOnlyList<string> _virtualPorts = [];
    private int _disposed;

    public SerialBridgeRuntime(string id, SerialBridgeProfile profile, ISerialPortService serialPortService,
        IVirtualSerialMapper virtualSerialMapper, ILogger logger)
    {
        _profile = profile; _serialPortService = serialPortService;
        _virtualSerialMapper = virtualSerialMapper; _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_profile.Channels.Count is < 1 or > ushort.MaxValue ||
            _profile.Channels.Any(c => string.IsNullOrWhiteSpace(c.PortName)) ||
            _profile.Channels.Select(c => c.PortName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != _profile.Channels.Count)
            throw new ArgumentException("请选择不重复的本地串口映射。");
        for (var i = 0; i < _profile.Channels.Count; i++) _mappings.Add((ushort)(i + 1), _profile.Channels[i].PortName);
        if (_profile.CreateVirtualPorts)
            _virtualPorts = await _virtualSerialMapper.CreatePairAsync(_profile.Name, cancellationToken).ConfigureAwait(false);
        if (_profile.Mode == SerialBridgeMode.Server)
        {
            var address = string.IsNullOrWhiteSpace(_profile.LocalHost) || _profile.LocalHost == "*" ? IPAddress.Any :
                IPAddress.TryParse(_profile.LocalHost, out var ip) ? ip :
                (await Dns.GetHostAddressesAsync(_profile.LocalHost, cancellationToken).ConfigureAwait(false)).First();
            _listener = new TcpListener(address, _profile.LocalPort);
            _listener.Start();
            _worker = Task.Run(AcceptAsync);
        }
        else
        {
            var client = new TcpClient { NoDelay = true };
            try { await client.ConnectAsync(_profile.RemoteHost, _profile.RemotePort, cancellationToken).ConfigureAwait(false); }
            catch { client.Dispose(); throw; }
            _connection = new(client, _mappings, _serialPortService, _logger, _lifetime.Token);
        }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(_lifetime.Token).ConfigureAwait(false);
                // Finish the old reader, sender and COM callbacks before handing a new client the mappings.
                if (_connection is not null) await _connection.DisposeAsync().ConfigureAwait(false);
                if (_lifetime.IsCancellationRequested) { client.Dispose(); break; }
                _connection = new(client, _mappings, _serialPortService, _logger, _lifetime.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        { if (!_lifetime.IsCancellationRequested) _logger.LogWarning(ex, "Serial bridge listener stopped"); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel(); _listener?.Stop();
        if (_worker is not null) await _worker.ConfigureAwait(false);
        if (_connection is not null) await _connection.DisposeAsync().ConfigureAwait(false);
        if (_virtualPorts.Count > 0) await _virtualSerialMapper.RemovePairAsync(_virtualPorts).ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
