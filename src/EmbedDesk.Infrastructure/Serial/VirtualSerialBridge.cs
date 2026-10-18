using System.Threading.Channels;
using EmbedDesk.Core.Abstractions;
using EmbedDesk.Core.Models;

namespace EmbedDesk.Infrastructure.Serial;

/// <summary>Bridges an optional system COM pair to the existing authenticated device channel.</summary>
public sealed class VirtualSerialBridge : IAsyncDisposable
{
    private const int BlockSize = 8192;
    private readonly IVirtualSerialEndpoint _endpoint;
    private readonly Func<SerialPortSettings, ISerialPortChannel> _createRemote;
    private readonly Func<bool> _connected;
    private readonly CancellationTokenSource _life = new();
    private readonly SemaphoreSlim _disposal = new(1, 1);
    private readonly object _readySync = new();
    private readonly Task _runner;
    private Session? _session;
    private string _status = "等待本机软件打开串口", _error = string.Empty;
    private long _sent, _received;
    private bool _disposed;
    public bool IsRunning => !_runner.IsCompleted;
    public string Status => Volatile.Read(ref _status);
    public string Error => Volatile.Read(ref _error);
    public long SentBytes => Interlocked.Read(ref _sent);
    public long ReceivedBytes => Interlocked.Read(ref _received);

    public VirtualSerialBridge(IVirtualSerialEndpoint endpoint, Func<SerialPortSettings, ISerialPortChannel> createRemote, Func<bool> connected)
    { _endpoint = endpoint; _createRemote = createRemote; _connected = connected; _runner = Task.Run(RunAsync); }

    private async Task RunAsync()
    {
        try
        {
            while (!_life.IsCancellationRequested)
            {
                if (!_connected()) throw new IOException("设备连接已断开，请重新连接后启用映射。");
                var state = _endpoint.GetPeerState();
                var session = Volatile.Read(ref _session);
                if (session is not null && (!state.IsOpen || session.Token.IsCancellationRequested))
                {
                    await CloseRemoteAsync().ConfigureAwait(false);
                    _endpoint.Discard();
                    _status = "等待本机软件打开串口";
                }
                else if (session is null && state.IsOpen)
                {
                    _status = "正在打开远端串口…";
                    StartRemote(state);
                }
                await Task.Delay(20, _life.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { }
        catch (OperationCanceledException) { RememberError("设备连接已取消或断开。"); }
        catch (Exception ex) { RememberError(ex.Message); }
        finally
        {
            try { await CloseRemoteAsync().ConfigureAwait(false); }
            catch (Exception ex) { RememberError(ex.Message); }
            try { await _endpoint.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { RememberError(ex.Message); }
            _status = Error.Length > 0 ? "失败：" + Error : "未启用";
        }
    }

    private void StartRemote(VirtualSerialPeerState state)
    {
        var session = new Session(_createRemote(state.ToSettings()), _life.Token);
        Volatile.Write(ref _session, session);
        session.Received = (_, bytes) => Received(session, bytes);
        session.Faulted = (_, error) =>
        {
            if (IsCurrent(session) && !session.Token.IsCancellationRequested) Fail(error);
        };
        session.Remote.DataReceived += session.Received;
        if (session.Remote is ISerialPortFaultNotifier notifier) notifier.Faulted += session.Faulted;
        // Receive must run independently: SendAsync/OpenAsync may raise a synchronous echo.
        session.ReceivePump = Task.Run(() => ReceiveAsync(session));
        session.SendPump = Task.Run(() => SendAsync(session, state));
    }

    private bool IsCurrent(Session session) => ReferenceEquals(Volatile.Read(ref _session), session);

    private async Task SendAsync(Session session, VirtualSerialPeerState previous)
    {
        var buffer = new byte[BlockSize];
        var ready = false;
        try
        {
            await session.Remote.OpenAsync(session.Token).ConfigureAwait(false);
            while (!session.Token.IsCancellationRequested && IsCurrent(session))
            {
                var state = _endpoint.GetPeerState();
                if (!state.IsOpen) { session.Cancel(); return; }
                if (state != previous)
                {
                    await ConfigureAsync(session, state).ConfigureAwait(false);
                    previous = state;
                }
                if (!ready) lock (_readySync)
                {
                    if (!IsCurrent(session) || session.Token.IsCancellationRequested) return;
                    _endpoint.SetReady(true);
                    ready = true;
                }
                _status = $"转发中 · {state.BaudRate} / {state.DataBits} / {state.Parity} / {state.StopBits}";
                var count = _endpoint.Read(buffer); // bounded driver read timeout, runs away from the UI
                session.Token.ThrowIfCancellationRequested();
                if (count > 0)
                {
                    var latest = _endpoint.GetPeerState();
                    if (!latest.IsOpen) { session.Cancel(); return; }
                    if (latest != previous)
                    {
                        await ConfigureAsync(session, latest).ConfigureAwait(false);
                        previous = latest;
                    }
                    await session.Remote.SendAsync(buffer.AsMemory(0, count), session.Token).ConfigureAwait(false);
                    Interlocked.Add(ref _sent, count);
                }
                else await Task.Delay(1, session.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (session.Token.IsCancellationRequested) { }
        catch (OperationCanceledException) { Fail("设备连接已取消或断开。"); }
        catch (Exception ex) { Fail(ex.Message); }
    }

    private static Task ConfigureAsync(Session session, VirtualSerialPeerState state) =>
        session.Remote is ISerialPortConfiguration configuration
            ? configuration.ConfigureAsync(state.ToSettings(), session.Token)
            : throw new NotSupportedException("对端不支持在线修改串口参数。");

    private void Received(Session session, ReadOnlyMemory<byte> bytes)
    {
        // Serialize callbacks so a blocked burst retains its byte order. Every opening owns
        // its queue/token, including callbacks captured by the old remote before unsubscribe.
        lock (session.ReceiveSync)
        {
            if (!IsCurrent(session) || session.Token.IsCancellationRequested) return;
            try
            {
                for (var offset = 0; offset < bytes.Length; offset += BlockSize)
                {
                    var block = bytes.Slice(offset, Math.Min(BlockSize, bytes.Length - offset)).ToArray();
                    session.Incoming.Writer.WriteAsync(block, session.Token).AsTask().GetAwaiter().GetResult();
                }
            }
            catch (OperationCanceledException) when (session.Token.IsCancellationRequested) { }
            catch (ChannelClosedException) when (session.Token.IsCancellationRequested) { }
            catch (Exception ex) { Fail(ex.Message); }
        }
    }

    private async Task ReceiveAsync(Session session)
    {
        try
        {
            await foreach (var bytes in session.Incoming.Reader.ReadAllAsync(session.Token).ConfigureAwait(false))
            {
                session.Token.ThrowIfCancellationRequested();
                if (!IsCurrent(session)) return;
                if (!_endpoint.GetPeerState().IsOpen) { session.Cancel(); return; }
                _endpoint.Write(bytes); // bounded native write timeout; join before reopening the endpoint
                Interlocked.Add(ref _received, bytes.Length);
            }
        }
        catch (OperationCanceledException) when (session.Token.IsCancellationRequested) { }
        catch (Exception ex) { Fail(ex.Message); } // retain real write errors even during cancellation
    }

    private void RememberError(string error) => Interlocked.CompareExchange(ref _error, error, string.Empty);
    private void Fail(string error) { RememberError(error); _life.Cancel(); }

    private async Task CloseRemoteAsync()
    {
        var session = Interlocked.Exchange(ref _session, null);
        session?.Cancel(); // unblock credits and receive callbacks before disposing the remote
        Exception? failure = null;
        try { lock (_readySync) _endpoint.SetReady(false); }
        catch (Exception ex) { failure = ex; }
        if (session is not null)
        {
            try
            {
                session.Remote.DataReceived -= session.Received;
                if (session.Remote is ISerialPortFaultNotifier notifier) notifier.Faulted -= session.Faulted;
                session.Incoming.Writer.TryComplete();
                await Task.WhenAll(session.SendPump, session.ReceivePump).ConfigureAwait(false);
            }
            catch (Exception ex) { failure ??= ex; }
            try { await session.Remote.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { failure ??= ex; }
            finally { session.Life.Dispose(); }
        }
        if (failure is not null) throw failure;
    }

    public async ValueTask DisposeAsync()
    {
        await _disposal.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true; _life.Cancel(); await _runner.ConfigureAwait(false); _life.Dispose();
        }
        finally { _disposal.Release(); }
    }

    private sealed class Session
    {
        private int _cancelled;
        public ISerialPortChannel Remote { get; }
        public CancellationTokenSource Life { get; }
        public CancellationToken Token { get; }
        public Channel<byte[]> Incoming { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        public object ReceiveSync { get; } = new();
        public EventHandler<ReadOnlyMemory<byte>>? Received { get; set; }
        public EventHandler<string>? Faulted { get; set; }
        public Task ReceivePump { get; set; } = Task.CompletedTask;
        public Task SendPump { get; set; } = Task.CompletedTask;
        public Session(ISerialPortChannel remote, CancellationToken token)
        { Remote = remote; Life = CancellationTokenSource.CreateLinkedTokenSource(token); Token = Life.Token; }
        public void Cancel() { if (Interlocked.Exchange(ref _cancelled, 1) == 0) Life.Cancel(); }
    }
}
