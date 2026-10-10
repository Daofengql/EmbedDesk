using System.Collections.ObjectModel;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Windows;

namespace EmbeddedDebugger.App;

public sealed record NatProbeResult(string Server, string MappedEndpoint, string Latency, string Status);

public partial class NatDetectionWindow : Window, IDisposable
{
    private const uint MagicCookie = 0x2112A442;
    private CancellationTokenSource? _lifetime;

    public NatDetectionWindow()
    {
        InitializeComponent();
        DataContext = this;
        WindowTheme.ApplyDarkTitleBar(this);
        Closed += (_, _) => Dispose();
    }

    public ObservableCollection<NatProbeResult> Results { get; } = [];

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        await StopAsync();
        var servers = ServersBox.Text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (servers.Length == 0) { StatusText.Text = "请填写至少一个 STUN 服务。"; return; }
        Results.Clear(); NatTypeText.Text = "检测中…"; SummaryText.Text = string.Empty; StatusText.Text = "正在发送 STUN Binding Request…";
        var lifetime = new CancellationTokenSource();
        _lifetime = lifetime; StopButton.IsEnabled = true; ServersBox.IsEnabled = false;
        var mapped = new List<IPEndPoint>();
        try
        {
            foreach (var server in servers)
            {
                if (lifetime.IsCancellationRequested) break;
                var result = await ProbeServerAsync(server, lifetime.Token);
                Results.Add(result.Result);
                if (result.Endpoint is not null) mapped.Add(result.Endpoint);
            }
            if (!lifetime.IsCancellationRequested) Classify(mapped);
        }
        finally
        {
            var canceled = lifetime.IsCancellationRequested;
            if (ReferenceEquals(_lifetime, lifetime)) _lifetime = null;
            lifetime.Dispose(); StopButton.IsEnabled = false; ServersBox.IsEnabled = true;
            StatusText.Text = canceled ? "已停止" : "检测完成";
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopAsync();

    private async Task StopAsync()
    {
        var lifetime = _lifetime;
        if (lifetime is null) return;
        _lifetime = null; lifetime.Cancel(); lifetime.Dispose();
        await Task.Yield();
        StopButton.IsEnabled = false; ServersBox.IsEnabled = true;
    }

    private static async Task<(NatProbeResult Result, IPEndPoint? Endpoint)> ProbeServerAsync(string server, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var hostParts = server.LastIndexOf(':') is var separator and > 0 && !server.Contains(']')
                ? (server[..separator], server[(separator + 1)..])
                : (server, "3478");
            if (!int.TryParse(hostParts.Item2, out var port) || port is < 1 or > 65535) throw new FormatException("端口无效");
            var addresses = await Dns.GetHostAddressesAsync(hostParts.Item1, cancellationToken);
            var address = addresses.FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault() ?? throw new SocketException((int)SocketError.HostNotFound);
            using var client = new UdpClient(address.AddressFamily);
            var request = CreateBindingRequest(out var transactionId);
            await client.SendAsync(request, request.Length, new IPEndPoint(address, port));
            var response = await client.ReceiveAsync(cancellationToken);
            var endpoint = ParseMappedEndpoint(response.Buffer, transactionId);
            if (endpoint is null) throw new InvalidDataException("响应中没有 XOR-MAPPED-ADDRESS");
            return (new(server, endpoint.ToString(), $"{Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.0} ms", "成功"), endpoint);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is SocketException or FormatException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            return (new(server, "—", "—", ex.Message), null);
        }
    }

    private void Classify(IReadOnlyList<IPEndPoint> endpoints)
    {
        if (endpoints.Count == 0)
        {
            NatTypeText.Text = "无法判断";
            SummaryText.Text = "没有收到任何 STUN 响应，可能是 UDP 被阻断或服务不可用。";
            return;
        }
        var distinct = endpoints.Select(endpoint => endpoint.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        HashSet<string> localAddresses;
        try { localAddresses = Dns.GetHostEntry(Dns.GetHostName()).AddressList.Select(address => address.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase); }
        catch (SocketException) { localAddresses = []; }
        var noNat = endpoints.All(endpoint => localAddresses.Contains(endpoint.Address.ToString()));
        NatTypeText.Text = noNat ? "可能无 NAT / 公网直连" : distinct.Length == 1 ? "映射稳定的 NAT" : "可能对称 NAT";
        SummaryText.Text = $"收到 {endpoints.Count} 个响应 · 发现 {distinct.Length} 个公网映射。STUN 结果用于判断映射特征，不替代完整 RFC 3489 行为测试。";
    }

    private static byte[] CreateBindingRequest(out byte[] transactionId)
    {
        var request = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(request, 0x0001);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4), MagicCookie);
        transactionId = new byte[12]; RandomNumberGenerator.Fill(transactionId); transactionId.CopyTo(request, 8);
        return request;
    }

    private static IPEndPoint? ParseMappedEndpoint(byte[] message, byte[] transactionId)
    {
        if (message.Length < 20 || BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(4)) != MagicCookie || !message.AsSpan(8, 12).SequenceEqual(transactionId)) return null;
        var length = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(2)), message.Length - 20);
        for (var offset = 20; offset + 4 <= 20 + length;)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset));
            var size = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset + 2));
            var valueOffset = offset + 4;
            if (valueOffset + size > message.Length) break;
            if ((type == 0x0020 || type == 0x0001) && size >= 8)
            {
                var family = message[valueOffset + 1];
                var port = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(valueOffset + 2));
                if (family is not (0x01 or 0x02) || size < (family == 0x02 ? 20 : 8)) { offset += 4 + ((size + 3) & ~3); continue; }
                var addressBytes = new byte[family == 0x02 ? 16 : 4];
                message.AsSpan(valueOffset + 4, addressBytes.Length).CopyTo(addressBytes);
                if (type == 0x0020)
                {
                    port ^= (ushort)(MagicCookie >> 16);
                    var cookie = BitConverter.GetBytes(MagicCookie); if (BitConverter.IsLittleEndian) Array.Reverse(cookie);
                    for (var i = 0; i < Math.Min(4, addressBytes.Length); i++) addressBytes[i] ^= cookie[i];
                    for (var i = 4; i < addressBytes.Length; i++) addressBytes[i] ^= transactionId[i - 4];
                }
                return new IPEndPoint(new IPAddress(addressBytes), port);
            }
            offset += 4 + ((size + 3) & ~3);
        }
        return null;
    }

    public void Dispose()
    {
        _lifetime?.Cancel(); _lifetime?.Dispose(); _lifetime = null; GC.SuppressFinalize(this);
    }
}
