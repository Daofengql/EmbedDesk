using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using EmbedDesk.App.ViewModels;
using EmbedDesk.Infrastructure.Connections;

namespace EmbedDesk.App.SmokeTests;

internal static class RelaySelectionChecks
{
    internal static async Task RunAsync(string relayUrl)
    {
        await using var fixture = new HealthFixture();
        var defaultServer = new RelayServerProfile { Name = "默认测试中继", Url = fixture.Url + "/default" };
        var backup = new RelayServerProfile { Name = "自定义备用", Url = fixture.Url + "/backup" };
        var free = new RelayServerProfile { Name = "免费测试节点", Url = fixture.Url + "/free", BuiltIn = true };
        var preferences = new ConnectionPreferences { RelayServers = [backup, free, defaultServer], DefaultRelayServerId = defaultServer.Id };
        var transport = new DeviceNodeOptions();
        var selected = await ConnectionCenterViewModel.SelectRelayServerAsync(preferences, transport, CancellationToken.None);
        DeviceConnectionsChecks.Require(selected?.Id == defaultServer.Id && fixture.Requests == 1, "healthy default relay is preferred without waiting for other nodes");
        fixture.DefaultAvailable = false;
        selected = await ConnectionCenterViewModel.SelectRelayServerAsync(preferences, transport, CancellationToken.None);
        DeviceConnectionsChecks.Require(selected?.Id == free.Id, "unavailable default falls back to a free node before a custom backup");
        var invalid = await RelayHealthProbe.CheckAsync(transport with { ServerUrl = fixture.Url + "/invalid" });
        DeviceConnectionsChecks.Require(!invalid.Available, "HTTP 200 with an incompatible health response is rejected");
        using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            try { await RelayHealthProbe.CheckAsync(transport with { ServerUrl = fixture.Url + "/slow" }, cancel.Token); throw new InvalidOperationException("health cancellation ignored"); }
            catch (OperationCanceledException) { Console.WriteLine("PASS relay health probe cancellation"); }
        }
        var directory = Path.Combine(Path.GetTempPath(), "EmbedDesk-Relay-Selection", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var store = new AppSettingsStore(directory);
            var real = new RelayServerProfile { Name = "Go 回环中继", Url = relayUrl };
            store.Update(s => s.ConnectionPreferences = new() { AutoConnect = false, RelayServers = [real], DefaultRelayServerId = real.Id });
            await using var node = new DeviceNode(new FakeSerialService());
            await using var model = new ConnectionCenterViewModel(node, store);
            await model.InitializeAsync(); await DeviceConnectionsChecks.UntilAsync(() => node.IsOnline);
            DeviceConnectionsChecks.Require(model.RelayName == real.Name && node.IsOnline, "startup automatically registers despite the retired AutoConnect=false preference");
            var identity = node.DeviceId;
            store.Update(s => s.ConnectionPreferences = s.ConnectionPreferences with { DefaultRelayServerId = real.Id });
            DeviceConnectionsChecks.Require(node.DeviceId == identity && node.IsOnline, "saving preferences preserves the active node");
            await model.StopAsync();
            store.Update(s => s.ConnectionPreferences = s.ConnectionPreferences with
            { RelayServers = [new() { Name = "慢响应中继", Url = fixture.Url + "/slow" }], ServerUrl = "" });
            var starting = model.StartAsync(); await Task.Delay(80);
            var watch = Stopwatch.StartNew(); await model.StopAsync(); await starting;
            DeviceConnectionsChecks.Require(watch.Elapsed < TimeSpan.FromSeconds(1) && !node.IsOnline, "stopping startup cancels a pending relay selection without opening a late connection");
            foreach (var name in new[] { "identity-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(relayUrl.TrimEnd('/'))))[..24] })
                model.SaveSecret(name, "");
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class HealthFixture : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _life = new();
        private readonly ConcurrentDictionary<int, Task> _clients = new();
        private readonly Task _accept;
        private int _sequence, _requests;
        public string Url => "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;
        public bool DefaultAvailable { get; set; } = true;
        public int Requests => _requests;
        public HealthFixture() { _listener.Start(); _accept = AcceptAsync(); }
        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync(_life.Token); var id = Interlocked.Increment(ref _sequence);
                    var task = ReplyAsync(client); _clients[id] = task;
                    _ = task.ContinueWith(_ => _clients.TryRemove(id, out var ignored), TaskScheduler.Default);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException) { }
        }
        private async Task ReplyAsync(TcpClient client)
        {
            using var owned = client;
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                var first = await reader.ReadLineAsync(_life.Token);
                for (var count = 0; count < 40; count++) { if (string.IsNullOrEmpty(await reader.ReadLineAsync(_life.Token))) break; }
                Interlocked.Increment(ref _requests);
                if (first?.Contains("/slow/", StringComparison.Ordinal) == true) await Task.Delay(5000, _life.Token);
                var failed = !DefaultAvailable && first?.Contains("/default/", StringComparison.Ordinal) == true;
                var body = first?.Contains("/invalid/", StringComparison.Ordinal) == true ? "{}" : "{\"status\":\"ok\",\"protocol\":1}";
                var bytes = Encoding.ASCII.GetBytes($"HTTP/1.1 {(failed ? "503 Unavailable" : "200 OK")}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
                await stream.WriteAsync(bytes, _life.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException) { }
        }
        public async ValueTask DisposeAsync()
        { _life.Cancel(); _listener.Stop(); await _accept; await Task.WhenAll(_clients.Values); _life.Dispose(); }
    }
}
