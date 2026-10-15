using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using EmbedDesk.Core.Abstractions;
using EmbedDesk.Core.Models;
using EmbedDesk.Protocols;

namespace EmbedDesk.Infrastructure.Connections;

public partial class DeviceNode
{
    public async Task ConnectPeerAsync(string deviceId, string password, CancellationToken token = default)
    {
        if (!_online) throw new InvalidOperationException("请先连接中继服务端。");
        if (password.Length > 128) throw new ArgumentException("连接密码不能超过 128 个字符。");
        deviceId = deviceId.Trim().ToUpperInvariant();
        if (deviceId.Length != 12 || !deviceId.All(Uri.IsHexDigit) || deviceId == DeviceId) throw new ArgumentException("对方设备 ID 无效。");
        if (_requests.Count >= 8 || _peers.Count >= 16) throw new InvalidOperationException("连接请求过多。");
        var request = new VisitorRequest(deviceId, password);
        _requests[request.Id] = request;
        try
        {
            await SendMessageAsync(new("connect", To: deviceId, Body: Element(new NodeHello(request.Id, password.Length == 0 ? "manual" : "password", request.Key.PublicKey, request.Nonce))), token).ConfigureAwait(false);
            await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(90), token).ConfigureAwait(false);
        }
        catch
        {
            if (request.SessionId is { } id) await DisconnectPeerAsync(id).ConfigureAwait(false);
            throw;
        }
        finally { _requests.TryRemove(request.Id, out _); request.Dispose(); }
    }
    public async Task ApproveAsync(string sessionId, bool approve, CancellationToken token = default)
    {
        if (!_peers.TryGetValue(sessionId, out var peer) || !peer.AwaitingApproval) throw new InvalidOperationException("授权请求已失效。");
        peer.AwaitingApproval = false;
        if (!approve) { await DisconnectPeerAsync(sessionId).ConfigureAwait(false); return; }
        peer.Secret = RandomNumberGenerator.GetBytes(32);
        await ChallengeAsync(peer, peer.Secret, token).ConfigureAwait(false); Notify();
    }
    public async Task DisconnectPeerAsync(string sessionId, bool notifyRelay = true)
    {
        var hadSession = _peers.ContainsKey(sessionId) || _requests.Values.Any(r => r.SessionId == sessionId);
        foreach (var mapping in _mappings.Values.Where(m => m.Info.SessionId == sessionId)) await UnmapPortAsync(mapping.Info.Id).ConfigureAwait(false);
        if (_peers.TryRemove(sessionId, out var peer)) await peer.DisposeAsync().ConfigureAwait(false);
        try { if (_online && hadSession && notifyRelay) await SendMessageAsync(new("close", Session: sessionId), _lifetime?.Token ?? default).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Net.WebSockets.WebSocketException) { }
        Notify();
    }
    private async Task HandleMessageAsync(NodeMessage message, CancellationToken token)
    {
        try
        {
            if (message.Type is "online" or "heartbeat") return;
            if (message.Type == "pending")
            {
                var hello = message.Body!.Value.Deserialize<NodeHello>(Json)!;
                if (_requests.TryGetValue(hello.RequestId, out var pending)) pending.SessionId = message.Session;
                else await SendMessageAsync(new("close", Session: message.Session), token).ConfigureAwait(false);
                return;
            }
            if (message.Type == "connect")
            {
                var hello = message.Body!.Value.Deserialize<NodeHello>(Json) ?? throw new InvalidDataException("连接请求为空。");
                ValidateHello(hello);
                var owner = new Peer(this, message.Session!, message.From!, true, CollaborationKeyAgreement.CreateKeyPair(), RandomNumberGenerator.GetBytes(32)) { Hello = hello };
                if (!_peers.TryAdd(owner.Id, owner)) { await owner.DisposeAsync().ConfigureAwait(false); return; }
                if (hello.Mode == "manual") { owner.AwaitingApproval = true; ApprovalRequested?.Invoke(this, new(owner.Id, owner.RemoteId)); Notify(); }
                else
                {
                    if (_options.ManualApprovalOnly || _options.Password.Length < 8) { await DisconnectPeerAsync(owner.Id).ConfigureAwait(false); return; }
                    owner.Secret = PasswordSecret(_options.Password, owner.Id, owner.RemoteId, DeviceId);
                    await ChallengeAsync(owner, null, token).ConfigureAwait(false);
                }
                return;
            }
            if (message.Type == "challenge")
            {
                var challenge = message.Body!.Value.Deserialize<NodeChallenge>(Json) ?? throw new InvalidDataException("握手为空。");
                if (!_requests.TryGetValue(challenge.RequestId, out var request) || request.RemoteId != message.From) throw new InvalidDataException("握手不属于当前连接请求。");
                request.SessionId = message.Session;
                var visitor = new Peer(this, message.Session!, message.From!, false, request.TakeKey(), request.Nonce);
                visitor.Secret = request.Password.Length == 0
                    ? challenge.ManualSecret is { Length: 32 } secret ? secret : throw new InvalidDataException("人工授权密钥无效。")
                    : PasswordSecret(request.Password, visitor.Id, DeviceId, visitor.RemoteId);
                visitor.Transcript = CollaborationHandshake.CreateTranscript(Convert.FromHexString(visitor.Id), challenge.PublicKey, visitor.Key.PublicKey, challenge.Nonce, visitor.Nonce);
                if (!CollaborationHandshake.VerifyProof(visitor.Secret, visitor.Transcript, CollaborationPeerRole.Owner, challenge.Proof))
                {
                    await visitor.DisposeAsync().ConfigureAwait(false); request.Completion.TrySetException(new UnauthorizedAccessException("连接密码错误或握手身份校验失败。"));
                    await SendMessageAsync(new("close", Session: message.Session), token).ConfigureAwait(false); return;
                }
                visitor.InitializeKeys(challenge.PublicKey);
                visitor.Request = request;
                if (!_peers.TryAdd(visitor.Id, visitor)) { await visitor.DisposeAsync().ConfigureAwait(false); throw new InvalidDataException("握手重复。"); }
                await SendMessageAsync(new("proof", Session: visitor.Id, Body: Element(CollaborationHandshake.CreateProof(visitor.Secret, visitor.Transcript, CollaborationPeerRole.Visitor))), token).ConfigureAwait(false); return;
            }
            if (message.Session is null || !_peers.TryGetValue(message.Session, out var peer))
            {
                if (message.Type is "close" or "error") foreach (var pending in _requests.Values.Where(r => r.SessionId is not null && r.SessionId == message.Session)) pending.Completion.TrySetException(new IOException(message.Error ?? "对方拒绝了连接。"));
                if (message.Type == "error")
                {
                    Fail(message.Error ?? "中继请求失败");
                    if (message.Body is { } body && body.TryGetProperty("requestId", out var rid) && _requests.TryGetValue(rid.GetString() ?? "", out var failed))
                        failed.Completion.TrySetException(new IOException(message.Error ?? "中继请求失败"));
                }
                return;
            }
            if (message.From is not null && message.From != peer.RemoteId) throw new InvalidDataException("对端身份与会话不匹配。");
            switch (message.Type)
            {
                case "proof":
                    if (!peer.IsOwner || peer.Secret is null || peer.Transcript is null || !CollaborationHandshake.VerifyProof(peer.Secret, peer.Transcript, CollaborationPeerRole.Visitor, message.Body!.Value.Deserialize<byte[]>(Json)!))
                        throw new UnauthorizedAccessException("握手证明无效。");
                    peer.InitializeKeys(peer.Hello!.PublicKey);
                    await SendMessageAsync(new("accept", Session: peer.Id, Body: Element(new NodeAccept(DirectEndpoints()))), token).ConfigureAwait(false); Notify(); break;
                case "accept":
                    if (peer.IsOwner || peer.Keys is null) throw new InvalidDataException("无效的会话确认。");
                    var accept = message.Body!.Value.Deserialize<NodeAccept>(Json);
                    if (accept?.Endpoints is null || accept.Endpoints.Length > 9 || accept.Endpoints.Any(e => e is null || e.Length > 80)) throw new InvalidDataException("直连候选地址无效。");
                    _ = NegotiatePathAsync(peer, accept, token); break;
                case "relay":
                    if (!peer.IsOwner || peer.Keys is null) throw new InvalidDataException("无效的中继回退请求。");
                    peer.Start(null); break;
                case "data":
                    if (!peer.Ready || peer.Path != "公网中继") throw new InvalidDataException("数据通道尚未建立。");
                    var decrypted = peer.Secure!.Decrypt(message.Body!.Value.Deserialize<byte[]>(Json)!);
                    peer.Receive(decrypted.Plaintext); break;
                case "close": case "error":
                    peer.Request?.Completion.TrySetException(new IOException(message.Error ?? "对方拒绝或结束了连接。"));
                    await DisconnectPeerAsync(peer.Id, false).ConfigureAwait(false); break;
            }
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException or JsonException or ArgumentException or UnauthorizedAccessException or NullReferenceException or InvalidOperationException)
        {
            Fail("会话建立失败：" + ex.Message);
            if (message.Session is { } id) await DisconnectPeerAsync(id).ConfigureAwait(false);
        }
    }
    private static void ValidateHello(NodeHello hello)
    {
        if (hello.RequestId.Length != 32 || hello.PublicKey.Length is < 32 or > 256 || hello.Nonce.Length != 32 || hello.Mode is not ("manual" or "password")) throw new InvalidDataException("连接握手参数无效。");
    }
    private static byte[] PasswordSecret(string password, string session, string origin, string owner) =>
        Rfc2898DeriveBytes.Pbkdf2(password, SHA256.HashData(Encoding.UTF8.GetBytes("EmbedDesk node v1:" + session + ":" + origin + ":" + owner)), 100000, HashAlgorithmName.SHA256, 32);
    private async Task ChallengeAsync(Peer peer, byte[]? manualSecret, CancellationToken token)
    {
        peer.Transcript = CollaborationHandshake.CreateTranscript(Convert.FromHexString(peer.Id), peer.Key.PublicKey, peer.Hello!.PublicKey, peer.Nonce, peer.Hello.Nonce);
        await SendMessageAsync(new("challenge", Session: peer.Id, Body: Element(new NodeChallenge(peer.Hello.RequestId, peer.Key.PublicKey, peer.Nonce,
            CollaborationHandshake.CreateProof(peer.Secret!, peer.Transcript, CollaborationPeerRole.Owner), manualSecret))), token).ConfigureAwait(false);
    }
    private string[] DirectEndpoints()
    {
        if (!_options.PreferDirect || _listener is null) return [];
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        var addresses = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address).Where(IsPrivateV4).Distinct().Take(8).Select(a => $"{a}:{port}").ToList();
        if (new Uri(_options.ServerUrl).IsLoopback) addresses.Insert(0, $"127.0.0.1:{port}");
        return addresses.ToArray();
    }
    private static bool IsPrivateV4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes(); return bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 169 && bytes[1] == 254;
    }
    private async Task NegotiatePathAsync(Peer peer, NodeAccept accept, CancellationToken token)
    {
        try
        {
            if (_options.PreferDirect)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, peer.Token); timeout.CancelAfter(TimeSpan.FromSeconds(3));
                foreach (var candidate in accept.Endpoints.Take(9))
                {
                    if (!IPEndPoint.TryParse(candidate, out var endpoint) || !IsPrivateV4(endpoint.Address) && !(IPAddress.IsLoopback(endpoint.Address) && new Uri(_options.ServerUrl).IsLoopback)) continue;
                    var client = new TcpClient { NoDelay = true };
                    try
                    {
                        await client.ConnectAsync(endpoint.Address, endpoint.Port, timeout.Token).ConfigureAwait(false);
                        var stream = client.GetStream(); await stream.WriteAsync(Encoding.ASCII.GetBytes(peer.Id), timeout.Token).ConfigureAwait(false);
                        var nonce = new byte[32]; await stream.ReadExactlyAsync(nonce, timeout.Token).ConfigureAwait(false);
                        var binding = nonce.Concat(Convert.FromHexString(peer.Id)).ToArray();
                        await stream.WriteAsync(HMACSHA256.HashData(peer.Keys!.VisitorToOwner, binding), timeout.Token).ConfigureAwait(false);
                        var proof = new byte[32]; await stream.ReadExactlyAsync(proof, timeout.Token).ConfigureAwait(false);
                        if (!CryptographicOperations.FixedTimeEquals(proof, HMACSHA256.HashData(peer.Keys.OwnerToVisitor, binding))) throw new CryptographicException("直连身份校验失败。");
                        peer.Start(client); return;
                    }
                    catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or CryptographicException or ArgumentException) { client.Dispose(); }
                }
            }
            peer.Start(null); await SendMessageAsync(new("relay", Session: peer.Id), token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Net.WebSockets.WebSocketException)
        { peer.Request?.Completion.TrySetException(ex); await DisconnectPeerAsync(peer.Id).ConfigureAwait(false); }
    }
    private readonly ConcurrentDictionary<int, Task> _directAcceptTasks = new();
    private int _directSequence;
    private async Task AcceptDirectAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(token).ConfigureAwait(false);
                if (_directAcceptTasks.Count >= 32) { client.Dispose(); continue; }
                var id = Interlocked.Increment(ref _directSequence);
                var task = AuthenticateDirectAsync(client, token); _directAcceptTasks[id] = task;
                _ = task.ContinueWith(_ => _directAcceptTasks.TryRemove(id, out var ignored), TaskScheduler.Default);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        finally { await Task.WhenAll(_directAcceptTasks.Values).ConfigureAwait(false); }
    }
    private async Task AuthenticateDirectAsync(TcpClient client, CancellationToken token)
    {
        var retained = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var stream = client.GetStream(); var id = new byte[32]; await stream.ReadExactlyAsync(id, timeout.Token).ConfigureAwait(false);
            if (!_peers.TryGetValue(Encoding.ASCII.GetString(id), out var peer) || !peer.IsOwner || peer.Keys is null || peer.Ready) return;
            var nonce = RandomNumberGenerator.GetBytes(32); var binding = nonce.Concat(Convert.FromHexString(peer.Id)).ToArray();
            await stream.WriteAsync(nonce, timeout.Token).ConfigureAwait(false);
            var proof = new byte[32]; await stream.ReadExactlyAsync(proof, timeout.Token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(proof, HMACSHA256.HashData(peer.Keys.VisitorToOwner, binding))) return;
            await stream.WriteAsync(HMACSHA256.HashData(peer.Keys.OwnerToVisitor, binding), timeout.Token).ConfigureAwait(false);
            retained = peer.Start(client);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or CryptographicException) { }
        finally { if (!retained) client.Dispose(); }
    }
    public ISerialPortChannel CreateSerialChannel(string portName, SerialPortSettings settings)
    {
        var remote = RemoteServices.FirstOrDefault(s => s.PortName == portName && s.Service.Kind == SharedServiceKind.Serial) ?? throw new IOException("远端串口已离线。");
        return new RemoteNodeSerial(_peers[remote.SessionId], remote.Service.Id, settings);
    }
    public async Task<DevicePortMapping> MapPortAsync(string sessionId, string serviceId, int localPort, CancellationToken token = default)
    {
        if (!_peers.TryGetValue(sessionId, out var peer) || peer.IsOwner || !peer.Ready) throw new IOException("对端连接尚未建立。");
        var service = peer.Services.FirstOrDefault(s => s.Id == serviceId && s.Kind is SharedServiceKind.Tcp or SharedServiceKind.Udp) ?? throw new IOException("对端未发布此 TCP 或 UDP 服务。");
        if (localPort is < 1 or > 65535) throw new ArgumentException("本机映射端口无效。");
        token.ThrowIfCancellationRequested();
        PortMapping mapping = service.Kind == SharedServiceKind.Udp ? new UdpMapping(peer, service, localPort) : new TcpMapping(peer, service, localPort);
        _mappings[mapping.Info.Id] = mapping; Notify();
        return await Task.FromResult(mapping.Info).ConfigureAwait(false);
    }
    public async Task UnmapPortAsync(string id)
    { if (_mappings.TryRemove(id, out var mapping)) await mapping.DisposeAsync().ConfigureAwait(false); Notify(); }

    private sealed class VisitorRequest(string remoteId, string password) : IDisposable
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string RemoteId { get; } = remoteId;
        public string Password { get; } = password;
        public string? SessionId { get; set; }
        public byte[] Nonce { get; } = RandomNumberGenerator.GetBytes(32);
        private CollaborationKeyPair? _key = CollaborationKeyAgreement.CreateKeyPair();
        public CollaborationKeyPair Key => _key!;
        public CollaborationKeyPair TakeKey() { var key = _key!; _key = null; return key; }
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose() => _key?.Dispose();
    }

    private sealed class Peer : IAsyncDisposable
    {
        public readonly DeviceNode Owner;
        public readonly string Id, RemoteId;
        public readonly bool IsOwner;
        public readonly CollaborationKeyPair Key;
        public readonly byte[] Nonce;
        public byte[]? Secret, Transcript;
        public CollaborationSessionKeys? Keys;
        public CollaborationSecureChannel? Secure;
        public NodeHello? Hello;
        public VisitorRequest? Request;
        public bool AwaitingApproval;
        private readonly CancellationTokenSource _life = new();
        private readonly SemaphoreSlim _disposal = new(1, 1);
        private readonly object _pathGate = new();
        private readonly Channel<NodeRpc> _out = Channel.CreateBounded<NodeRpc>(new BoundedChannelOptions(128) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        private readonly Channel<NodeRpc> _in = Channel.CreateBounded<NodeRpc>(new BoundedChannelOptions(512) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        public readonly ConcurrentDictionary<string, ServiceChannel> Channels = new();
        public readonly ConcurrentDictionary<string, RemoteNodeSerial> Serials = new();
        private readonly ConcurrentDictionary<string, Channel<NodeRpc>> _dataOut = new();
        private readonly ConcurrentDictionary<string, Task> _opening = new();
        private readonly SemaphoreSlim _wake = new(0);
        private int _roundRobin;
        private TcpClient? _direct;
        private NetworkStream? _directStream;
        private Task _writer = Task.CompletedTask, _reader = Task.CompletedTask, _processor = Task.CompletedTask;
        private bool _disposed;
        private long _sent, _received, _lastNotify;
        public bool Ready { get; private set; }
        public string Path { get; private set; } = "协商中";
        public SharedDeviceService[] Services { get; set; } = [];
        public CancellationToken Token => _life.Token;
        public bool HasOpenChannels => !Channels.IsEmpty || Serials.Values.Any(s => s.IsOpen);
        public DevicePeerInfo Info => new(Id, RemoteId, IsOwner, AwaitingApproval ? "等待本机授权" : Ready ? "已连接" : "握手中", Path, Interlocked.Read(ref _sent), Interlocked.Read(ref _received));
        public Peer(DeviceNode owner, string id, string remoteId, bool isOwner, CollaborationKeyPair key, byte[] nonce)
        {
            Owner = owner; Id = id; RemoteId = remoteId; IsOwner = isOwner; Key = key; Nonce = nonce;
            _ = ExpireHandshakeAsync();
        }
        private async Task ExpireHandshakeAsync()
        {
            try { await Task.Delay(TimeSpan.FromSeconds(90), Token).ConfigureAwait(false); if (!Ready) await Owner.DisconnectPeerAsync(Id).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        public void InitializeKeys(byte[] publicKey)
        {
            if (Keys is not null) throw new InvalidDataException("握手重复。");
            Keys = CollaborationKeyAgreement.DeriveSessionKeys(Key.PrivateKey, publicKey, Secret!, Transcript!);
            Secure = Keys.CreateChannel(IsOwner ? CollaborationPeerRole.Owner : CollaborationPeerRole.Visitor);
        }
        public bool Start(TcpClient? direct)
        {
            lock (_pathGate)
            {
                if (Ready || _life.IsCancellationRequested) return false;
                _direct = direct; _directStream = direct?.GetStream(); Path = direct is null ? "公网中继" : "局域网直连"; Ready = true;
                _writer = Task.Run(WriteAsync); _processor = Task.Run(ProcessAsync);
                if (direct is not null) _reader = Task.Run(ReadDirectAsync);
            }
            if (IsOwner) TryQueue(new("services", Services: Owner.PublishedServices()));
            Request?.Completion.TrySetResult(); Owner.Notify(); return true;
        }
        private Channel<NodeRpc> DataQueue(string id) => _dataOut.GetOrAdd(id, _ => Channel.CreateBounded<NodeRpc>(new BoundedChannelOptions(8) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait }));
        public bool TryQueue(NodeRpc rpc)
        {
            if (_life.IsCancellationRequested) throw new IOException("设备会话已断开。");
            var writer = rpc.Type is "data" or "udp" or "close" ? DataQueue(rpc.Channel).Writer : _out.Writer;
            if (!writer.TryWrite(rpc)) { if (rpc.Type is not ("data" or "udp")) Abort("控制发送队列已满。"); return false; }
            _wake.Release(); return true;
        }
        public async Task QueueAsync(NodeRpc rpc, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); if (_life.IsCancellationRequested) throw new IOException("设备会话已断开。");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, Token);
            var writer = rpc.Type is "data" or "udp" or "close" ? DataQueue(rpc.Channel).Writer : _out.Writer;
            try { await writer.WriteAsync(rpc, linked.Token).ConfigureAwait(false); _wake.Release(); }
            catch (ChannelClosedException ex) { throw new IOException("服务通道已关闭。", ex); }
        }
        private bool NextMessage(out NodeRpc? rpc)
        {
            if (_out.Reader.TryRead(out rpc)) return true;
            var queues = _dataOut.OrderBy(q => q.Key, StringComparer.Ordinal).ToArray();
            for (var i = 0; i < queues.Length; i++)
            {
                var entry = queues[(int)((uint)_roundRobin + (uint)i) % queues.Length];
                if (!entry.Value.Reader.TryRead(out rpc)) continue;
                _roundRobin += i + 1; if (rpc.Type == "close") _dataOut.TryRemove(entry.Key, out _); return true;
            }
            rpc = null; return false;
        }
        private async Task WriteAsync()
        {
            try
            {
                while (true)
                {
                    await _wake.WaitAsync(Token).ConfigureAwait(false); if (!NextMessage(out var next)) continue; var rpc = next!;
                    var encoded = JsonSerializer.SerializeToUtf8Bytes(rpc, Json);
                    if (encoded.Length > 32 * 1024) throw new InvalidDataException("服务报文过大。");
                    if (_directStream is not null) await Secure!.SendAsync(_directStream, CollaborationMessageType.Data, encoded, Token).ConfigureAwait(false);
                    else await Owner.SendMessageAsync(new("data", Session: Id, Body: Element(Secure!.Encrypt(CollaborationMessageType.Data, encoded))), Token).ConfigureAwait(false);
                    Interlocked.Add(ref _sent, rpc.Data?.Length ?? 0); NotifyTraffic();
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Net.WebSockets.WebSocketException or CryptographicException or ObjectDisposedException or InvalidOperationException or InvalidDataException) { if (!_life.IsCancellationRequested) Abort(ex.Message); }
        }
        private async Task ReadDirectAsync()
        {
            try
            {
                while (await Secure!.ReceiveAsync(_directStream!, Token).ConfigureAwait(false) is { } frame) Receive(frame.Plaintext);
                if (!_life.IsCancellationRequested) Abort("局域网连接已断开。");
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or CryptographicException or ObjectDisposedException or JsonException) { if (!_life.IsCancellationRequested) Abort(ex.Message); }
        }
        public void Receive(byte[] payload)
        {
            if (payload.Length > 32 * 1024) throw new InvalidDataException("服务报文过大。");
            var rpc = JsonSerializer.Deserialize<NodeRpc>(payload, Json) ?? throw new InvalidDataException("服务报文为空。");
            if (rpc.Data?.Length > 8192) throw new InvalidDataException("服务数据块过大。");
            if (!_in.Writer.TryWrite(rpc)) { Abort("接收队列已满。"); return; }
            Interlocked.Add(ref _received, rpc.Data?.Length ?? 0); NotifyTraffic();
        }
        private void NotifyTraffic()
        {
            var now = Environment.TickCount64; if (now - Interlocked.Read(ref _lastNotify) < 250) return;
            Interlocked.Exchange(ref _lastNotify, now); Owner.Notify();
        }
        private async Task ProcessAsync()
        {
            try { await foreach (var rpc in _in.Reader.ReadAllAsync(Token).ConfigureAwait(false)) await ProcessRpcAsync(rpc).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidDataException) { if (!_life.IsCancellationRequested) Abort(ex.Message); }
        }
        private async Task ProcessRpcAsync(NodeRpc rpc)
        {
            if (rpc.Type is not ("services" or "open" or "opened" or "error" or "data" or "udp" or "close" or "credit" or "serial-configure" or "serial-configured") || rpc.Type != "services" && (rpc.Channel?.Length != 32 || !Guid.TryParseExact(rpc.Channel, "N", out _))) throw new InvalidDataException("服务通道报文无效。");
            switch (rpc.Type)
            {
                case "services":
                    if (IsOwner || rpc.Services is null || rpc.Services.Length > 128) throw new InvalidDataException("服务列表无效。");
                    if (rpc.Services.Any(s => s is null || s.Id?.Length != 32 || !Guid.TryParseExact(s.Id, "N", out _) || s.Name?.Length is not (> 0 and <= 64) || s.Name.Any(char.IsControl) || !Enum.IsDefined(s.Kind))
                        || rpc.Services.Select(s => s.Id).Distinct().Count() != rpc.Services.Length) throw new InvalidDataException("服务定义无效。");
                    Services = rpc.Services; Owner.Notify(); return;
                case "open":
                    if (!IsOwner || Channels.Count >= 32 || rpc.Channel.Length != 32) { await QueueAsync(new("error", rpc.Channel, Error: "通道请求无效或达到上限。")).ConfigureAwait(false); return; }
                    var service = Owner._services.FirstOrDefault(s => s.Enabled && s.Id == rpc.Service);
                    if (service is null) { await QueueAsync(new("error", rpc.Channel, Error: "目标服务未授权或已停用。")).ConfigureAwait(false); return; }
                    var channel = new ServiceChannel(this, rpc.Channel);
                    if (!Channels.TryAdd(rpc.Channel, channel)) { await QueueAsync(new("error", rpc.Channel, Error: "通道已打开。")).ConfigureAwait(false); return; }
                    var task = OpenServiceAsync(channel, service, rpc.Settings); _opening[channel.Id] = task;
                    _ = task.ContinueWith(_ => _opening.TryRemove(channel.Id, out var ignored), TaskScheduler.Default);
                    return;
                case "opened":
                    if (Serials.TryGetValue(rpc.Channel, out var serial)) serial.Acknowledge();
                    if (Channels.TryGetValue(rpc.Channel, out var pending)) pending.Acknowledge(); return;
                case "serial-configure":
                    if (!IsOwner || !Guid.TryParseExact(rpc.Request, "N", out _)) throw new InvalidDataException("串口参数请求无效。");
                    string? configurationError = null;
                    try
                    {
                        if (!Channels.TryGetValue(rpc.Channel, out var configured)) throw new IOException("远端串口未打开。");
                        await configured.ConfigureSerialAsync(rpc.Settings, Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException)
                    { configurationError = ex.Message; }
                    await QueueAsync(new("serial-configured", rpc.Channel, Error: configurationError, Request: rpc.Request)).ConfigureAwait(false); return;
                case "serial-configured":
                    if (IsOwner || !Guid.TryParseExact(rpc.Request, "N", out _)) throw new InvalidDataException("串口参数响应无效。");
                    if (Serials.TryGetValue(rpc.Channel, out var updating)) updating.ConfigurationAcknowledged(rpc.Request!, rpc.Error);
                    return;
                case "error":
                    if (Serials.TryRemove(rpc.Channel, out var failed)) failed.Reject(rpc.Error ?? "远端操作失败。");
                    if (Channels.TryRemove(rpc.Channel, out var rejected)) { rejected.Reject(rpc.Error ?? "远端操作失败。"); await rejected.DisposeAsync().ConfigureAwait(false); }
                    RemoveDataQueue(rpc.Channel);
                    Owner.Fail(rpc.Error ?? "远端操作失败。"); return;
                case "data":
                    if (rpc.Data is null) throw new InvalidDataException("通道数据为空。");
                    if (Serials.TryGetValue(rpc.Channel, out var receiver)) receiver.Receive(rpc.Data);
                    else if (Channels.TryGetValue(rpc.Channel, out var output)) await output.SendAsync(rpc.Data, Token).ConfigureAwait(false);
                    await QueueAsync(new("credit", rpc.Channel)).ConfigureAwait(false);
                    return;
                case "udp":
                    if (rpc.Data is null) throw new InvalidDataException("UDP 报文为空。");
                    if (Channels.TryGetValue(rpc.Channel, out var datagramChannel)) await datagramChannel.SendDatagramPartAsync(rpc, Token).ConfigureAwait(false);
                    await QueueAsync(new("credit", rpc.Channel)).ConfigureAwait(false);
                    return;
                case "credit":
                    if (Serials.TryGetValue(rpc.Channel, out var flowSerial)) flowSerial.ReleaseCredit();
                    if (Channels.TryGetValue(rpc.Channel, out var flowTcp)) flowTcp.ReleaseCredit(); return;
                case "close":
                    if (Serials.TryRemove(rpc.Channel, out var closed)) closed.Reject("远端关闭了串口。");
                    if (Channels.TryRemove(rpc.Channel, out var removed)) await removed.DisposeAsync().ConfigureAwait(false);
                    RemoveDataQueue(rpc.Channel); return;
                default: throw new InvalidDataException("不支持的服务报文。");
            }
        }
        private async Task OpenServiceAsync(ServiceChannel channel, SharedDeviceService service, SerialPortSettings? settings)
        {
            try { await channel.OpenAsync(service, settings, Token).ConfigureAwait(false); if (channel.IsDisposed) throw new OperationCanceledException(); await QueueAsync(new("opened", channel.Id)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or SocketException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or TimeoutException or OperationCanceledException)
            {
                Channels.TryRemove(channel.Id, out _); await channel.DisposeAsync().ConfigureAwait(false);
                if (!Token.IsCancellationRequested)
                { var reason = "打开“" + service.Name + "”失败：" + ex.Message; Owner.Fail(reason); try { await QueueAsync(new("error", channel.Id, Error: reason)).ConfigureAwait(false); } catch (OperationCanceledException) { } }
            }
        }
        private void Abort(string reason)
        {
            if (_life.IsCancellationRequested) return; _life.Cancel(); _direct?.Dispose(); Owner.Fail(reason);
            Request?.Completion.TrySetException(new IOException(reason));
            _ = Task.Run(() => Owner.DisconnectPeerAsync(Id));
        }
        public async ValueTask DisposeAsync()
        {
            await _disposal.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) return; _disposed = true; _life.Cancel(); _direct?.Dispose(); _out.Writer.TryComplete(); _in.Writer.TryComplete();
                foreach (var queue in _dataOut.Values) queue.Writer.TryComplete(); _dataOut.Clear();
                Ready = false; Request?.Completion.TrySetException(new IOException("设备连接已断开。"));
                foreach (var serial in Serials.Values) serial.Reject("设备连接已断开。");
                await Task.WhenAll(_writer, _reader, _processor).ConfigureAwait(false);
                await Task.WhenAll(_opening.Values).ConfigureAwait(false);
                foreach (var channel in Channels.Values) await channel.DisposeAsync().ConfigureAwait(false); Channels.Clear();
                Secure?.Dispose(); Keys?.Dispose(); Key.Dispose(); if (Secret is not null) CryptographicOperations.ZeroMemory(Secret);
            }
            finally { _disposal.Release(); }
        }
        public async Task AddTcpAsync(TcpClient client, string service, CancellationToken token, TcpMapping mapping)
        {
            if (Channels.Count + Serials.Count >= 32) { client.Dispose(); mapping.RecordFailure("此设备连接的服务通道已达 32 条上限。"); return; }
            var channel = new ServiceChannel(this, Guid.NewGuid().ToString("N"));
            if (!Channels.TryAdd(channel.Id, channel)) { client.Dispose(); return; }
            mapping.Owned[channel.Id] = 0;
            channel.OwnedSet = mapping.Owned; channel.Traffic = mapping.RecordTraffic; channel.Failure = mapping.RecordFailure;
            try
            {
                channel.Attach(client); await QueueAsync(new("open", channel.Id, service)).ConfigureAwait(false);
                await channel.Opened.Task.WaitAsync(TimeSpan.FromSeconds(15), token).ConfigureAwait(false); channel.StartRead();
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException) { if (!token.IsCancellationRequested) { Owner.Fail(ex.Message); mapping.RecordFailure(ex.Message); } await CloseChannelAsync(channel.Id).ConfigureAwait(false); }
        }
        public async Task<ServiceChannel> AddUdpAsync(string service, UdpMapping mapping, IPEndPoint endpoint, CancellationToken token)
        {
            if (Channels.Count + Serials.Count >= 32) throw new IOException("此设备连接的服务通道已达 32 条上限。");
            var channel = new ServiceChannel(this, Guid.NewGuid().ToString("N"));
            if (!Channels.TryAdd(channel.Id, channel)) throw new IOException("创建 UDP 通道失败。");
            mapping.Owned[channel.Id] = 0;
            channel.OwnedSet = mapping.Owned; channel.Traffic = mapping.RecordTraffic; channel.Failure = mapping.RecordFailure;
            channel.AttachUdp((data, cancellation) => mapping.SendReplyAsync(data, endpoint, cancellation));
            try
            {
                await QueueAsync(new("open", channel.Id, service), token).ConfigureAwait(false);
                await channel.Opened.Task.WaitAsync(TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
                return channel;
            }
            catch { await CloseChannelAsync(channel.Id).ConfigureAwait(false); throw; }
        }
        private void RemoveDataQueue(string id)
        {
            if (_dataOut.TryRemove(id, out var queue)) queue.Writer.TryComplete();
        }
        public async Task CloseChannelAsync(string id)
        {
            if (Channels.TryRemove(id, out var channel)) await channel.DisposeAsync().ConfigureAwait(false);
            if (!Token.IsCancellationRequested) { try { await QueueAsync(new("close", id)).ConfigureAwait(false); } catch (Exception ex) when (ex is IOException or OperationCanceledException) { } }
        }
    }

    private sealed class ServiceChannel : IAsyncDisposable
    {
        private readonly Peer peer;
        private readonly string id;
        public ServiceChannel(Peer peer, string id)
        {
            this.peer = peer; this.id = id;
            _life = CancellationTokenSource.CreateLinkedTokenSource(peer.Token); Token = _life.Token;
        }
        public string Id => id;
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public CancellationToken Token { get; }
        public ConcurrentDictionary<string, byte>? OwnedSet { get; set; }
        public Action<int, bool>? Traffic { get; set; }
        public Action<string>? Failure { get; set; }
        private ISerialPortChannel? _serial;
        private TcpClient? _tcp;
        private NetworkStream? _tcpStream;
        private UdpClient? _udp;
        private Func<byte[], CancellationToken, Task>? _datagramSink;
        private bool _isUdp;
        private byte[]? _datagram;
        private int _datagramOffset;
        private readonly CancellationTokenSource _life;
        private readonly SemaphoreSlim _credits = new(8, 8);
        private readonly SemaphoreSlim _datagramSend = new(1, 1);
        private readonly SemaphoreSlim _disposal = new(1, 1);
        private Task _read = Task.CompletedTask;
        private int _disposed;
        public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Acknowledge() => Opened.TrySetResult();
        public void Reject(string reason) { Failure?.Invoke(reason); Opened.TrySetException(new IOException(reason)); }
        public void ReleaseCredit() { try { _credits.Release(); } catch (SemaphoreFullException) { throw new InvalidDataException("通道流控额度无效。"); } }
        public void Attach(TcpClient client) { _tcp = client; _tcpStream = client.GetStream(); }
        public void AttachUdp(Func<byte[], CancellationToken, Task> sink) { _isUdp = true; _datagramSink = sink; }
        public async Task OpenAsync(SharedDeviceService service, SerialPortSettings? settings, CancellationToken token)
        {
            if (service.Kind == SharedServiceKind.Serial)
            {
                if (settings is null || !SerialBridgeControlCodec.TryDecodeSettings(SerialBridgeControlCodec.EncodeSettings(settings), out _)) throw new ArgumentException("串口参数无效。");
                var actual = new SerialPortSettings { PortName = service.PortName, BaudRate = settings.BaudRate, DataBits = settings.DataBits, Parity = settings.Parity, StopBits = settings.StopBits, Handshake = settings.Handshake, DtrEnable = settings.DtrEnable, RtsEnable = settings.RtsEnable };
                _serial = peer.Owner._serial is ISerialPortSharing sharing ? sharing.CreateSharedChannel(actual) : throw new InvalidOperationException("串口共享资源管理器不可用。");
                _serial.DataReceived += SerialReceived;
                await Task.Run(() => _serial.OpenAsync(token), token).ConfigureAwait(false);
            }
            else if (service.Kind == SharedServiceKind.Udp)
            {
                _isUdp = true;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, Token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var addresses = await Dns.GetHostAddressesAsync(service.TargetHost, timeout.Token).ConfigureAwait(false);
                var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault() ?? throw new IOException("UDP 目标地址无法解析。");
                _udp = new UdpClient(address.AddressFamily);
                _udp.Connect(new IPEndPoint(address, service.TargetPort)); StartRead();
            }
            else
            {
                _tcp = new TcpClient { NoDelay = true };
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await _tcp.ConnectAsync(service.TargetHost, service.TargetPort, timeout.Token).ConfigureAwait(false); _tcpStream = _tcp.GetStream(); StartRead();
            }
        }
        public Task ConfigureSerialAsync(SerialPortSettings? settings, CancellationToken token)
        {
            if (settings is null || !SerialBridgeControlCodec.TryDecodeSettings(SerialBridgeControlCodec.EncodeSettings(settings), out _)) throw new ArgumentException("串口参数无效。");
            if (_serial is not ISerialPortConfiguration configuration) throw new NotSupportedException("对端串口不支持在线修改参数。");
            var actual = new SerialPortSettings { PortName = _serial.Settings.PortName, BaudRate = settings.BaudRate, DataBits = settings.DataBits,
                Parity = settings.Parity, StopBits = settings.StopBits, Handshake = settings.Handshake, DtrEnable = settings.DtrEnable, RtsEnable = settings.RtsEnable };
            return Task.Run(() => configuration.ConfigureAsync(actual, token), token);
        }
        private void SerialReceived(object? sender, ReadOnlyMemory<byte> bytes)
        {
            if (IsDisposed) return;
            try
            {
                for (var offset = 0; offset < bytes.Length; offset += 8192)
                {
                    if (_credits.Wait(0) && peer.TryQueue(new("data", Id, Data: bytes.Slice(offset, Math.Min(8192, bytes.Length - offset)).ToArray()))) continue;
                    var reason = "串口接收超过通道缓冲容量，已关闭此串口以避免丢失数据。";
                    peer.Owner.Fail(reason); _ = Task.Run(async () => { try { await peer.QueueAsync(new("error", Id, Error: reason)).ConfigureAwait(false); await peer.CloseChannelAsync(Id).ConfigureAwait(false); } catch (Exception ex) when (ex is IOException or OperationCanceledException) { } }); break;
                }
            }
            catch (IOException) { }
        }
        public void StartRead() => _read = Task.Run(ReadAsync);
        private async Task ReadAsync()
        {
            try
            {
                if (_udp is not null)
                {
                    while (!_life.IsCancellationRequested)
                    {
                        var datagram = await _udp.ReceiveAsync(Token).ConfigureAwait(false);
                        await QueueDatagramAsync(datagram.Buffer, Token).ConfigureAwait(false);
                    }
                    return;
                }
                var bytes = new byte[8192];
                while (!_life.IsCancellationRequested)
                {
                    await _credits.WaitAsync(Token).ConfigureAwait(false);
                    var count = await _tcpStream!.ReadAsync(bytes, Token).ConfigureAwait(false); if (count == 0) break;
                    Traffic?.Invoke(count, true);
                    await peer.QueueAsync(new("data", Id, Data: bytes[..count]), Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                if (_isUdp && !_life.IsCancellationRequested)
                {
                    peer.Owner.Fail("UDP 目标收发失败：" + ex.Message);
                    try { await peer.QueueAsync(new("error", Id, Error: "UDP 目标收发失败：" + ex.Message)).ConfigureAwait(false); }
                    catch (Exception failure) when (failure is IOException or OperationCanceledException) { }
                }
            }
            finally { if (Volatile.Read(ref _disposed) == 0) _ = Task.Run(() => peer.CloseChannelAsync(Id)); }
        }
        public async Task QueueDatagramAsync(byte[] data, CancellationToken token)
        {
            if (!_isUdp || data.Length > 65507) throw new IOException("UDP 报文不得超过 65,507 字节。");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, Token);
            await _datagramSend.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                // Ordered fragments keep one bounded reassembly buffer per channel, including empty datagrams.
                var offset = 0;
                do
                {
                    var count = Math.Min(8192, data.Length - offset);
                    await _credits.WaitAsync(linked.Token).ConfigureAwait(false);
                    await peer.QueueAsync(new("udp", Id, Data: data.AsSpan(offset, count).ToArray(), DatagramLength: data.Length, DatagramOffset: offset), linked.Token).ConfigureAwait(false);
                    offset += count;
                } while (offset < data.Length);
                Traffic?.Invoke(data.Length, true);
            }
            finally { _datagramSend.Release(); }
        }
        public async Task SendDatagramPartAsync(NodeRpc rpc, CancellationToken token)
        {
            if (!_isUdp || rpc.Data is null || rpc.DatagramLength is < 0 or > 65507 || rpc.DatagramOffset < 0
                || rpc.DatagramOffset > rpc.DatagramLength || rpc.Data.Length > rpc.DatagramLength - rpc.DatagramOffset
                || rpc.Data.Length == 0 && rpc.DatagramLength != 0) throw new InvalidDataException("UDP 分帧参数无效。");
            if (_datagram is null)
            {
                if (rpc.DatagramOffset != 0) throw new InvalidDataException("UDP 报文缺少起始分帧。");
                _datagram = new byte[rpc.DatagramLength]; _datagramOffset = 0;
            }
            if (_datagram.Length != rpc.DatagramLength || rpc.DatagramOffset != _datagramOffset) throw new InvalidDataException("UDP 分帧顺序无效。");
            rpc.Data.CopyTo(_datagram, _datagramOffset); _datagramOffset += rpc.Data.Length;
            if (_datagramOffset != _datagram.Length) return;
            var complete = _datagram; _datagram = null; _datagramOffset = 0;
            await WriteDatagramAsync(complete, token).ConfigureAwait(false);
        }
        private async Task WriteDatagramAsync(byte[] data, CancellationToken token)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, Token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
                if (_datagramSink is not null) await _datagramSink(data, timeout.Token).ConfigureAwait(false);
                else if (_udp is not null) await _udp.SendAsync(data, timeout.Token).ConfigureAwait(false);
                else throw new IOException("UDP 目标尚未打开。");
                Traffic?.Invoke(data.Length, false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                if (!token.IsCancellationRequested && !_life.IsCancellationRequested)
                {
                    var reason = "UDP 收发失败：" + ex.Message;
                    peer.Owner.Fail(reason); await peer.QueueAsync(new("error", Id, Error: reason)).ConfigureAwait(false);
                    await peer.CloseChannelAsync(Id).ConfigureAwait(false);
                }
            }
        }
        public async Task SendAsync(byte[] data, CancellationToken token)
        {
            if (_isUdp) throw new InvalidDataException("UDP 通道不能接收流式数据。");
            try
            {
                if (_serial is not null) await Task.Run(() => _serial.SendAsync(data, token), token).ConfigureAwait(false);
                else if (_tcp is not null) await WriteTargetAsync(data, token).ConfigureAwait(false);
                Traffic?.Invoke(data.Length, false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or SocketException)
            { peer.Owner.Fail(ex.Message); await peer.QueueAsync(new("error", Id, Error: "远端收发失败：" + ex.Message)).ConfigureAwait(false); await peer.CloseChannelAsync(Id).ConfigureAwait(false); }
        }
        private async Task WriteTargetAsync(byte[] data, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, Token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try { await _tcpStream!.WriteAsync(data, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException ex) when (!token.IsCancellationRequested) { throw new TimeoutException("目标 TCP 写入超时或通道已关闭。", ex); }
        }
        public async ValueTask DisposeAsync()
        {
            await _disposal.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                OwnedSet?.TryRemove(Id, out _);
                Opened.TrySetException(new IOException("服务通道已关闭。"));
                _life.Cancel(); _tcp?.Dispose(); _udp?.Dispose();
                if (_serial is not null) { _serial.DataReceived -= SerialReceived; await _serial.DisposeAsync().ConfigureAwait(false); }
                await _read.ConfigureAwait(false); _life.Dispose();
            }
            finally { _disposal.Release(); }
        }
    }

    private sealed class RemoteNodeSerial(Peer peer, string service, SerialPortSettings settings) : ISerialPortChannel, ISerialPortFaultNotifier, ISerialPortConfiguration
    {
        private readonly SemaphoreSlim _operations = new(1, 1);
        private TaskCompletionSource? _ready;
        private TaskCompletionSource? _configurationReady;
        private string? _configurationId;
        private CancellationTokenSource? _life;
        private readonly SemaphoreSlim _credits = new(8, 8);
        private bool _disposed;
        public string Id { get; private set; } = Guid.NewGuid().ToString("N");
        public SerialPortSettings Settings => settings;
        public bool IsOpen { get; private set; }
        public event EventHandler<ReadOnlyMemory<byte>>? DataReceived;
        public event EventHandler<string>? Faulted;
        public void Acknowledge() { IsOpen = true; _ready?.TrySetResult(); }
        public void ReleaseCredit() { try { _credits.Release(); } catch (SemaphoreFullException) { throw new InvalidDataException("串口流控额度无效。"); } }
        public void Reject(string reason) { var wasOpen = IsOpen; IsOpen = false; _life?.Cancel(); _ready?.TrySetException(new IOException(reason)); _configurationReady?.TrySetException(new IOException(reason)); if (wasOpen) Faulted?.Invoke(this, reason); }
        public void ConfigurationAcknowledged(string request, string? error)
        { if (request == _configurationId) { if (error is null) _configurationReady?.TrySetResult(); else _configurationReady?.TrySetException(new IOException(error)); } }
        public void Receive(byte[] bytes) { if (IsOpen || _ready is not null) DataReceived?.Invoke(this, bytes); }
        public async Task OpenAsync(CancellationToken cancellationToken = default)
        {
            await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this); if (IsOpen) return;
                if (peer.Channels.Count + peer.Serials.Count >= 32) throw new IOException("此设备连接的服务通道已达 32 条上限。");
                _life?.Dispose(); _life = CancellationTokenSource.CreateLinkedTokenSource(peer.Token);
                Id = Guid.NewGuid().ToString("N");
                while (_credits.Wait(0)) { } _credits.Release(8);
                _ready = new(TaskCreationOptions.RunContinuationsAsynchronously); peer.Serials[Id] = this;
                try
                {
                    var wire = new SerialPortSettings { PortName = "COM1", BaudRate = settings.BaudRate, DataBits = settings.DataBits, Parity = settings.Parity, StopBits = settings.StopBits, Handshake = settings.Handshake, DtrEnable = settings.DtrEnable, RtsEnable = settings.RtsEnable };
                    await peer.QueueAsync(new("open", Id, service, Settings: wire)).ConfigureAwait(false);
                    await _ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
                }
                catch { try { await peer.QueueAsync(new("close", Id)).ConfigureAwait(false); } catch (IOException) { } peer.Serials.TryRemove(Id, out _); IsOpen = false; throw; }
                finally { _ready = null; }
            }
            finally { _operations.Release(); }
        }
        public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            if (!IsOpen) throw new IOException("远端串口未打开。");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _life!.Token);
            for (var offset = 0; offset < data.Length; offset += 8192) { await _credits.WaitAsync(linked.Token).ConfigureAwait(false); await peer.QueueAsync(new("data", Id, Data: data.Slice(offset, Math.Min(8192, data.Length - offset)).ToArray()), linked.Token).ConfigureAwait(false); }
        }
        public async Task ConfigureAsync(SerialPortSettings next, CancellationToken cancellationToken = default)
        {
            await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!IsOpen || _life is null) throw new IOException("远端串口未打开。");
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _life.Token);
                var wire = new SerialPortSettings { PortName = "COM1", BaudRate = next.BaudRate, DataBits = next.DataBits, Parity = next.Parity, StopBits = next.StopBits, Handshake = next.Handshake, DtrEnable = next.DtrEnable, RtsEnable = next.RtsEnable };
                _configurationId = Guid.NewGuid().ToString("N");
                _configurationReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
                await peer.QueueAsync(new("serial-configure", Id, Settings: wire, Request: _configurationId), linked.Token).ConfigureAwait(false);
                await _configurationReady.Task.WaitAsync(TimeSpan.FromSeconds(10), linked.Token).ConfigureAwait(false);
                settings = next;
            }
            finally { _configurationReady = null; _configurationId = null; _operations.Release(); }
        }
        public Task CloseAsync(CancellationToken cancellationToken = default)
        {
            IsOpen = false; _life?.Cancel(); peer.Serials.TryRemove(Id, out _);
            return peer.Ready && !peer.Token.IsCancellationRequested ? peer.QueueAsync(new("close", Id), cancellationToken) : Task.CompletedTask;
        }
        public async ValueTask DisposeAsync() { _disposed = true; await CloseAsync().ConfigureAwait(false); _life?.Dispose(); _life = null; }
    }

    private abstract class PortMapping : IAsyncDisposable
    {
        private readonly SemaphoreSlim _disposal = new(1, 1);
        private bool _disposed;
        public abstract DevicePortMapping Info { get; }
        protected abstract ValueTask DisposeCoreAsync();
        public async ValueTask DisposeAsync()
        {
            await _disposal.WaitAsync().ConfigureAwait(false);
            try { if (_disposed) return; _disposed = true; await DisposeCoreAsync().ConfigureAwait(false); }
            finally { _disposal.Release(); }
        }
    }

    private sealed class TcpMapping : PortMapping
    {
        private readonly Peer _peer;
        private readonly SharedDeviceService _service;
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _life;
        private readonly Task _accept;
        private readonly ConcurrentDictionary<int, Task> _connections = new();
        private readonly ConcurrentDictionary<string, byte> _owned = new();
        private int _sequence;
        private DevicePortMapping _info;
        private long _sent, _received;
        private string _error = "";
        public ConcurrentDictionary<string, byte> Owned => _owned;
        public override DevicePortMapping Info => _info with { SentBytes = Interlocked.Read(ref _sent), ReceivedBytes = Interlocked.Read(ref _received), Error = _error };
        public void RecordTraffic(int count, bool sent) { if (sent) Interlocked.Add(ref _sent, count); else Interlocked.Add(ref _received, count); }
        public void RecordFailure(string error) { _error = error; _peer.Owner.Notify(); }
        public TcpMapping(Peer peer, SharedDeviceService service, int port)
        {
            _peer = peer; _service = service; _life = CancellationTokenSource.CreateLinkedTokenSource(peer.Token);
            _listener = new TcpListener(IPAddress.Loopback, port); _listener.Start();
            _info = new(Guid.NewGuid().ToString("N"), peer.Id, service.Name, port); _accept = AcceptAsync();
        }
        private async Task AcceptAsync()
        {
            try
            {
                while (!_life.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_life.Token).ConfigureAwait(false); client.NoDelay = true;
                    if (_connections.Count >= 64) { client.Dispose(); continue; }
                    var id = Interlocked.Increment(ref _sequence); var task = _peer.AddTcpAsync(client, _service.Id, _life.Token, this); _connections[id] = task;
                    _ = task.ContinueWith(_ => _connections.TryRemove(id, out var ignored), TaskScheduler.Default);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
            finally { _listener.Stop(); }
        }
        protected override async ValueTask DisposeCoreAsync() { _life.Cancel(); _listener.Stop(); await _accept.ConfigureAwait(false); await Task.WhenAll(_connections.Values).ConfigureAwait(false); foreach (var id in _owned.Keys) await _peer.CloseChannelAsync(id).ConfigureAwait(false); _life.Dispose(); }
    }
}
