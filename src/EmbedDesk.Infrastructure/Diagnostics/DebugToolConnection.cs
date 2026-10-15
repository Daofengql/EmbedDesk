using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using EmbedDesk.Core.Abstractions;
using EmbedDesk.Core.Models;

namespace EmbedDesk.Infrastructure.Diagnostics;

public enum DebugToolTransport { Serial, TcpClient, TcpServer, Udp, UdpTarget, UdpListen }

public sealed record DebugToolEndpoint(DebugToolTransport Transport, string Host, int Port, SerialPortSettings? Serial = null)
{
    public string LocalHost { get; init; } = "0.0.0.0";
    public int LocalPort { get; init; }
    public int TimeoutMilliseconds { get; init; } = 10000;
}

/// <summary>One independent connection per tool. UDP locks to the first sender for the session.</summary>
public sealed class DebugToolConnection(ISerialPortService serialService) : IDisposable
{
    private readonly SemaphoreSlim _send = new(1, 1);
    private ISerialPortChannel? _serial;
    private NetworkStream? _stream;
    private UdpClient? _udp;
    private IPEndPoint? _peer;

    public async Task RunAsync(DebugToolEndpoint endpoint, Func<ReadOnlyMemory<byte>, Task> receive, Action<string> state, CancellationToken token)
    {
        if (!Enum.IsDefined(endpoint.Transport) || endpoint.Transport == DebugToolTransport.UdpListen)
            throw new ArgumentException("协议调试连接需要指定目标或锁定单个对端。");
        if (endpoint.Transport == DebugToolTransport.Serial)
        {
            var queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(32) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
            await using var channel = serialService.CreateChannel(endpoint.Serial ?? throw new ArgumentException("请选择串口参数。"));
            void OnData(object? sender, ReadOnlyMemory<byte> bytes)
            {
                if (bytes.Length > 256 * 1024 || !queue.Writer.TryWrite(bytes.ToArray()))
                    queue.Writer.TryComplete(new IOException("处理速度低于串口接收速度，接收已停止。请降低发送速率。"));
            }
            void OnFault(object? sender, string message) => queue.Writer.TryComplete(new IOException(message));
            channel.DataReceived += OnData;
            if (channel is ISerialPortFaultNotifier notifier) notifier.Faulted += OnFault;
            try
            {
                await channel.OpenAsync(token); _serial = channel; state("串口已连接");
                await foreach (var bytes in queue.Reader.ReadAllAsync(token)) await receive(bytes);
            }
            finally
            {
                _serial = null; channel.DataReceived -= OnData;
                if (channel is ISerialPortFaultNotifier notifier2) notifier2.Faulted -= OnFault;
            }
            return;
        }
        if (endpoint.Transport is DebugToolTransport.Udp or DebugToolTransport.UdpTarget)
        {
            var targetMode = endpoint.Transport == DebugToolTransport.UdpTarget;
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Parse(targetMode ? endpoint.LocalHost : endpoint.Host), targetMode ? endpoint.LocalPort : endpoint.Port));
            _udp = udp; _peer = null;
            try
            {
                if (targetMode)
                {
                    var addresses = await Dns.GetHostAddressesAsync(endpoint.Host, token);
                    _peer = new IPEndPoint(addresses.First(address => address.AddressFamily == udp.Client.AddressFamily), endpoint.Port);
                    state("UDP 已绑定目标");
                }
                else state("UDP 正在监听 · 锁定首个发送端");
                while (true)
                {
                    var packet = await udp.ReceiveAsync(token);
                    if (_peer is null) { _peer = packet.RemoteEndPoint; state("UDP 已锁定发送端"); }
                    if (_peer.Equals(packet.RemoteEndPoint)) await receive(packet.Buffer);
                }
            }
            finally { _udp = null; _peer = null; }
        }
        TcpListener? listener = null;
        TcpClient? client = null;
        try
        {
            if (endpoint.Transport == DebugToolTransport.TcpServer)
            {
                listener = new TcpListener(IPAddress.Parse(endpoint.Host), endpoint.Port); listener.Start(1);
                state("TCP 正在监听 · 等待开发板连接");
                client = await listener.AcceptTcpClientAsync(token);
                listener.Stop();
            }
            else
            {
                client = new TcpClient();
                if (endpoint.LocalHost != "0.0.0.0" || endpoint.LocalPort != 0)
                    client.Client.Bind(new IPEndPoint(IPAddress.Parse(endpoint.LocalHost), endpoint.LocalPort));
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(endpoint.TimeoutMilliseconds);
                try { await client.ConnectAsync(endpoint.Host, endpoint.Port, timeout.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException($"TCP 连接超时（{endpoint.TimeoutMilliseconds} 毫秒）。"); }
            }
            client.NoDelay = true;
            _stream = client.GetStream(); state("TCP 已连接");
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var count = await _stream.ReadAsync(buffer, token);
                if (count == 0) throw new IOException("对端已关闭连接。");
                await receive(buffer.AsMemory(0, count));
            }
        }
        finally { _stream = null; client?.Dispose(); listener?.Stop(); }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await _send.WaitAsync(timeout.Token);
        try
        {
            if (_serial is { } serial) await serial.SendAsync(bytes, timeout.Token);
            else if (_stream is { } stream) await stream.WriteAsync(bytes, timeout.Token);
            else if (_udp is { } udp && _peer is { } peer) await udp.SendAsync(bytes, peer, timeout.Token);
            else throw new InvalidOperationException("尚未连接到开发板。");
        }
        finally { _send.Release(); }
    }

    // The owner cancels and joins receive/send tasks before disposing the gate.
    public void Dispose() => _send.Dispose();
}
