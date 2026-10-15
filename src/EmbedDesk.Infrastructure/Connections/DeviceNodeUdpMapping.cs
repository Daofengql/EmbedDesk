using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace EmbedDesk.Infrastructure.Connections;

public partial class DeviceNode
{
    private sealed class UdpMapping : PortMapping
    {
        private readonly Peer _peer;
        private readonly SharedDeviceService _service;
        private readonly UdpClient _listener;
        private readonly CancellationTokenSource _life;
        private readonly ConcurrentDictionary<IPEndPoint, Association> _associations = new();
        private readonly DevicePortMapping _info;
        private readonly Task _receive, _expire;
        private long _sent, _received, _dropped;
        private string _error = string.Empty;
        public ConcurrentDictionary<string, byte> Owned { get; } = new();
        public override DevicePortMapping Info => _info with
        {
            SentBytes = Interlocked.Read(ref _sent), ReceivedBytes = Interlocked.Read(ref _received),
            DroppedDatagrams = Interlocked.Read(ref _dropped), Error = _error
        };

        public UdpMapping(Peer peer, SharedDeviceService service, int port)
        {
            _peer = peer; _service = service;
            _life = CancellationTokenSource.CreateLinkedTokenSource(peer.Token);
            _listener = new UdpClient(AddressFamily.InterNetwork);
            try { _listener.ExclusiveAddressUse = true; _listener.Client.Bind(new IPEndPoint(IPAddress.Loopback, port)); }
            catch { _listener.Dispose(); _life.Dispose(); throw; }
            _info = new(Guid.NewGuid().ToString("N"), peer.Id, service.Name, port, Kind: SharedServiceKind.Udp);
            _receive = ReceiveAsync(); _expire = ExpireAsync();
        }

        public void RecordTraffic(int count, bool sent)
        { if (sent) Interlocked.Add(ref _sent, count); else Interlocked.Add(ref _received, count); }
        public void RecordFailure(string reason) { _error = reason; _peer.Owner.Notify(); }

        private async Task ReceiveAsync()
        {
            try
            {
                while (!_life.IsCancellationRequested)
                {
                    var packet = await _listener.ReceiveAsync(_life.Token).ConfigureAwait(false);
                    if (packet.Buffer.Length > 65507) { Interlocked.Increment(ref _dropped); continue; }
                    if (!_associations.TryGetValue(packet.RemoteEndPoint, out var association))
                    {
                        // Each sender has its own connected target socket and reply path.
                        if (_associations.Count >= 32) { Interlocked.Increment(ref _dropped); continue; }
                        association = new Association(this, packet.RemoteEndPoint);
                        _associations[packet.RemoteEndPoint] = association; association.Start();
                    }
                    if (!association.Enqueue(packet.Buffer)) Interlocked.Increment(ref _dropped);
                }
            }
            catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException or OperationCanceledException)
            { if (!_life.IsCancellationRequested) { RecordFailure("UDP 本机监听失败：" + ex.Message); _life.Cancel(); } }
        }

        private async Task ExpireAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), _life.Token).ConfigureAwait(false);
                    foreach (var association in _associations.Values)
                        if (!association.IsSending && Environment.TickCount64 - association.LastActivity >= 60_000) association.Cancel();
                }
            }
            catch (OperationCanceledException) { }
        }

        public async Task SendReplyAsync(byte[] data, IPEndPoint endpoint, CancellationToken token)
        {
            if (_associations.TryGetValue(endpoint, out var association)) association.Touch();
            await _listener.SendAsync(data, endpoint, token).ConfigureAwait(false);
        }

        protected override async ValueTask DisposeCoreAsync()
        {
            _life.Cancel(); _listener.Dispose();
            await Task.WhenAll(_receive, _expire).ConfigureAwait(false);
            await Task.WhenAll(_associations.Values.Select(a => a.Worker)).ConfigureAwait(false);
            foreach (var id in Owned.Keys) await _peer.CloseChannelAsync(id).ConfigureAwait(false);
            _life.Dispose();
        }

        private sealed class Association
        {
            private readonly UdpMapping _mapping;
            private readonly IPEndPoint _endpoint;
            private readonly CancellationTokenSource _life;
            private readonly Channel<byte[]> _packets = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8)
            { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
            private long _lastActivity = Environment.TickCount64;
            private int _sending;
            public bool IsSending => Volatile.Read(ref _sending) != 0;
            public long LastActivity => Interlocked.Read(ref _lastActivity);
            public Task Worker { get; private set; } = Task.CompletedTask;
            public Association(UdpMapping mapping, IPEndPoint endpoint)
            { _mapping = mapping; _endpoint = endpoint; _life = CancellationTokenSource.CreateLinkedTokenSource(mapping._life.Token); }
            public void Start() => Worker = Task.Run(RunAsync);
            public void Touch() => Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);
            public bool Enqueue(byte[] packet) { Touch(); return _packets.Writer.TryWrite(packet); }
            public void Cancel() { try { _life.Cancel(); } catch (ObjectDisposedException) { } }
            private async Task RunAsync()
            {
                ServiceChannel? channel = null;
                try
                {
                    Volatile.Write(ref _sending, 1);
                    channel = await _mapping._peer.AddUdpAsync(_mapping._service.Id, _mapping, _endpoint, _life.Token).ConfigureAwait(false);
                    Touch(); Volatile.Write(ref _sending, 0);
                    using var active = CancellationTokenSource.CreateLinkedTokenSource(_life.Token, channel.Token);
                    await foreach (var packet in _packets.Reader.ReadAllAsync(active.Token).ConfigureAwait(false))
                    {
                        Touch(); Volatile.Write(ref _sending, 1);
                        await channel.QueueDatagramAsync(packet, active.Token).ConfigureAwait(false);
                        Touch(); Volatile.Write(ref _sending, 0);
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException or SocketException)
                { if (!_life.IsCancellationRequested && channel?.IsDisposed != true) _mapping.RecordFailure(ex.Message); }
                finally
                {
                    Volatile.Write(ref _sending, 0);
                    _packets.Writer.TryComplete();
                    if (channel is not null) await _mapping._peer.CloseChannelAsync(channel.Id).ConfigureAwait(false);
                    _mapping._associations.TryRemove(_endpoint, out _); _life.Dispose();
                }
            }
        }
    }
}
