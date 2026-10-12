using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace EmbedDesk.Infrastructure.Networking;

public sealed record PortCheckResult(string Address, int Port, string Status, double ElapsedMilliseconds, string Detail);

public static class TcpPortChecker
{
    public static IReadOnlyList<int> ParsePorts(string input)
    {
        var ports = new SortedSet<int>();
        foreach (var part in input.Split([',', '，', ';', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var ends = part.Split('-');
            if (ends.Length is < 1 or > 2 || !int.TryParse(ends[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first) || first is < 1 or > 65535)
                throw new FormatException("端口应为 1–65535，可使用逗号分隔或范围，例如 22,80,443,8000-8010。");
            var last = first;
            if (ends.Length == 2 && (!int.TryParse(ends[1], NumberStyles.None, CultureInfo.InvariantCulture, out last) || last < first || last > 65535))
                throw new FormatException("端口范围无效。");
            if (last - first >= 1024) throw new FormatException("每次最多检测 1024 个端口。");
            for (var port = first; port <= last; port++) ports.Add(port);
            if (ports.Count > 1024) throw new FormatException("每次最多检测 1024 个端口。");
        }
        if (ports.Count == 0) throw new FormatException("请输入至少一个端口。");
        return ports.ToArray();
    }

    public static async Task<IReadOnlyList<PortCheckResult>> CheckAsync(string host, IReadOnlyList<int> ports,
        int timeoutMilliseconds, int concurrency, AddressFamily family = AddressFamily.Unspecified,
        IProgress<PortCheckResult>? progress = null, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("请输入主机名或 IP 地址。");
        if (timeoutMilliseconds is < 100 or > 60000 || concurrency is < 1 or > 64 || ports.Count is < 1 or > 1024 || ports.Any(p => p is < 1 or > 65535))
            throw new ArgumentException("超时为 100–60000 ms，并发为 1–64，每次最多 1024 个端口。");
        using var resolveTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        resolveTimeout.CancelAfter(timeoutMilliseconds);
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(host.Trim(), family, resolveTimeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("解析主机地址超时。"); }
        if (addresses.Length == 0) throw new ArgumentException("主机没有符合所选 IPv4 / IPv6 的地址。");
        // Test each resolved endpoint separately so a closed IPv6 port does not hide an open IPv4 port.
        var targets = addresses.Distinct().SelectMany(address => ports.Distinct().Select(port => new IPEndPoint(address, port))).ToArray();
        if (targets.Length > 4096) throw new ArgumentException("地址和端口组合超过 4096 项，请缩小端口范围。");
        var results = new ConcurrentBag<PortCheckResult>();
        await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = token }, async (endpoint, ct) =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeoutMilliseconds);
            var start = Stopwatch.GetTimestamp();
            var status = "开放"; var detail = "TCP 三次握手成功";
            try
            {
                using var client = new TcpClient(endpoint.AddressFamily);
                await client.ConnectAsync(endpoint, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { status = "超时"; detail = "无响应；可能被过滤或链路不可达"; }
            catch (SocketException ex)
            {
                status = ex.SocketErrorCode == SocketError.ConnectionRefused ? "拒绝" : "错误";
                detail = $"{ex.SocketErrorCode} · {ex.Message}";
            }
            ct.ThrowIfCancellationRequested();
            var result = new PortCheckResult(endpoint.Address.ToString(), endpoint.Port, status, Stopwatch.GetElapsedTime(start).TotalMilliseconds, detail);
            results.Add(result); progress?.Report(result);
        }).ConfigureAwait(false);
        return results.OrderBy(r => r.Address, StringComparer.Ordinal).ThenBy(r => r.Port).ToArray();
    }
}
