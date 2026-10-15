using System.ComponentModel;
using System.Threading.Channels;
using EmbedDesk.Core.Abstractions;
using EmbedDesk.Core.Models;

namespace EmbedDesk.Infrastructure.Serial;

/// <summary>Bridges an optional system COM pair to the existing authenticated device channel.</summary>
public sealed class VirtualSerialBridge : IAsyncDisposable
{
    private readonly IVirtualSerialEndpoint _endpoint;
    private readonly Func<SerialPortSettings, ISerialPortChannel> _createRemote;
    private readonly Func<bool> _connected;
    private readonly CancellationTokenSource _life = new();
    private readonly Channel<byte[]> _incoming = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly SemaphoreSlim _disposal = new(1, 1);
    private readonly Task _runner;
    private ISerialPortChannel? _remote;
    private string _status = "等待本机软件打开串口", _error = string.Empty;
    private long _sent, _received;
    private bool _disposed;
    public bool IsRunning => !_runner.IsCompleted;
    public string Status => _status;
    public string Error => _error;
    public long SentBytes => Interlocked.Read(ref _sent);
    public long ReceivedBytes => Interlocked.Read(ref _received);

    public VirtualSerialBridge(IVirtualSerialEndpoint endpoint, Func<SerialPortSettings, ISerialPortChannel> createRemote, Func<bool> connected)
    { _endpoint = endpoint; _createRemote = createRemote; _connected = connected; _runner = Task.Run(RunAsync); }

    private async Task RunAsync()
    {
        VirtualSerialPeerState? previous = null;
        var buffer = new byte[8192];
        try
        {
            while (!_life.IsCancellationRequested)
            {
                if (!_connected()) throw new IOException("设备连接已断开，请重新连接后启用映射。");
                var state = _endpoint.GetPeerState();
                if (!state.IsOpen)
                {
                    if (_remote is not null) { await CloseRemoteAsync().ConfigureAwait(false); _endpoint.Discard(); }
                    previous = null; _status = "等待本机软件打开串口";
                    await Task.Delay(20, _life.Token).ConfigureAwait(false);
                    continue;
                }
                if (_remote is null)
                {
                    _status = "正在打开远端串口…";
                    _remote = _createRemote(state.ToSettings());
                    _remote.DataReceived += Received;
                    if (_remote is ISerialPortFaultNotifier notifier) notifier.Faulted += Faulted;
                    await _remote.OpenAsync(_life.Token).ConfigureAwait(false);
                    _endpoint.SetReady(true);
                }
                else if (previous is not null && state != previous)
                {
                    if (_remote is not ISerialPortConfiguration configuration) throw new NotSupportedException("对端不支持在线修改串口参数。");
                    await configuration.ConfigureAsync(state.ToSettings(), _life.Token).ConfigureAwait(false);
                }
                previous = state;
                _status = $"转发中 · {state.BaudRate} / {state.DataBits} / {state.Parity} / {state.StopBits}";
                for (var i = 0; i < 8 && _incoming.Reader.TryRead(out var bytes); i++)
                { _endpoint.Write(bytes); Interlocked.Add(ref _received, bytes.Length); }
                var count = _endpoint.Read(buffer); // bounded driver read timeout, runs away from the UI
                if (count > 0)
                {
                    var latest = _endpoint.GetPeerState();
                    if (!latest.IsOpen) continue;
                    if (latest != previous)
                    {
                        if (_remote is not ISerialPortConfiguration configuration) throw new NotSupportedException("对端不支持在线修改串口参数。");
                        await configuration.ConfigureAsync(latest.ToSettings(), _life.Token).ConfigureAwait(false); previous = latest;
                    }
                    await _remote.SendAsync(buffer.AsMemory(0, count), _life.Token).ConfigureAwait(false);
                    Interlocked.Add(ref _sent, count);
                }
                else await Task.Delay(1, _life.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { }
        catch (OperationCanceledException) { _error = "设备连接已取消或断开。"; }
        catch (Exception ex) when (ex is IOException or Win32Exception or ArgumentException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException or TimeoutException)
        { _error = ex.Message; }
        finally
        {
            try { await CloseRemoteAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException or Win32Exception) { if (_error.Length == 0) _error = ex.Message; }
            await _endpoint.DisposeAsync().ConfigureAwait(false);
            _status = _error.Length > 0 ? "失败：" + _error : "未启用";
        }
    }

    private void Received(object? sender, ReadOnlyMemory<byte> bytes)
    {
        if (_life.IsCancellationRequested) return;
        for (var offset = 0; offset < bytes.Length; offset += 8192)
        {
            if (_incoming.Writer.TryWrite(bytes.Slice(offset, Math.Min(8192, bytes.Length - offset)).ToArray())) continue;
            _error = "虚拟串口接收队列已满，已停止映射以避免丢失串口数据。";
            _life.Cancel(); return;
        }
    }
    private void Faulted(object? sender, string error) { _error = error; _life.Cancel(); }
    private async Task CloseRemoteAsync()
    {
        var remote = _remote; _remote = null;
        try { _endpoint.SetReady(false); }
        finally
        {
            if (remote is not null)
            {
                remote.DataReceived -= Received;
                if (remote is ISerialPortFaultNotifier notifier) notifier.Faulted -= Faulted;
                await remote.DisposeAsync().ConfigureAwait(false);
            }
            while (_incoming.Reader.TryRead(out _)) { }
        }
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
}
