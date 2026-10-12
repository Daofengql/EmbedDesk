using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace EmbedDesk.Infrastructure.Networking;

public enum NatKind { Unknown, OpenInternet, FullCone, RestrictedCone, PortRestrictedCone, Symmetric }
public sealed record NatObservation(string Server, string MappedEndpoint, string Latency, string Detail);
public sealed record NatExitReport(string PublicIp, NatKind Kind, string Evidence,
    IReadOnlyList<string> Nodes, int TargetCount, IReadOnlyList<int> Ports, string LocalEndpoint)
{
    public string Heading => $"公网出口 {PublicIp}";
    public string MappingDetails => $"节点 {Nodes.Count} 个 · 目标 IP {TargetCount} 个（含备用地址） · 映射端口：{string.Join(", ", Ports)}";
    public string NodesText => "参与节点：" + string.Join("、", Nodes);
    public string LocalDetails => "本地 UDP：" + LocalEndpoint;
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
public sealed record NatReport(IReadOnlyList<NatExitReport> Exits, string Evidence)
{
    // There is deliberately no single NAT classification across independent exits.
    public NatKind Kind => Exits.Count == 1 ? Exits[0].Kind : NatKind.Unknown;
    public string Label => Exits.Count switch
    {
        0 => "暂无法确定 / Inconclusive",
        1 => Exits[0].Label,
        _ => $"检测到 {Exits.Count} 个公网出口 / Multiple exits"
    };
}

/// <summary>IPv4 UDP mapping comparison and RFC 5780 alternate-source filtering tests.</summary>
public sealed class NatDetector
{
    public static IReadOnlyList<string> DefaultServers { get; } =
    [
        "stun.miwifi.com:3478", "stun.chat.bilibili.com:3478", "stun.hitv.com:3478",
        "stun.cdnbye.com:3478", "stun.cloudflare.com:3478", "stun.l.google.com:19302",
        "stun.voip.blackberry.com:3478", "stun.antisip.com:3478", "stun.telnyx.com:3478"
    ];
    private const uint Cookie = 0x2112A442;
    private readonly TimeSpan _timeout;
    public NatDetector(TimeSpan? timeout = null) => _timeout = timeout ?? TimeSpan.FromMilliseconds(1200);

    public async Task<NatReport> DetectAsync(IEnumerable<string> servers, IProgress<NatObservation>? progress, CancellationToken token)
    {
        // Every mapping comparison MUST reuse this socket: different local ports create
        // different mappings even on a full-cone NAT.
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var observations = new List<MappingSample>();
        var filters = new Dictionary<IPAddress, List<FilteringSample>>();
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
                var local = LocalAddress(remote);
                observations.Add(new(server, remote, reply.Mapped, local));
                progress?.Report(new(server, reply.Mapped.ToString(), $"{Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.0} ms", "映射成功"));

                if (!filters.TryGetValue(reply.Mapped.Address, out var exitFilters))
                    filters[reply.Mapped.Address] = exitFilters = [];
                if (exitFilters.Count < 2 && ValidAlternate(remote, reply.Other))
                {
                    // Budget and evidence belong to this exit, not to the whole run.
                    // A fresh filtering socket can itself be routed to a different exit.
                    FilteringSample? filtering = null;
                    try { filtering = await TestFilteringAsync(remote, token).ConfigureAwait(false); }
                    catch (SocketException) { }
                    exitFilters.Add(filtering is not null && filtering.PublicIp.Equals(reply.Mapped.Address) && filtering.Local.Equals(local)
                        ? filtering : new(reply.Mapped.Address, local, NatKind.Unknown));
                    // Additional mapping destination on the same socket, after filtering.
                    try
                    {
                        var alternate = await ExchangeAsync(socket, reply.Other!, 0, null, token).ConfigureAwait(false);
                        if (alternate?.Mapped is not null && !alternate.Error)
                            observations.Add(new(server, reply.Other!, alternate.Mapped, LocalAddress(reply.Other!)));
                    }
                    catch (SocketException) { } // Optional diagnostics must not duplicate a successful node row.
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is SocketException or FormatException or ArgumentException or TimeoutException)
            {
                progress?.Report(new(server, "—", "—", ex.Message));
            }
        }
        if (observations.Count == 0)
            return new([], "没有收到有效响应，可能是节点不可用或 UDP 受限；不能据此确定 NAT 类型。");
        var localPort = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        var exits = new List<NatExitReport>();
        foreach (var group in observations.GroupBy(o => o.Mapped.Address))
        {
            token.ThrowIfCancellationRequested();
            filters.TryGetValue(group.Key, out var exitFilters);
            var changingExit = group.Any(sample => observations.Any(other =>
                other.Server.Equals(sample.Server) && !other.Mapped.Address.Equals(sample.Mapped.Address)));
            exits.Add(await ClassifyExitAsync(socket, group.ToList(), exitFilters ?? [], localPort, changingExit, token).ConfigureAwait(false));
        }
        var confirmed = exits.Count(exit => exit.Kind != NatKind.Unknown);
        return new(exits, $"按公网 IP 分组，{confirmed}/{exits.Count} 个出口已判定。" + (exits.Count > 1
            ? "不同出口可能来自分流、多出口或 NAT 地址池；各组独立比较，结论仅适用于本次采样路径。"
            : "结论仅适用于本次 IPv4 / UDP 采样路径。"));
    }

    private sealed record MappingSample(string Name, IPEndPoint Server, IPEndPoint Mapped, IPAddress Local);
    private sealed record FilteringSample(IPAddress PublicIp, IPAddress Local, NatKind Kind);

    private static IPAddress LocalAddress(IPEndPoint remote)
    {
        using var route = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        route.Connect(remote);
        return ((IPEndPoint)route.LocalEndPoint!).Address;
    }

    private async Task<NatExitReport> ClassifyExitAsync(UdpClient socket, List<MappingSample> observations,
        List<FilteringSample> filtering, int localPort, bool changingExit, CancellationToken token)
    {
        var first = observations[0];
        var distinctServers = observations.Select(o => o.Server.Address).Distinct().Count();
        var mappedCount = observations.Select(o => o.Mapped).Distinct().Count();
        NatExitReport Result(NatKind kind, string evidence) => new(first.Mapped.Address.ToString(), kind, evidence,
            observations.Select(o => o.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), distinctServers,
            observations.Select(o => o.Mapped.Port).Distinct().Order().ToArray(),
            string.Join(", ", observations.Select(o => new IPEndPoint(o.Local, localPort)).Distinct()));
        if (observations.Select(o => o.Local).Distinct().Count() > 1)
            return Result(NatKind.Unknown, "公网 IP 相同，但本地发送接口不同，映射不能直接比较。");
        if (changingExit || observations.GroupBy(o => o.Server).Any(g => g.Select(o => o.Mapped).Distinct().Count() > 1))
            return Result(NatKind.Unknown, "同一目标在检测中出现映射变化，可能发生网络切换或映射重建，请复测。");
        if (observations.All(o => o.Mapped.Address.Equals(o.Local) && o.Mapped.Port == localPort))
            return Result(NatKind.OpenInternet, "公网地址及端口与本地出口一致；入站过滤仍可能由防火墙限制。");
        if (distinctServers < 2)
            return Result(NatKind.Unknown, "此出口只有一个有效目标 IP，需要至少两个不同 IP 的可用目标比较映射；其他出口的节点不参与此组判断。");
        // Recheck the initial destination to distinguish destination-dependent mapping
        // from a changing network/mapping during the measurement.
        Reply? recheck;
        try { recheck = await ExchangeAsync(socket, first.Server, 0, null, token).ConfigureAwait(false); }
        catch (SocketException) { recheck = null; }
        if (recheck?.Mapped is null || recheck.Error || !recheck.Mapped.Equals(first.Mapped))
            return Result(NatKind.Unknown, "此出口首节点复测失败或映射变化，请重试以排除网络切换与映射过期。");
        if (mappedCount > 1)
            return Result(NatKind.Symmetric, "同一公网 IP 下，同一本地 UDP 端口访问不同目标时产生不同映射端口，首节点复测一致。");
        var filters = filtering.Where(f => f.Local.Equals(first.Local) && f.Kind != NatKind.Unknown).Select(f => f.Kind).Distinct().ToArray();
        if (filters.Length != 1)
            return Result(NatKind.Unknown, filters.Length > 1
                ? "节点的过滤测试结果不一致，请复测。"
                : "此出口在不同目标间的映射端口一致，首节点复测稳定（锥形候选）；缺少属于此出口的有效过滤证据，暂不能区分三种锥形 NAT。");
        var kind = filters.Single();
        return Result(kind, "此出口在不同目标间的映射端口一致，首节点复测稳定。" + (kind switch
        {
            NatKind.FullCone => "未事先发送到备用 IP / 端口，也收到其响应。",
            NatKind.RestrictedCone => "不同 IP 的响应超时，同 IP 的不同端口响应成功（基于超时推断）。",
            _ => "不同 IP / 端口的响应均超时，原来源复测成功（基于超时推断，丢包或防火墙可能影响结果）。"
        }));
    }

    private async Task<FilteringSample?> TestFilteringAsync(IPEndPoint primary, CancellationToken token)
    {
        // A fresh socket has not contacted the alternate IP/port and therefore has
        // not opened a pinhole that would invalidate restricted-cone filtering tests.
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var baseline = await ExchangeAsync(socket, primary, 0, null, token).ConfigureAwait(false);
        if (baseline?.Mapped is null || baseline.Error || !ValidAlternate(primary, baseline.Other)) return null;
        var local = LocalAddress(primary);
        FilteringSample Result(NatKind kind) => new(baseline.Mapped.Address, local, kind);
        var other = baseline.Other!;
        var changed = await ExchangeAsync(socket, primary, 6, other, token).ConfigureAwait(false);
        if (changed is not null)
            return Result(!changed.Error && changed.Source.Equals(other) && Equals(changed.Mapped, baseline.Mapped)
                ? NatKind.FullCone : NatKind.Unknown); // A server ignoring CHANGE-REQUEST is not evidence.
        var changedPort = new IPEndPoint(primary.Address, other.Port);
        var portReply = await ExchangeAsync(socket, primary, 2, changedPort, token).ConfigureAwait(false);
        if (portReply is not null)
            return Result(!portReply.Error && portReply.Source.Equals(changedPort) && Equals(portReply.Mapped, baseline.Mapped)
                ? NatKind.RestrictedCone : NatKind.Unknown);
        var control = await ExchangeAsync(socket, primary, 0, null, token).ConfigureAwait(false);
        return Result(control is { Error: false } && Equals(control.Mapped, baseline.Mapped) ? NatKind.PortRestrictedCone : NatKind.Unknown);
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
