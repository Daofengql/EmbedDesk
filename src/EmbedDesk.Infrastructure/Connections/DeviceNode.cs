using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using EmbedDesk.Core.Abstractions;
using EmbedDesk.Core.Models;
using EmbedDesk.Protocols;

namespace EmbedDesk.Infrastructure.Connections;

/// <summary>One relay connection, authenticated per-device sessions, independent service channels.</summary>
public sealed partial class DeviceNode : IAsyncDisposable
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ISerialPortService _serial;
    private readonly ConcurrentDictionary<string, Peer> _peers = new();
    private readonly ConcurrentDictionary<string, VisitorRequest> _requests = new();
    private readonly ConcurrentDictionary<string, PortMapping> _mappings = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1), _socketSend = new(1, 1);
    private readonly object _connectionRoleGate = new();
    private SharedDeviceService[] _services = [];
    private CancellationTokenSource? _lifetime;
    private TcpListener? _listener;
    private Task _runner = Task.CompletedTask, _acceptLoop = Task.CompletedTask;
    private ClientWebSocket? _socket;
    private DeviceNodeOptions _options = new();
    private long _relayBitsPerSecond;
    private volatile bool _online;
    private string _status = "尚未配置中继服务端";
    public DeviceNode(ISerialPortService serial) => _serial = serial;
    public string DeviceId { get; private set; } = string.Empty;
    public string Status => _status;
    public bool IsOnline => _online;
    public bool CanInitiateConnection { get { lock (_connectionRoleGate) return !_peers.Values.Any(peer => peer.IsOwner); } }
    public event EventHandler? Changed;
    public event EventHandler<DeviceApproval>? ApprovalRequested;
    public event EventHandler<string>? OperationFailed;
    public IReadOnlyList<DevicePeerInfo> Peers => _peers.Values.Select(p => p.Info).ToArray();
    public IReadOnlyList<DeviceRemoteService> RemoteServices => _peers.Values.SelectMany(p => p.Services.Select(s => new DeviceRemoteService(p.Id, p.RemoteId, s))).ToArray();
    public IReadOnlyList<DeviceApproval> PendingApprovals => _peers.Values.Where(p => p.AwaitingApproval).Select(p => new DeviceApproval(p.Id, p.RemoteId)).ToArray();
    public IReadOnlyList<DevicePortMapping> Mappings => _mappings.Values.Select(m => m.Info).ToArray();
    public bool IsShared(string portName) => _serial is ISerialPortSharing sharing && sharing.IsShared(portName);
    public void ConfigureServices(IEnumerable<SharedDeviceService> services)
    {
        var next = services.ToArray();
        if (next.Length > 128 || next.Select(s => s.Id).Distinct().Count() != next.Length) throw new ArgumentException("共享服务过多或编号重复。");
        foreach (var service in next) service.Validate();
        var catalog = next.Where(s => s.Enabled).ToArray();
        if (JsonSerializer.SerializeToUtf8Bytes(new NodeRpc("services", Services: catalog), Json).Length > 32 * 1024) throw new ArgumentException("共享服务目录过大，请减少数量或缩短服务名称。");
        if (_peers.Values.Any(p => p.HasOpenChannels)) throw new InvalidOperationException("请先关闭正在使用的服务通道，再修改共享配置。");
        if (_serial is ISerialPortSharing sharing) sharing.SetSharedPorts(next.Where(s => s.Enabled && s.Kind == SharedServiceKind.Serial).Select(s => s.PortName));
        _services = next;
        foreach (var peer in _peers.Values.Where(p => p.Ready && p.IsOwner)) _ = peer.QueueAsync(new NodeRpc("services", Services: PublishedServices()));
        Notify();
    }
    // Sent only through the authenticated encrypted peer channel. Targets are display
    // metadata; opening a channel still resolves its ID against our own local services.
    private SharedDeviceService[] PublishedServices() => _services.Where(s => s.Enabled).ToArray();
    public async Task StartAsync(DeviceNodeOptions options, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(options.ServerUrl, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.Scheme is not ("https" or "http"))
            throw new ArgumentException("中继地址须为 HTTP 或 HTTPS URL；生产公网访问应由反向代理提供 HTTPS。");
        if (Convert.FromBase64String(options.Credential).Length != 32) throw new ArgumentException("设备凭据无效。");
        if (options.TimeoutSeconds is < 2 or > 120 || options.RetrySeconds is < 1 or > 300) throw new ArgumentException("连接超时或重连间隔无效。");
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false); _options = options;
            _lifetime = new();
            if (options.PreferDirect)
            {
                _listener = new TcpListener(IPAddress.Any, 0); _listener.Start();
                _acceptLoop = AcceptDirectAsync(_lifetime.Token);
            }
            else
            {
                _listener = null; _acceptLoop = Task.CompletedTask;
            }
            _runner = RunAsync(_lifetime.Token);
        }
        finally { _lifecycle.Release(); }
    }
    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }
    private async Task StopCoreAsync()
    {
        _lifetime?.Cancel(); _listener?.Stop(); _socket?.Abort();
        await Task.WhenAll(_runner, _acceptLoop).ConfigureAwait(false);
        foreach (var mapping in _mappings.Values) await mapping.DisposeAsync().ConfigureAwait(false); _mappings.Clear();
        foreach (var peer in _peers.Values) await peer.DisposeAsync().ConfigureAwait(false); _peers.Clear();
        foreach (var request in _requests.Values) request.Completion.TrySetException(new IOException("中继连接已停止。"));
        _lifetime?.Dispose(); _lifetime = null; _listener = null; _online = false; SetStatus("连接已停止");
    }
    private IWebProxy? BuildProxy() => RelayHttpTransport.CreateProxy(_options);
    private bool ValidateCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        return RelayHttpTransport.ValidateCertificate(_options, certificate, errors);
    }
    private async Task RunAsync(CancellationToken token)
    {
        var retryAttempt = 0;
        while (!token.IsCancellationRequested)
        {
            var connectedAt = 0L;
            var failure = "中继连接已关闭";
            try
            {
                SetStatus("连接中继服务端…");
                using var handler = RelayHttpTransport.CreateHandler(_options);
                using var http = new HttpClient(handler) { BaseAddress = new Uri(_options.ServerUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds) };
                using var request = new HttpRequestMessage(HttpMethod.Post, "v1/register") { Content = new StringContent(JsonSerializer.Serialize(new { credential = _options.Credential }, Json), Encoding.UTF8, "application/json") };
                if (_options.AdmissionKey.Length > 0) request.Headers.Authorization = new("Bearer", _options.AdmissionKey);
                using var response = await http.SendAsync(request, token).ConfigureAwait(false); response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
                DeviceId = document.RootElement.GetProperty("id").GetString()!;
                if (DeviceId?.Length != 12 || !DeviceId.All(Uri.IsHexDigit)) throw new InvalidDataException("服务端设备 ID 无效。");
                _relayBitsPerSecond = document.RootElement.TryGetProperty("bitsPerSecond", out var bits) && bits.TryGetInt64(out var value) ? value : 0;
                using var socket = new ClientWebSocket(); _socket = socket;
                socket.Options.Proxy = BuildProxy(); socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                socket.Options.RemoteCertificateValidationCallback = ValidateCertificate;
                socket.Options.SetRequestHeader("Authorization", "Bearer " + _options.Credential); socket.Options.SetRequestHeader("X-Device-ID", DeviceId);
                var address = new UriBuilder(new Uri(http.BaseAddress!, "v1/channel")) { Scheme = http.BaseAddress!.Scheme == "https" ? "wss" : "ws" };
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(token); connect.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
                await socket.ConnectAsync(address.Uri, connect.Token).ConfigureAwait(false);
                _online = true; connectedAt = Stopwatch.GetTimestamp(); SetStatus(_relayBitsPerSecond > 0 ? $"在线 · 可用带宽 {FormatBandwidth(_relayBitsPerSecond)}" : "在线 · 公网中继");
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(token);
                var heart = HeartbeatAsync(socket, heartbeat.Token);
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        var message = await ReceiveMessageAsync(socket, token).ConfigureAwait(false);
                        await HandleMessageAsync(message, token).ConfigureAwait(false);
                    }
                }
                finally { heartbeat.Cancel(); await heart.ConfigureAwait(false); }
            }
            catch (Exception ex) when (ex is HttpRequestException or WebSocketException or IOException or OperationCanceledException or InvalidDataException or JsonException or CryptographicException or ArgumentException)
            {
                failure = ex.Message;
            }
            finally
            {
                _online = false; _socket?.Abort(); _socket = null;
                foreach (var mapping in _mappings.Values) await mapping.DisposeAsync().ConfigureAwait(false); _mappings.Clear();
                foreach (var peer in _peers.Values) await peer.DisposeAsync().ConfigureAwait(false); _peers.Clear();
                foreach (var request in _requests.Values) request.Completion.TrySetException(new IOException("中继连接已断开。"));
                Notify();
            }
            try
            {
                if (token.IsCancellationRequested) break;
                // Brief successful upgrades must not reset the backoff during a relay outage.
                if (connectedAt != 0 && Stopwatch.GetElapsedTime(connectedAt) >= TimeSpan.FromMinutes(1)) retryAttempt = 0;
                var exponent = Math.Min(retryAttempt++, 10);
                var delay = Math.Clamp(_options.RetrySeconds * Math.Pow(2, exponent) * (0.8 + Random.Shared.NextDouble() * 0.4), 1, 300);
                SetStatus($"中继连接失败：{failure}；{Math.Ceiling(delay):0} 秒后重试");
                await Task.Delay(TimeSpan.FromSeconds(delay), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }
    }
    private async Task HeartbeatAsync(ClientWebSocket socket, CancellationToken token)
    {
        try { while (true) { await Task.Delay(TimeSpan.FromSeconds(20), token).ConfigureAwait(false); await SendMessageAsync(new("heartbeat"), token).ConfigureAwait(false); } }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or InvalidOperationException or IOException)
        { if (!token.IsCancellationRequested) socket.Abort(); }
    }
    internal async Task SendMessageAsync(NodeMessage message, CancellationToken token)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (data.Length > 96 * 1024) throw new InvalidDataException("中继报文过大。");
        await _socketSend.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var socket = _socket ?? throw new IOException("中继已断开。");
            token.ThrowIfCancellationRequested();
            // Canceling a ClientWebSocket send aborts the entire socket. A peer's cancellation
            // may cancel the gate wait, but a started global write must finish for other peers.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime?.Token ?? CancellationToken.None);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await socket.SendAsync(data.AsMemory(), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
        }
        finally { _socketSend.Release(); }
    }
    private static async Task<NodeMessage> ReceiveMessageAsync(WebSocket socket, CancellationToken token)
    {
        using var buffer = new MemoryStream(); var chunk = new byte[8192];
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        while (true)
        {
            idle.CancelAfter(TimeSpan.FromSeconds(75));
            ValueWebSocketReceiveResult result;
            try { result = await socket.ReceiveAsync(chunk.AsMemory(), idle.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("中继 75 秒未响应，连接已超时。"); }
            if (result.MessageType != WebSocketMessageType.Text) throw new IOException("中继连接已关闭或报文无效。");
            if (buffer.Length + result.Count > 96 * 1024) throw new InvalidDataException("中继报文过大。");
            buffer.Write(chunk, 0, result.Count);
            if (result.EndOfMessage) return JsonSerializer.Deserialize<NodeMessage>(buffer.ToArray(), Json) ?? throw new InvalidDataException("中继报文为空。");
        }
    }
    private void SetStatus(string status) { _status = status; Notify(); }
    public static string FormatBandwidth(long bitsPerSecond)
    {
        string[] units = ["bps", "Kbps", "Mbps", "Gbps", "Tbps", "Pbps", "Ebps"];
        var value = Math.Max(0, (decimal)bitsPerSecond);
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1) { value /= 1000; unit++; }
        return value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " " + units[unit];
    }
    internal void Notify() => Changed?.Invoke(this, EventArgs.Empty);
    internal void Fail(string error) => OperationFailed?.Invoke(this, error);
    internal static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Json);
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
