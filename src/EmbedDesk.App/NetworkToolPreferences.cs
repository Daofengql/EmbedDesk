using EmbedDesk.Infrastructure.Networking;

namespace EmbedDesk.App;

/// <summary>Last-used non-secret inputs for local network utilities. Results and payloads are never stored here.</summary>
public sealed record NetworkToolPreferences
{
    public CidrCalculatorPreferences CidrCalculator { get; init; } = new();
    public DnsQueryPreferences DnsQuery { get; init; } = new();
    public TcpPortScanPreferences TcpPortScan { get; init; } = new();
    public TlsDiagnosticPreferences TlsDiagnostic { get; init; } = new();
    public Iperf3ToolPreferences Iperf3 { get; init; } = new();
    public NetworkDiscoveryPreferences NetworkDiscovery { get; init; } = new();
    public NatDetectionPreferences NatDetection { get; init; } = new();
    public UpnpDiscoveryPreferences UpnpDiscovery { get; init; } = new();
}

public sealed record CidrCalculatorPreferences
{
    public string Input { get; init; } = "192.168.1.0/24";
}

public sealed record DnsQueryPreferences
{
    public string Query { get; init; } = "example.com";
    public string RecordType { get; init; } = "自动（域名 A / IP PTR）";
    public int QueryClassIndex { get; init; }
    public int TransportIndex { get; init; }
    public string DnsServer { get; init; } = string.Empty;
    public string DohEndpoint { get; init; } = "https://dns.alidns.com/dns-query";
    public int Port { get; init; } = 53;
    public int TimeoutMilliseconds { get; init; } = 2000;
    public int Retries { get; init; } = 1;
    public bool RecursionDesired { get; init; } = true;
}

public sealed record TcpPortScanPreferences
{
    public string Host { get; init; } = "127.0.0.1";
    public string Ports { get; init; } = "22,80,443,3389,5900,8080";
    public int AddressFamilyIndex { get; init; }
    public int TimeoutMilliseconds { get; init; } = 3000;
    public int Concurrency { get; init; } = 16;
}

public sealed record TlsDiagnosticPreferences
{
    public string Host { get; init; } = "example.com";
    public int Port { get; init; } = 443;
    public string ServerName { get; init; } = string.Empty;
    public int TimeoutMilliseconds { get; init; } = 10000;
    public bool CheckRevocation { get; init; }
}

public sealed record Iperf3ToolPreferences
{
    public int ModeIndex { get; init; }
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 5201;
    public int ProtocolIndex { get; init; }
    public int DirectionIndex { get; init; }
    public int DurationSeconds { get; init; } = 10;
    public int BlockSizeBytes { get; init; } = 131072;
    public string Rate { get; init; } = "20";
    public int RateUnitIndex { get; init; }
}

public sealed record NetworkDiscoveryPreferences
{
    public string LocalAddress { get; init; } = string.Empty;
    public int Concurrency { get; init; } = 64;
}

public sealed record NatDetectionPreferences
{
    public string Servers { get; init; } = string.Join(Environment.NewLine, NatDetector.DefaultServers);
    public bool ShowServerSettings { get; init; }
}

public sealed record UpnpDiscoveryPreferences
{
    public int SearchTypeIndex { get; init; }
}
