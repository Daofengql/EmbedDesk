using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using EmbedDesk.App.ViewModels;
using EmbedDesk.Infrastructure.Connections;

namespace EmbedDesk.App.SmokeTests;

internal static class RelayAdmissionChecks
{
    internal static async Task RunAsync()
    {
        CheckCodes();
        using var directory = new TemporarySettingsDirectory();
        var firstDirectory = Path.Combine(directory.DirectoryPath, "first"); Directory.CreateDirectory(firstDirectory);
        var secondDirectory = Path.Combine(directory.DirectoryPath, "second"); Directory.CreateDirectory(secondDirectory);
        var firstStart = await DeviceConnectionsChecks.RelayStartAsync(firstDirectory, FreePort(), 1000000, "test-relay-token-a");
        var secondStart = await DeviceConnectionsChecks.RelayStartAsync(secondDirectory, FreePort(), 1000000, "test-relay-token-b");
        var firstUrl = await ReadUrlAsync(firstDirectory);
        var secondUrl = await ReadUrlAsync(secondDirectory);
        firstStart.ArgumentList.Add("--connection-code"); firstStart.ArgumentList.Add(firstUrl); firstStart.ArgumentList.Add("测试中继 A");
        using (var export = Process.Start(firstStart)!)
        {
            var output = export.StandardOutput.ReadToEndAsync(); var error = export.StandardError.ReadToEndAsync();
            await export.WaitForExitAsync(); var code = await output; await error;
            Require(export.ExitCode == 0 && !File.Exists(Path.Combine(firstDirectory, "relay.sqlite")), "server export exits without opening a database or listener");
            var imported = RelayConnectionInfo.Parse(code);
            Require(imported.Name == "测试中继 A" && imported.Url == firstUrl && imported.AdmissionToken == "test-relay-token-a", "Go-generated connection code imports into the desktop codec");
        }
        firstStart.ArgumentList.Clear();
        using var first = Process.Start(firstStart)!; using var second = Process.Start(secondStart)!;
        var outputs = new[] { first.StandardOutput.ReadToEndAsync(), first.StandardError.ReadToEndAsync(), second.StandardOutput.ReadToEndAsync(), second.StandardError.ReadToEndAsync() };
        using var store = new AppSettingsStore(Path.Combine(directory.DirectoryPath, "settings"));
        var a = new RelayServerProfile { Name = "测试中继 A", Url = firstUrl };
        var b = new RelayServerProfile { Name = "测试中继 B", Url = secondUrl };
        store.Update(s => s.ConnectionPreferences = new() { RelayServers = [a, b], DefaultRelayServerId = a.Id, PreferDirect = false });
        await using var node = new DeviceNode(new FakeSerialService());
        await using var model = new ConnectionCenterViewModel(node, store);
        try
        {
            model.SaveSecret("admission", "test-legacy-token");
            Require(model.ReadRelayAdmissionToken(a) == "test-legacy-token" && model.ReadRelayAdmissionToken(b) == "test-legacy-token"
                && model.ReadRelayAdmissionToken(new() { Name = "新中继", Url = firstUrl }).Length == 0,
                "legacy global token applies only to existing saved profiles before migration");
            model.SaveRelayAdmissionToken(a, "test-relay-token-a"); model.SaveRelayAdmissionToken(b, "test-relay-token-b");
            store.Update(s => s.ConnectionPreferences = s.ConnectionPreferences with { RelayTokensMigrated = true }); store.SaveNow();
            using (var reload = new AppSettingsStore(Path.GetDirectoryName(store.FilePath)))
                Require(reload.Snapshot().ConnectionPreferences.RelayTokensMigrated, "relay admission migration flag survives reload");
            var json = await File.ReadAllTextAsync(store.FilePath);
            Require(!json.Contains("test-relay-token", StringComparison.Ordinal) && !json.Contains("test-legacy-token", StringComparison.Ordinal), "normal JSON settings contain no admission tokens");
            using (var lockedSettings = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try
                {
                    model.SaveConnectionPreferences(model.Preferences with { DefaultRelayServerId = b.Id },
                        new Dictionary<string, string> { [a.Id] = "test-uncommitted-token", [b.Id] = "" });
                    throw new InvalidOperationException("A locked settings file was reported as saved.");
                }
                catch (IOException) { }
                Require(model.ReadRelayAdmissionToken(a) == "test-relay-token-a" && model.ReadRelayAdmissionToken(b) == "test-relay-token-b"
                    && model.ReadSecret("admission") == "test-legacy-token" && model.Preferences.DefaultRelayServerId == a.Id,
                    "failed settings writes restore relay tokens, legacy secret and previous preferences");
            }
            store.SaveNow();
            await WaitForHealthAsync(firstUrl); await WaitForHealthAsync(secondUrl);
            await model.StartAsync(); await DeviceConnectionsChecks.UntilAsync(() => node.IsOnline);
            Require(model.RelayName == a.Name, "default relay registers with its own admission token");
            await model.StopAsync();
            store.Update(s => s.ConnectionPreferences = s.ConnectionPreferences with { DefaultRelayServerId = b.Id });
            await model.StartAsync(); await DeviceConnectionsChecks.UntilAsync(() => node.IsOnline);
            Require(model.RelayName == b.Name, "switching relay uses the second token rather than the first or legacy global token");
            await model.StopAsync();
            model.SaveRelayAdmissionToken(b, "test-relay-token-a");
            await model.StartAsync(); await DeviceConnectionsChecks.UntilAsync(() => node.Status.StartsWith("中继连接失败", StringComparison.Ordinal));
            Require(!node.IsOnline && node.Status.Contains("401", StringComparison.Ordinal), "another relay's token is refused by registration");
            await model.StopAsync();
            model.SaveRelayAdmissionToken(b, string.Empty);
            Require(model.ReadRelayAdmissionToken(b).Length == 0, "clearing a migrated token does not resurrect the global secret");
            model.SaveRelayAdmissionToken(b, "test-relay-token-b");
            first.Kill(); await first.WaitForExitAsync();
            store.Update(s => s.ConnectionPreferences = s.ConnectionPreferences with { DefaultRelayServerId = a.Id });
            await model.StartAsync(); await DeviceConnectionsChecks.UntilAsync(() => node.IsOnline);
            Require(model.RelayName == b.Name, "unavailable default falls back with the backup relay's token");
            await model.StopAsync();
        }
        finally
        {
            await model.StopAsync();
            foreach (var profile in new[] { a, b })
            {
                model.SaveRelayAdmissionToken(profile, string.Empty);
                model.SaveSecret("identity-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(profile.Url)))[..24], string.Empty);
            }
            model.SaveSecret("admission", string.Empty);
            foreach (var process in new[] { first, second }) { if (!process.HasExited) process.Kill(); await process.WaitForExitAsync(); }
            await Task.WhenAll(outputs);
        }
        Console.WriteLine("PASS per-relay admission, legacy migration, import code and fallback isolation checks");
    }

    private static void CheckCodes()
    {
        var info = RelayConnectionInfo.Parse(" \r\n" + RelayConnectionInfo.Encode(" 中继 ", "https://relay.example.test/base/", "test-token+/=") + "\r\n");
        Require(info.Name == "中继" && info.Url == "https://relay.example.test/base" && info.AdmissionToken == "test-token+/=", "connection code round-trips name, address and token");
        Require(RelayConnectionInfo.Parse(RelayConnectionInfo.Encode("开放中继", "http://127.0.0.1:7080", "")).AdmissionToken.Length == 0, "open relays can omit a token");
        Require(RelayConnectionInfo.Parse(RelayConnectionInfo.Encode("Relay", "https://relay.example.test", new string('x', 2048))).AdmissionToken.Length == 2048,
            "maximum-length ordinary token round-trips within the code size limit");
        Reject(() => RelayConnectionInfo.Encode("Relay", "https://relay.example.test", new string('<', 2048)));
        foreach (var invalid in new[] { "", "EMBEDDESK-FILES:abc", RelayConnectionInfo.Prefix + "%%%", new string('x', 17 * 1024) }) Reject(() => RelayConnectionInfo.Parse(invalid));
        foreach (var json in new[] {
            "null", "{\"version\":2,\"name\":\"Relay\",\"url\":\"https://relay.example.test\",\"admissionToken\":\"\"}",
            "{\"version\":1,\"name\":\"Relay\",\"url\":\"https://relay.example.test\"}",
            "{\"version\":1,\"name\":\"Relay\",\"url\":\"https://relay.example.test\",\"admissionToken\":\"\",\"AdmissionToken\":\"duplicate\"}",
            "{\"version\":1,\"name\":\"Relay\",\"url\":\"https://relay.example.test\",\"admissionToken\":\"\",\"unexpected\":true}" })
            Reject(() => RelayConnectionInfo.Parse(RelayConnectionInfo.Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json))));
        foreach (var address in new[] { "file:///relay", "https://user:secret@relay.example.test", "https://relay.example.test?token=test", "https://relay.example.test#fragment", "http://127.0.0.1:0" })
            Reject(() => RelayConnectionInfo.Encode("Relay", address, ""));
        foreach (var token in new[] { "line\r\nbreak", "with space", "非ASCII", new string('x', 2049) }) Reject(() => RelayConnectionInfo.Encode("Relay", "https://relay.example.test", token));
        Console.WriteLine("PASS bounded relay code parsing and malformed/unsafe input rejection");
    }
    private static async Task<string> ReadUrlAsync(string directory)
    { using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "relay.json"))); return "http://" + json.RootElement.GetProperty("listen").GetString(); }
    private static async Task WaitForHealthAsync(string url)
    {
        var watch = Stopwatch.StartNew();
        while (!(await RelayHealthProbe.CheckAsync(new() { ServerUrl = url })).Available)
        { if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Test relay did not become healthy."); await Task.Delay(100); }
    }
    private static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    private static void Reject(Action action) { try { action(); } catch (FormatException) { return; } throw new InvalidOperationException("Invalid relay connection code was accepted."); }
    private static void Require(bool condition, string message) => DeviceConnectionsChecks.Require(condition, message);
}
