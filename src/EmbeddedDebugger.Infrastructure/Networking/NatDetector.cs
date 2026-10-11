using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace EmbeddedDebugger.Infrastructure.Networking;

public enum NatKind { Unknown, OpenInternet, FullCone, RestrictedCone, PortRestrictedCone, Symmetric }
public sealed record NatObservation(string Server, string MappedEndpoint, string Latency, string Detail);
public sealed record NatReport(NatKind Kind, string Evidence)
{
    public string Label => Kind switch
    {
        NatKind.FullCone => "全锥形 NAT / Full Cone NAT",
        NatKind.RestrictedCone => "受限锥形 NAT / Restricted Cone NAT",
        NatKind.PortRestrictedCone => "端口受限锥形 NAT / Port Restricted Cone NAT",
        NatKind.Symmetric => "对称型 NAT / Symmetric NAT",
        NatKind.OpenInternet => "无 NAT / Open Internet",
        _ => "暂无法确定 / Inconclusive"
    };
}

/// <summary>IPv4 UDP mapping comparison and RFC 5780 alternate-source filtering tests.</summary>
public sealed class NatDetector
{
    public static IReadOnlyList<string> DefaultServers { get; } =
    [
        "stun.miwifi.com:3478", "stun.qq.com:3478", "stun.chat.bilibili.com:3478",
        "stun.hitv.com:3478", "stun.douyucdn.cn:3478",
        "stun.cloudflare.com:3478", "stun.l.google.com:19302"
    ];
    private const uint Cookie = 0x2112A442;
    private readonly TimeSpan _timeout;
    public NatDetector(TimeSpan? timeout = null) => _timeout = timeout ?? TimeSpan.FromMilliseconds(1200);

    public async Task<NatReport> DetectAsync(IEnumerable<string> servers, IProgress<NatObservation>? progress, CancellationToken token)
    {
        // Every mapping comparison MUST reuse this socket: different local ports create
        // different mappings even on a full-cone NAT.
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var observations = new List<(IPEndPoint Server, IPEndPoint Mapped, IPAddress Local)>();
        var filters = new HashSet<NatKind>();
        var behaviorTests = 0;
        foreach (var server in servers.Distinct(StringComparer.OrdinalIgnoreCase).Take(16))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var remote = await ResolveAsync(server, token).ConfigureAwait(false);
                var started = Stopwatch.GetTimestamp();
                var reply = await ExchangeAsync(socket, remote, 0, null, token).ConfigureAwait(false);
                if (reply?.Mapped is null || reply.Error)
                {
                    progress?.Report(new(server, "—", "—", reply?.Error == true ? "STUN 服务拒绝请求" : "超时 / 无有效响应"));
                    continue;
                }
                using var route = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                route.Connect(remote);
                observations.Add((remote, reply.Mapped, ((IPEndPoint)route.LocalEndPoint!).Address));
                progress?.Report(new(server, reply.Mapped.ToString(), $"{Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.0} ms",
                    reply.Other is null ? "映射成功 · 不支持行为检测" : "映射成功 · 支持备用地址"));

                if (behaviorTests < 2 && ValidAlternate(remote, reply.Other))
                {
                    behaviorTests++;
                    var filtering = await TestFilteringAsync(remote, token).ConfigureAwait(false);
                    if (filtering != NatKind.Unknown) filters.Add(filtering);
                    progress?.Report(new(server + " · 过滤测试", reply.Mapped.ToString(), "—", filtering switch
                    {
                        NatKind.FullCone => "已收到不同 IP、不同端口的响应",
                        NatKind.RestrictedCone => "仅同 IP 的不同端口响应成功",
                        NatKind.PortRestrictedCone => "备用来源均超时，原来源复测成功（推断）",
                        _ => "备用来源不可验证，不能细分锥形类型"
                    }));
                    // Additional mapping destination on the same socket, after filtering.
                    var alternate = await ExchangeAsync(socket, reply.Other!, 0, null, token).ConfigureAwait(false);
                    if (alternate?.Mapped is not null && !alternate.Error)
                    {
                        using var alternateRoute = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                        alternateRoute.Connect(reply.Other!);
                        observations.Add((reply.Other!, alternate.Mapped, ((IPEndPoint)alternateRoute.LocalEndPoint!).Address));
                        progress?.Report(new(server + " · 备用地址", alternate.Mapped.ToString(), "—", "备用映射成功"));
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is SocketException or FormatException or ArgumentException or TimeoutException)
            {
                progress?.Report(new(server, "—", "—", ex.Message));
            }
        }
        if (observations.Count == 0)
            return new(NatKind.Unknown, "没有收到有效响应，可能是节点不可用或 UDP 受限；不能据此确定 NAT 类型。");
        var first = observations[0];
        var distinctServers = observations.Select(o => o.Server.Address).Distinct().Count();
        var mappedCount = observations.Select(o => o.Mapped).Distinct().Count();
        var prefix = $"有效目标 IP {distinctServers} 个 · 公网映射 {mappedCount} 个。";
        if (observations.Select(o => o.Local).Distinct().Count() > 1)
            return new(NatKind.Unknown, prefix + "节点经不同网络出口发送，映射不能直接比较。");
        if (observations.Select(o => o.Mapped.Address).Distinct().Count() > 1)
            return new(NatKind.Unknown, prefix + "出现多个公网出口，可能存在 VPN / 代理分流、多出口或 NAT 地址池。请选用同一出口的节点重新检测，不能仅凭公网 IP 不同判为对称型。");
        var localPort = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        if (observations.All(o => o.Mapped.Address.Equals(o.Local) && o.Mapped.Port == localPort))
            return new(NatKind.OpenInternet, prefix + "公网地址及端口与本地出口一致；入站过滤仍可能由防火墙限制。");
        if (distinctServers < 2)
            return new(NatKind.Unknown, prefix + "需要至少两个不同 IP 的可用节点比较映射。");
        // Recheck the initial destination to distinguish destination-dependent mapping
        // from a changing network/mapping during the measurement.
        Reply? recheck;
        try { recheck = await ExchangeAsync(socket, first.Server, 0, null, token).ConfigureAwait(false); }
        catch (SocketException) { recheck = null; }
        if (recheck?.Mapped is null || recheck.Error || !recheck.Mapped.Equals(first.Mapped))
            return new(NatKind.Unknown, prefix + "首节点复测失败或映射变化，请重试以排除网络切换与映射过期。");
        if (mappedCount > 1)
            return new(NatKind.Symmetric, prefix + "同一本地 UDP 端口对不同目标产生不同映射，首节点复测一致。");
        if (filters.Count != 1)
            return new(NatKind.Unknown, prefix + (filters.Count > 1
                ? "节点的过滤测试结果不一致，请复测。"
                : "映射稳定（锥形候选）；节点未提供有效的备用来源响应，无法区分全锥形、受限锥形与端口受限锥形。"));
        var kind = filters.Single();
        return new(kind, prefix + "同一本地 UDP 端口的映射稳定。" + (kind switch
        {
            NatKind.FullCone => "未事先发送到备用 IP / 端口，也收到其响应。",
            NatKind.RestrictedCone => "不同 IP 的响应超时，同 IP 的不同端口响应成功（基于超时推断）。",
            _ => "不同 IP / 端口的响应均超时，原来源复测成功（基于超时推断，丢包或防火墙可能影响结果）。"
        }));
    }

    private async Task<NatKind> TestFilteringAsync(IPEndPoint primary, CancellationToken token)
    {
        // A fresh socket has not contacted the alternate IP/port and therefore has
        // not opened a pinhole that would invalidate restricted-cone filtering tests.
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var baseline = await ExchangeAsync(socket, primary, 0, null, token).ConfigureAwait(false);
        if (baseline?.Mapped is null || !ValidAlternate(primary, baseline.Other)) return NatKind.Unknown;
        var other = baseline.Other!;
        var changed = await ExchangeAsync(socket, primary, 6, other, token).ConfigureAwait(false);
        if (changed is not null)
            return !changed.Error && changed.Source.Equals(other) && Equals(changed.Mapped, baseline.Mapped)
                ? NatKind.FullCone : NatKind.Unknown; // A server ignoring CHANGE-REQUEST is not evidence.
        var changedPort = new IPEndPoint(primary.Address, other.Port);
        var portReply = await ExchangeAsync(socket, primary, 2, changedPort, token).ConfigureAwait(false);
        if (portReply is not null)
            return !portReply.Error && portReply.Source.Equals(changedPort) && Equals(portReply.Mapped, baseline.Mapped)
                ? NatKind.RestrictedCone : NatKind.Unknown;
        var control = await ExchangeAsync(socket, primary, 0, null, token).ConfigureAwait(false);
        return control is { Error: false } && Equals(control.Mapped, baseline.Mapped) ? NatKind.PortRestrictedCone : NatKind.Unknown;
    }

    private static bool ValidAlternate(IPEndPoint primary, IPEndPoint? other) =>
        other is not null && other.Address.AddressFamily == AddressFamily.InterNetwork &&
        IsUnicast(other.Address) && !other.Address.Equals(primary.Address) && other.Port != primary.Port && other.Port > 0;

    private static bool IsUnicast(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes()[0] is > 0 and < 224 && !address.Equals(IPAddress.Broadcast);

    private static async Task<IPEndPoint> ResolveAsync(string server, CancellationToken token)
    {
        var parts = server.Split(':');
        if (parts.Length > 2 || string.IsNullOrWhiteSpace(parts[0])) throw new FormatException("请输入 IPv4 主机名:端口");
        var port = 3478;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out port) || port is < 1 or > 65535)) throw new FormatException("端口无效");
        var addresses = await Dns.GetHostAddressesAsync(parts[0], token).WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
        var address = addresses.FirstOrDefault(IsUnicast) ?? throw new FormatException("没有可用 IPv4 地址");
        return new(address, port);
    }

    private async Task<Reply?> ExchangeAsync(UdpClient socket, IPEndPoint target, uint change, IPEndPoint? alternate, CancellationToken token)
    {
        var request = new byte[change == 0 ? 20 : 28];
        BinaryPrimitives.WriteUInt16BigEndian(request, 1);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), (ushort)(request.Length - 20));
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4), Cookie);
        RandomNumberGenerator.Fill(request.AsSpan(8, 12));
        if (change != 0)
        {
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(20), 3);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(22), 4);
            BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(24), change);
        }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(_timeout);
            try
            {
                await socket.SendAsync(request, target, deadline.Token).ConfigureAwait(false);
                while (true)
                {
                    var datagram = await socket.ReceiveAsync(deadline.Token).ConfigureAwait(false);
                    if (!datagram.RemoteEndPoint.Equals(target) && !datagram.RemoteEndPoint.Equals(alternate)) continue;
                    var reply = Parse(datagram.Buffer, request, datagram.RemoteEndPoint);
                    if (reply is not null) return reply;
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        }
        token.ThrowIfCancellationRequested();
        return null;
    }

    private sealed record Reply(IPEndPoint Source, IPEndPoint? Mapped, IPEndPoint? Other, bool Error);
    private static Reply? Parse(byte[] data, byte[] request, IPEndPoint source)
    {
        if (data.Length < 20 || !data.AsSpan(4, 16).SequenceEqual(request.AsSpan(4, 16))) return null;
        var type = BinaryPrimitives.ReadUInt16BigEndian(data);
        var length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2));
        if (type is not (0x0101 or 0x0111) || length % 4 != 0 || 20 + length != data.Length) return null;
        IPEndPoint? mapped = null, xorMapped = null, other = null;
        for (var offset = 20; offset < data.Length;)
        {
            if (offset + 4 > data.Length) return null;
            var attr = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
            var size = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 2));
            if (offset + 4 + size > data.Length) return null;
            var value = data.AsSpan(offset + 4, size);
            if (attr is 1 or 0x20 or 0x802c or 5)
            {
                // Detection deliberately uses IPv4. Ignore other address families.
                if (size == 8 && value[1] == 1)
                {
                    var port = BinaryPrimitives.ReadUInt16BigEndian(value[2..]);
                    var ip = BinaryPrimitives.ReadUInt32BigEndian(value[4..]);
                    if (attr == 0x20) { port ^= (ushort)(Cookie >> 16); ip ^= Cookie; }
                    var bytes = new byte[4];
                    BinaryPrimitives.WriteUInt32BigEndian(bytes, ip);
                    var endpoint = new IPEndPoint(new IPAddress(bytes), port);
                    if (attr == 0x20) xorMapped = endpoint;
                    else if (attr == 1) mapped = endpoint;
                    else other = endpoint;
                }
            }
            offset += 4 + ((size + 3) & ~3);
        }
        return new(source, xorMapped ?? mapped, other, type == 0x0111);
    }
}
