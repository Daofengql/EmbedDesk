using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using EmbeddedDebugger.Core.Abstractions;

namespace EmbeddedDebugger.Infrastructure.Networking;

/// <summary>Independent probes never write bytes to a debugging session's transport.</summary>
public sealed class NetworkProbe : INetworkProbe
{
    public async Task<NetworkProbeResult> ProbeAsync(NetworkProbeTarget target, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var addresses = IPAddress.TryParse(target.Host, out var literal)
                ? new[] { literal }
                : await Dns.GetHostAddressesAsync(target.Host, deadline.Token).ConfigureAwait(false);
            // Prefer IPv4, as the transport clients do, but support IPv6-only targets too.
            var address = addresses.Select(a => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a)
                .Where(IsUnicast).OrderBy(a => a.AddressFamily != AddressFamily.InterNetwork).FirstOrDefault();
            if (address is null) return new(NetworkProbeOutcome.Unavailable, null, "没有可探测的单播地址（不探测监听、广播或组播地址）");
            if (target.TcpPort is int port)
            {
                using var client = new TcpClient(address.AddressFamily);
                var started = Stopwatch.GetTimestamp();
                await client.ConnectAsync(address, port, deadline.Token).ConfigureAwait(false);
                return new(NetworkProbeOutcome.Success, Stopwatch.GetElapsedTime(started).TotalMilliseconds, $"TCP {new IPEndPoint(address, port)} · 握手成功");
            }
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(1500), new byte[32], null, deadline.Token).ConfigureAwait(false);
            return reply.Status == IPStatus.Success
                ? new(NetworkProbeOutcome.Success, reply.RoundtripTime, $"ICMP {reply.Address} · 已响应")
                : new(NetworkProbeOutcome.NoReply, null, $"ICMP {address} · {reply.Status}（可能被禁用或过滤）");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(NetworkProbeOutcome.NoReply, null, "探测超时（3 秒，含地址解析）");
        }
        catch (SocketException ex)
        {
            return new(NetworkProbeOutcome.NoReply, null, ex.SocketErrorCode == SocketError.ConnectionRefused ? "目标端口拒绝连接" : $"网络探测失败：{ex.SocketErrorCode}");
        }
        catch (Exception ex) when (ex is PingException or ArgumentException or InvalidOperationException or NotSupportedException or System.Security.SecurityException or System.ComponentModel.Win32Exception)
        {
            return new(NetworkProbeOutcome.Unavailable, null, $"无法测量：{ex.Message}");
        }
    }

    private static bool IsUnicast(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.IsIPv6Multicast) return false;
        var bytes = address.GetAddressBytes();
        return address.AddressFamily != AddressFamily.InterNetwork || bytes[0] is > 0 and < 224 && !address.Equals(IPAddress.Broadcast);
    }
}
