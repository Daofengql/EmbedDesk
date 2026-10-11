namespace EmbeddedDebugger.Infrastructure.Networking;

public sealed record PublicIperf3Server(string Name, string Host, int FirstPort, int LastPort, string Source)
{
    public int DefaultPort { get; init; }
    public string Description => $"{Host} · TCP / UDP · 可用端口范围 {FirstPort}–{LastPort}。预设端口已于 2026-10-11 完成正反向互通测试；当前可用性取决于线路及节点负载。";
}

public static class PublicIperf3Servers
{
    // Published TCP/UDP endpoints, reviewed 2026-10-11. Opening the tool sends no traffic.
    public static IReadOnlyList<PublicIperf3Server> All { get; } = Array.AsReadOnly(new[]
    {
        new PublicIperf3Server("Scaleway · 法国", "ping.online.net", 5200, 5209, "https://iperf.fr/iperf-servers.php") { DefaultPort = 5202 },
        new PublicIperf3Server("Moji · 法国", "iperf3.moji.fr", 5200, 5240, "https://iperf.fr/iperf-servers.php") { DefaultPort = 5203 },
        new PublicIperf3Server("Uztelecom · 乌兹别克斯坦", "speedtest.uztelecom.uz", 5200, 5209, "https://iperf.fr/iperf-servers.php") { DefaultPort = 5203 },
        new PublicIperf3Server("Hurricane Electric · 美国", "iperf.he.net", 5201, 5201, "https://iperf.fr/iperf-servers.php") { DefaultPort = 5201 }
    });
}
