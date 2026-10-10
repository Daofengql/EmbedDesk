using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

namespace EmbeddedDebugger.Infrastructure.Remote;

public sealed record VncFrame(int Width, int Height, byte[] Pixels);

/// <summary>RFB 3.3/3.7/3.8 client with Raw, CopyRect and DesktopSize encodings.</summary>
public sealed class VncClient : IAsyncDisposable
{
    private readonly TcpClient _tcp = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private NetworkStream? _stream;
    private Task? _reader;
    private byte[] _pixels = [];
    private int _width, _height;
    private bool _disposed;
    public string DesktopName { get; private set; } = string.Empty;
    public Func<VncFrame, Task>? FrameReceived { get; set; }
    public event Action<string>? Disconnected;

    public async Task ConnectAsync(string host, int port, string password, CancellationToken token = default)
    {
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        connect.CancelAfter(TimeSpan.FromSeconds(20));
        var ct = connect.Token;
        await _tcp.ConnectAsync(host, port, ct);
        _tcp.NoDelay = true;
        _stream = _tcp.GetStream();
        var version = Encoding.ASCII.GetString(await ReadAsync(12, ct));
        if (!version.StartsWith("RFB 003.", StringComparison.Ordinal) || !int.TryParse(version.AsSpan(8, 3), out var minor) || minor < 3)
            throw new IOException("服务器未返回支持的 RFB 版本。");
        var negotiated = minor >= 8 ? 8 : minor >= 7 ? 7 : 3;
        await WriteAsync(Encoding.ASCII.GetBytes($"RFB 003.00{negotiated}\n"), ct);
        uint security;
        if (negotiated == 3)
        {
            security = await ReadUInt32Async(ct);
            if (security == 0) throw new IOException(await ReadStringAsync(ct));
        }
        else
        {
            var count = (await ReadAsync(1, ct))[0];
            if (count == 0) throw new IOException(await ReadStringAsync(ct));
            var types = await ReadAsync(count, ct);
            security = types.Contains((byte)2) ? 2u : types.Contains((byte)1) ? 1u : 0u;
            if (security == 0) throw new NotSupportedException("此 VNC 服务器要求 TLS / VeNCrypt 或专用认证；当前支持标准 VNC 密码认证和无认证。");
            await WriteAsync([(byte)security], ct);
        }
        if (security == 2)
        {
            var challenge = await ReadAsync(16, ct);
            var key = new byte[8];
            for (var i = 0; i < Math.Min(8, password.Length); i++)
            {
                if (password[i] > 255) throw new ArgumentException("标准 VNC 密码使用 Latin-1 字符，最多取前 8 个字符。");
                var value = (byte)password[i];
                for (var bit = 0; bit < 8; bit++) key[i] |= (byte)(((value >> bit) & 1) << (7 - bit));
            }
            var des = new DesEngine();
            des.Init(true, new KeyParameter(key));
            var response = new byte[16];
            des.ProcessBlock(challenge, 0, response, 0);
            des.ProcessBlock(challenge, 8, response, 8);
            Array.Clear(key);
            await WriteAsync(response, ct);
            Array.Clear(response);
        }
        else if (security != 1) throw new NotSupportedException($"不支持的 VNC 认证类型：{security}");
        if (security == 2 || negotiated >= 8)
        {
            if (await ReadUInt32Async(ct) != 0)
                throw new IOException(negotiated >= 8 ? await ReadStringAsync(ct) : "VNC 认证失败，请检查密码。");
        }
        await WriteAsync([1], ct); // Shared session: do not disconnect other viewers.
        var init = await ReadAsync(24, ct);
        Resize(U16(init, 0), U16(init, 2));
        DesktopName = Encoding.UTF8.GetString(await ReadAsync(CheckedLength(BinaryPrimitives.ReadUInt32BigEndian(init.AsSpan(20)), 1024 * 1024), ct));
        // 32-bit little-endian BGRX, 24 significant bits, true colour.
        await WriteAsync([0, 0, 0, 0, 32, 24, 0, 1, 0, 255, 0, 255, 0, 255, 16, 8, 0, 0, 0, 0], ct);
        byte[] encodings = [2, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0, 1, 255, 255, 255, 33, 255, 255, 255, 32];
        await WriteAsync(encodings, ct); // Raw, CopyRect, DesktopSize (-223), LastRect (-224).
        await RequestFrameAsync(false, ct);
        _reader = ReadLoopAsync(_lifetime.Token);
    }

    public async Task SendKeyAsync(uint keysym, bool down, CancellationToken token = default)
    {
        var packet = new byte[8]; packet[0] = 4; packet[1] = down ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), keysym);
        await WriteAsync(packet, token);
    }
    public async Task SendPointerAsync(int x, int y, byte buttons, CancellationToken token = default)
    {
        var packet = new byte[6]; packet[0] = 5; packet[1] = buttons;
        Put16(packet, 2, Math.Clamp(x, 0, _width - 1)); Put16(packet, 4, Math.Clamp(y, 0, _height - 1));
        await WriteAsync(packet, token);
    }
    public async Task SendClipboardAsync(string text, CancellationToken token = default)
    {
        if (text.Any(c => c > 255)) throw new NotSupportedException("标准 VNC 剪贴板仅支持 Latin-1 文本。");
        var data = Encoding.Latin1.GetBytes(text);
        if (data.Length > 1024 * 1024) throw new ArgumentException("剪贴板文本过大。");
        var packet = new byte[8 + data.Length]; packet[0] = 6;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), (uint)data.Length); data.CopyTo(packet, 8);
        await WriteAsync(packet, token);
    }
    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var type = (await ReadAsync(1, ct))[0];
                if (type == 0)
                {
                    var header = await ReadAsync(3, ct);
                    var count = U16(header, 1);
                    var resized = false;
                    for (var i = 0; i < count; i++)
                    {
                        var rect = await ReadAsync(12, ct);
                        var x = U16(rect, 0); var y = U16(rect, 2); var w = U16(rect, 4); var h = U16(rect, 6);
                        var encoding = BinaryPrimitives.ReadInt32BigEndian(rect.AsSpan(8));
                        if (encoding == -224) break;
                        if (encoding == -223) { Resize(w, h); resized = true; continue; }
                        ValidateRectangle(x, y, w, h);
                        if (encoding == 0)
                        {
                            var data = await ReadAsync(checked(w * h * 4), ct);
                            for (var row = 0; row < h; row++) Buffer.BlockCopy(data, row * w * 4, _pixels, ((y + row) * _width + x) * 4, w * 4);
                        }
                        else if (encoding == 1)
                        {
                            var source = await ReadAsync(4, ct); var sx = U16(source, 0); var sy = U16(source, 2);
                            ValidateRectangle(sx, sy, w, h);
                            for (var n = 0; n < h; n++)
                            {
                                var row = y > sy ? h - 1 - n : n;
                                Buffer.BlockCopy(_pixels, ((sy + row) * _width + sx) * 4, _pixels, ((y + row) * _width + x) * 4, w * 4);
                            }
                        }
                        else throw new IOException($"服务器返回未协商的 VNC 编码：{encoding}");
                    }
                    if (FrameReceived is { } render) await render(new VncFrame(_width, _height, (byte[])_pixels.Clone()));
                    await RequestFrameAsync(!resized, ct);
                }
                else if (type == 2) { /* Bell */ }
                else if (type == 3)
                {
                    var header = await ReadAsync(7, ct);
                    await ReadAsync(CheckedLength(BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(3)), 1024 * 1024), ct);
                    // Do not overwrite the user's local clipboard from a remote host.
                }
                else throw new IOException($"不支持的 VNC 消息：{type}");
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            if (!ct.IsCancellationRequested) Disconnected?.Invoke(ex.Message);
        }
    }
    private async Task RequestFrameAsync(bool incremental, CancellationToken ct)
    {
        var packet = new byte[10]; packet[0] = 3; packet[1] = incremental ? (byte)1 : (byte)0;
        Put16(packet, 6, _width); Put16(packet, 8, _height); await WriteAsync(packet, ct);
    }
    private async Task<byte[]> ReadAsync(int count, CancellationToken ct)
    {
        var data = new byte[count]; await _stream!.ReadExactlyAsync(data, ct); return data;
    }
    private async Task<uint> ReadUInt32Async(CancellationToken ct) => BinaryPrimitives.ReadUInt32BigEndian(await ReadAsync(4, ct));
    private async Task<string> ReadStringAsync(CancellationToken ct) => Encoding.UTF8.GetString(await ReadAsync(CheckedLength(await ReadUInt32Async(ct), 1024 * 1024), ct));
    private async Task WriteAsync(byte[] data, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _writeGate.WaitAsync(linked.Token);
        try { await (_stream ?? throw new IOException("VNC 尚未连接。")).WriteAsync(data, linked.Token); }
        finally { _writeGate.Release(); }
    }
    private void Resize(int width, int height)
    {
        if (width < 1 || height < 1 || width > 8192 || height > 8192 || (long)width * height > 16_777_216) throw new IOException("VNC 桌面尺寸无效或过大。");
        _width = width; _height = height; _pixels = new byte[checked(width * height * 4)];
    }
    private void ValidateRectangle(int x, int y, int w, int h)
    {
        if (x + w > _width || y + h > _height) throw new IOException("VNC 图像区域超出桌面边界。");
    }
    private static int CheckedLength(uint value, int max) => value <= max ? (int)value : throw new IOException("VNC 消息长度超过限制。");
    private static int U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
    private static void Put16(byte[] data, int offset, int value) => BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset), (ushort)value);
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel(); _tcp.Dispose();
        if (_reader is not null) await _reader;
        // In-flight writes can still be unwinding; do not dispose their semaphore.
    }
}
