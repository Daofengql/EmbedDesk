using System.Diagnostics;
using EmbedDesk.App.ViewModels;
using EmbedDesk.Infrastructure.Connections;

namespace EmbedDesk.App.SmokeTests;

internal static class RelayLatencyChecks
{
    internal static async Task RunAsync()
    {
        await using var status = new RelayLatencyStatus(interval: TimeSpan.FromMilliseconds(25));
        foreach (var (milliseconds, color) in new[] { (0, "#4CC9B0"), (100, "#4CC9B0"), (101, "#F0C36D"), (200, "#F0C36D"), (201, "#FF8585") })
        {
            status.Apply(new(true, milliseconds, ""));
            DeviceConnectionsChecks.Require(status.Color == color && status.Text == (milliseconds == 0 ? "<1 ms" : milliseconds + " ms"),
                "relay latency thresholds retain a numeric reading and distinct colors");
        }
        await using var fixture = new RelaySelectionChecks.HealthFixture();
        await status.StartAsync(new() { ServerUrl = fixture.Url + "/default" }, true);
        await DeviceConnectionsChecks.UntilAsync(() => fixture.Requests >= 2 && status.Text.EndsWith(" ms", StringComparison.Ordinal));
        fixture.DefaultAvailable = false;
        await DeviceConnectionsChecks.UntilAsync(() => status.Text == "不可用");
        DeviceConnectionsChecks.Require(status.Color == "#9AA5AF" && status.Hint.Contains("503", StringComparison.Ordinal),
            "failed probes replace stale latency with an unavailable state and the real reason");
        fixture.DefaultAvailable = true;
        await DeviceConnectionsChecks.UntilAsync(() => status.Text.EndsWith(" ms", StringComparison.Ordinal));
        status.SetOnline(false);
        var paused = fixture.Requests;
        await Task.Delay(100);
        Console.WriteLine($"DEBUG offline text={status.Text} paused={paused} requests={fixture.Requests}");
        DeviceConnectionsChecks.Require(status.Text == "未连接" && fixture.Requests == paused, "offline relay latency pauses HTTP polling and clears the reading");
        status.SetOnline(true);
        await DeviceConnectionsChecks.UntilAsync(() => fixture.Requests > paused && status.Text.EndsWith(" ms", StringComparison.Ordinal));
        await status.StartAsync(new() { ServerUrl = fixture.Url + "/slow" }, true);
        await Task.Delay(60);
        var watch = Stopwatch.StartNew(); await status.StopAsync();
        DeviceConnectionsChecks.Require(watch.Elapsed < TimeSpan.FromSeconds(1) && status.Text == "未连接", "stopping cancels an in-flight latency request promptly");

        var oldResult = new TaskCompletionSource<RelayHealthResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<DeviceNodeOptions>();
        var inFlight = 0; var maximum = 0;
        await using var switching = new RelayLatencyStatus(async (options, _) =>
        {
            calls.Add(options); maximum = Math.Max(maximum, ++inFlight);
            try { return options.ServerUrl.EndsWith("/old", StringComparison.Ordinal) ? await oldResult.Task : new(true, 42, ""); }
            finally { inFlight--; }
        }, TimeSpan.FromMilliseconds(25));
        await switching.StartAsync(new() { ServerUrl = fixture.Url + "/old" }, true);
        var currentOptions = new DeviceNodeOptions { ServerUrl = fixture.Url + "/current", ProxyMode = "Custom", ProxyUrl = "http://127.0.0.1:1", ProxyUser = "test-proxy-user", ProxyPassword = "test-proxy-password" };
        var changed = switching.StartAsync(currentOptions, true);
        oldResult.SetResult(new(true, 999, "")); await changed;
        await DeviceConnectionsChecks.UntilAsync(() => calls.Count >= 3);
        DeviceConnectionsChecks.Require(switching.Text == "42 ms" && maximum == 1 && calls.Skip(1).All(options => options == currentOptions),
            "switching relays joins the old probe, rejects stale results and preserves active transport options without overlap");
        await switching.StopAsync();
        var stopped = calls.Count; await Task.Delay(100);
        DeviceConnectionsChecks.Require(calls.Count == stopped, "stopping latency polling leaves no background probes");
        Console.WriteLine("PASS periodic relay latency, colors, recovery, cancellation and stale-result isolation");
    }
}
