using System.Globalization;
using EmbeddedDebugger.Core.Models;

namespace EmbeddedDebugger.Infrastructure.Remote;

internal sealed class LinuxPerformanceReader
{
    // Single quotes protect awk's field references from the remote login shell.
    internal const string Command = """
        LC_ALL=C awk '
        FILENAME=="/proc/stat" && $1=="cpu" {for(i=2;i<=9;i++) total+=$i; idle=$5+$6; printf "TOTAL=%.0f\nIDLE=%.0f\n",total,idle}
        FILENAME=="/proc/stat" && $1 ~ /^cpu[0-9]+$/ {cores++}
        FILENAME=="/proc/meminfo" {mem[$1]=$2}
        FILENAME=="/proc/loadavg" {printf "LOAD=%s\n",$1}
        FILENAME=="/proc/uptime" {printf "UP=%s\n",$1}
        FILENAME=="/proc/net/dev" && $1 ~ /:$/ && $1!="lo:" {rx+=$2;tx+=$10}
        END {
          available=("MemAvailable:" in mem)?mem["MemAvailable:"]:mem["MemFree:"]+mem["Buffers:"]+mem["Cached:"]+mem["SReclaimable:"]-mem["Shmem:"];
          if(mem["MemTotal:"]>0) printf "MEM=%.2f\n",100*(mem["MemTotal:"]-available)/mem["MemTotal:"];
          printf "CORES=%d\nRX=%.0f\nTX=%.0f\n",cores,rx,tx;
        }' /proc/stat /proc/meminfo /proc/loadavg /proc/uptime /proc/net/dev
        """;

    private double? _previousTotal;
    private double? _previousIdle;

    public RemotePerformanceSnapshot Parse(string output)
    {
        var values = output.Split('\n').Select(line => line.Trim().Split('=', 2))
            .Where(parts => parts.Length == 2).GroupBy(parts => parts[0])
            .ToDictionary(group => group.Key, group => group.Last()[1], StringComparer.Ordinal);
        double? Number(string key) => values.TryGetValue(key, out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value >= 0 ? value : null;
        var total = Number("TOTAL");
        var idle = Number("IDLE");
        var memory = Number("MEM");
        if (total is null || idle is null || memory is null) throw new IOException("未获取到有效的 /proc 性能数据");
        double? cpu = _previousTotal is double previous && total > previous && _previousIdle is double previousIdle && idle >= previousIdle
            ? Math.Clamp(100 * (1 - (idle.Value - previousIdle) / (total.Value - previous)), 0, 100) : null;
        _previousTotal = total;
        _previousIdle = idle;
        var seconds = Number("UP");
        var uptime = seconds is double s && s < TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(s) : (TimeSpan?)null;
        return new RemotePerformanceSnapshot(DateTimeOffset.Now, cpu, Math.Clamp(memory.Value, 0, 100), Number("LOAD"),
            (long?)Number("RX"), (long?)Number("TX"),
            uptime is { } span ? $"{(int)span.TotalDays}天 {span.Hours:00}:{span.Minutes:00}:{span.Seconds:00}" : null,
            cpu is null ? "CPU 采样中" : "Linux /proc") { ProcessorCount = (int?)Number("CORES") };
    }
}
