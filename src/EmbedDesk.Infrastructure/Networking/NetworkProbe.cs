using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using EmbedDesk.Core.Abstractions;

namespace EmbedDesk.Infrastructure.Networking;

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
                .Where(IsUnicast)
                .Where(a => target.AddressFamily switch
                {
                    NetworkProbeAddressFamily.IPv4 => a.AddressFamily == AddressFamily.InterNetwork,
                    NetworkProbeAddressFamily.IPv6 => a.AddressFamily == AddressFamily.InterNetworkV6,
                    _ => true
                })
                .OrderBy(a => a.AddressFamily != AddressFamily.InterNetwork).FirstOrDefault();
            if (address is null) return new(NetworkProbeOutcome.Unavailable, null, "没有可探测的单播地址（不探测监听、广播或组播地址）");
            var localAddress = ParseLocalAddress(target.LocalAddress, address.AddressFamily);
            if (target.TcpPort is int port)
            {
                using var client = new TcpClient(address.AddressFamily);
                if (localAddress is not null) client.Client.Bind(new IPEndPoint(localAddress, 0));
                var started = Stopwatch.GetTimestamp();
                await client.ConnectAsync(address, port, deadline.Token).ConfigureAwait(false);
                var source = localAddress is null ? string.Empty : $" · 出口 {localAddress}";
                return new(NetworkProbeOutcome.Success, Stopwatch.GetElapsedTime(started).TotalMilliseconds, $"TCP {new IPEndPoint(address, port)} · 握手成功{source}");
            }
            if (localAddress is not null)
                return await ProbeWithSystemPingAsync(address, localAddress, target.PacketSize, deadline.Token).ConfigureAwait(false);
            using var ping = new Ping();
            var packetSize = Math.Clamp(target.PacketSize, 0, 65500);
            var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(1500), new byte[packetSize], null, deadline.Token).ConfigureAwait(false);
            return reply.Status == IPStatus.Success
                ? new(NetworkProbeOutcome.Success, reply.RoundtripTime, $"ICMP {reply.Address} · {packetSize} 字节 · 已响应")
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

    private static IPAddress? ParseLocalAddress(string? value, AddressFamily family)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!IPAddress.TryParse(value, out var address) || address.AddressFamily != family)
            throw new ArgumentException("指定的网络出口与目标地址族不匹配。");
        return address;
    }

    private static async Task<NetworkProbeResult> ProbeWithSystemPingAsync(IPAddress address, IPAddress localAddress, int packetSize, CancellationToken cancellationToken)
    {
        packetSize = Math.Clamp(packetSize, 0, 65500);
        var start = Stopwatch.GetTimestamp();
        var info = new ProcessStartInfo
        {
            FileName = "ping.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add(address.AddressFamily == AddressFamily.InterNetwork ? "-4" : "-6");
        info.ArgumentList.Add("-n"); info.ArgumentList.Add("1");
        info.ArgumentList.Add("-w"); info.ArgumentList.Add("1500");
        info.ArgumentList.Add("-l"); info.ArgumentList.Add(packetSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        info.ArgumentList.Add("-S"); info.ArgumentList.Add(localAddress.ToString());
        info.ArgumentList.Add(address.ToString());
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new InvalidOperationException("无法启动系统 ping。");
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = (await outputTask.ConfigureAwait(false)).Trim();
            var error = (await errorTask.ConfigureAwait(false)).Trim();
            var detail = string.IsNullOrWhiteSpace(output) ? error : output;
            detail = string.Join(' ', detail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return process.ExitCode == 0
                ? new(NetworkProbeOutcome.Success, Stopwatch.GetElapsedTime(start).TotalMilliseconds, $"ICMP {address} · 出口 {localAddress} · {packetSize} 字节 · {detail}")
                : new(NetworkProbeOutcome.NoReply, null, $"ICMP {address} · 出口 {localAddress} · {detail}");
        }
        catch
        {
            if (!process.HasExited) process.Kill(true);
            throw;
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
