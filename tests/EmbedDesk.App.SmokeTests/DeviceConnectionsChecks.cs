using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbedDesk.Core.Abstractions;
using EmbedDesk.Core.Models;
using EmbedDesk.Infrastructure.Connections;
using EmbedDesk.Infrastructure.Serial;
using EmbedDesk.App.ViewModels;

namespace EmbedDesk.App.SmokeTests;
internal static class DeviceConnectionsChecks
{
    public static async Task RunMappingsAsync()
    {
        var url = Environment.GetEnvironmentVariable("EMBEDDESK_TEST_RELAY_URL");
        if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("Set EMBEDDESK_TEST_RELAY_URL to an isolated test relay before running this check.");
        var admission = Environment.GetEnvironmentVariable("EMBEDDESK_TEST_RELAY_ADMISSION_KEY") ?? string.Empty;
        CheckBandwidthDisplay();
        await CheckConnectionRolesAsync(url, admission);
        await CheckViewModelAsync(url, admission);
        await CheckPathAsync(url, false, admission: admission);
        await CheckPathAsync(url, true, admission: admission);
        await CheckUdpPathAsync(url, false, admission);
        await CheckUdpPathAsync(url, true, admission);
    }
    public static async Task RunAsync()
    {
        var root = FindRepository(); var directory = Path.Combine(Path.GetTempPath(), "EmbedDesk-Node-Test", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var relayPath = Path.Combine(root, "artifacts", "relay-test.exe"); if (!File.Exists(relayPath)) throw new IOException("Build server to artifacts/relay-test.exe before this check.");
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        var start = await RelayStartAsync(directory, port, 1000000);
        using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await Task.Delay(500);
            CheckBandwidthDisplay();
            await CheckConnectionRolesAsync("http://127.0.0.1:" + port);
            await RelaySelectionChecks.RunAsync("http://127.0.0.1:" + port);
            await CheckPathAsync("http://127.0.0.1:" + port, false);
            await CheckPathAsync("http://127.0.0.1:" + port, true);
            await CheckPathAsync("http://127.0.0.1:" + port, true, true);
            await CheckUdpPathAsync("http://127.0.0.1:" + port, false);
            await CheckUdpPathAsync("http://127.0.0.1:" + port, true);
            await SerialFlowChecks.CheckAsync("http://127.0.0.1:" + port, "", false);
            await SerialFlowChecks.CheckAsync("http://127.0.0.1:" + port, "", true);
            await CheckViewModelAsync("http://127.0.0.1:" + port);
            await CheckReconnectAsync(start, process, "http://127.0.0.1:" + port);
            Console.WriteLine("PASS device node: Go relay, password/manual auth, direct/fallback, TCP/UDP mapping, serial failures and cleanup");
        }
        finally { if (!process.HasExited) process.Kill(); await process.WaitForExitAsync(); await Task.WhenAll(stdout, stderr); Directory.Delete(directory, true); }
    }
    private static void CheckBandwidthDisplay()
    {
        foreach (var (bits, expected) in new (long, string)[]
        {
            (0, "0 bps"), (999, "999 bps"), (1000, "1 Kbps"), (8000, "8 Kbps"),
            (1_000_000, "1 Mbps"), (10_000_000, "10 Mbps"), (1_000_000_000, "1 Gbps"),
            (4_096_000_000, "4.096 Gbps"), (100_000_000_000, "100 Gbps"), (1_000_000_000_000, "1 Tbps")
        }) Require(DeviceNode.FormatBandwidth(bits) == expected, "available bandwidth uses decimal bps units: " + expected);
    }
    private static async Task CheckConnectionRolesAsync(string url, string admission = "")
    {
        await using var a = new DeviceNode(new FakeSerialService());
        await using var b = new DeviceNode(new FakeSerialService());
        await using var c = new DeviceNode(new FakeSerialService());
        await using var d = new DeviceNode(new FakeSerialService());
        const string password = "test-role-password";
        foreach (var node in new[] { a, b, c, d }) await node.StartAsync(Options(url, password, false, admission));
        await UntilAsync(() => new[] { a, b, c, d }.All(node => node.IsOnline));
        Require(a.Status.Contains("可用带宽 1 Mbps", StringComparison.Ordinal) && !a.Status.Contains("额度", StringComparison.Ordinal),
            "online status reports available bandwidth without the old quota label");
        using var requestLife = new CancellationTokenSource();
        var pending = a.ConnectPeerAsync(b.DeviceId, "", requestLife.Token);
        await UntilAsync(() => b.PendingApprovals.Count == 1);
        Require(!b.CanInitiateConnection, "pending incoming approval reserves the receiving role");
        await ExpectAsync<InvalidOperationException>(() => b.ConnectPeerAsync(c.DeviceId, password));
        await ExpectAsync<IOException>(() => c.ConnectPeerAsync(a.DeviceId, password));
        Require(a.PendingApprovals.Count == 0 && b.Peers.All(peer => peer.IsOwner), "pending outgoing request rejects reverse incoming connections");
        requestLife.Cancel(); await ExpectAsync<OperationCanceledException>(() => pending);
        await UntilAsync(() => a.Peers.Count == 0 && b.Peers.Count == 0);
        Require(b.CanInitiateConnection, "canceling authorization releases the receiving role");
        await a.ConnectPeerAsync(b.DeviceId, password);
        await UntilAsync(() => b.Peers.Any(peer => peer.State == "已连接"));
        await ExpectAsync<InvalidOperationException>(() => b.ConnectPeerAsync(c.DeviceId, password));
        await ExpectAsync<IOException>(() => c.ConnectPeerAsync(a.DeviceId, password));
        await a.ConnectPeerAsync(c.DeviceId, password);
        await d.ConnectPeerAsync(b.DeviceId, password);
        Require(a.Peers.Count == 2 && a.Peers.All(peer => !peer.IsOwner) && b.Peers.Count == 2 && b.Peers.All(peer => peer.IsOwner),
            "multiple connections in the same direction remain supported without mixing roles");
        foreach (var peer in a.Peers) await a.DisconnectPeerAsync(peer.SessionId);
        foreach (var peer in d.Peers) await d.DisconnectPeerAsync(peer.SessionId);
        await UntilAsync(() => new[] { a, b, c, d }.All(node => node.Peers.Count == 0));
        await b.ConnectPeerAsync(a.DeviceId, password);
        Require(b.Peers.All(peer => !peer.IsOwner), "after disconnect the former receiver can become an initiator");
        await b.DisconnectPeerAsync(b.Peers.Single().SessionId);
        await UntilAsync(() => a.Peers.Count == 0 && b.Peers.Count == 0);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var attempts = await Task.WhenAll(ConnectOrFailAsync(a, b.DeviceId), ConnectOrFailAsync(b, a.DeviceId));
            Require(attempts.Count(success => success) <= 1, "simultaneous cross connections cannot establish both directions");
            Require(!a.Peers.Select(peer => peer.IsOwner).Distinct().Skip(1).Any() && !b.Peers.Select(peer => peer.IsOwner).Distinct().Skip(1).Any(),
                "simultaneous opposite requests never mix connection roles");
            foreach (var peer in a.Peers) await a.DisconnectPeerAsync(peer.SessionId);
            await UntilAsync(() => a.Peers.Count == 0 && b.Peers.Count == 0);
            if (!attempts.Any(success => success))
            {
                await a.ConnectPeerAsync(b.DeviceId, password);
                await a.DisconnectPeerAsync(a.Peers.Single().SessionId);
                await UntilAsync(() => a.Peers.Count == 0 && b.Peers.Count == 0);
            }
        }
        for (var attempt = 0; attempt < 6; attempt++)
        {
            using var cancel = new CancellationTokenSource();
            var connecting = a.ConnectPeerAsync(b.DeviceId, "", cancel.Token);
            await UntilAsync(() => b.PendingApprovals.Count == 1);
            var authorizing = b.ApproveAsync(b.PendingApprovals.Single().SessionId, true);
            cancel.Cancel();
            try { await connecting; }
            catch (OperationCanceledException) { }
            foreach (var peer in a.Peers) await a.DisconnectPeerAsync(peer.SessionId);
            try { await authorizing; }
            catch (Exception ex) when (ex is InvalidOperationException or IOException) { }
            await UntilAsync(() => a.Peers.Count == 0 && b.Peers.Count == 0);
            Require(a.CanInitiateConnection && b.CanInitiateConnection, "cancel racing with approval releases late handshake sessions and both roles");
        }
        Console.WriteLine("PASS connection role exclusion through authorization, active sessions, cancellation and opposite requests");
        async Task<bool> ConnectOrFailAsync(DeviceNode node, string id)
        {
            try { await node.ConnectPeerAsync(id, password); return true; }
            catch (Exception ex) when (ex is InvalidOperationException or IOException) { return false; }
        }
    }
    private static async Task CheckReconnectAsync(ProcessStartInfo start, Process running, string url)
    {
        using var serial = new SharedSerialPortService(new TestSerial());
        await using var owner = new DeviceNode(serial); await using var visitor = new DeviceNode(new FakeSerialService());
        var shared = new SharedDeviceService { Name = "reconnect serial", Kind = SharedServiceKind.Serial, PortName = "COM3" };
        var tcp = new SharedDeviceService { Name = "reconnect TCP", Kind = SharedServiceKind.Tcp };
        owner.ConfigureServices([shared, tcp]);
        await owner.StartAsync(Options(url, "test-reconnect-password", false) with { RetrySeconds = 1 });
        await visitor.StartAsync(Options(url, "test-reconnect-password", false) with { RetrySeconds = 1 });
        await UntilAsync(() => owner.IsOnline && visitor.IsOnline); var id = owner.DeviceId;
        await visitor.ConnectPeerAsync(id, "test-reconnect-password"); await UntilAsync(() => visitor.RemoteServices.Count == 2);
        var remote = visitor.RemoteServices.Single(s => s.Service.Kind == SharedServiceKind.Serial);
        await using var channel = visitor.CreateSerialChannel(remote.PortName, new() { PortName = remote.PortName }); await channel.OpenAsync();
        await visitor.MapPortAsync(visitor.Peers.Single().SessionId, tcp.Id, FreePort());
        running.Kill(); await running.WaitForExitAsync();
        await UntilAsync(() => !owner.IsOnline && !visitor.IsOnline && visitor.Peers.Count == 0 && visitor.Mappings.Count == 0 && !channel.IsOpen && !serial.IsInUse("COM3"));
        Require(serial.IsShared("COM3"), "relay loss clears active serial/mapping resources and retains software reservation");
        using var restarted = Process.Start(start)!; var stdout = restarted.StandardOutput.ReadToEndAsync(); var stderr = restarted.StandardError.ReadToEndAsync();
        try
        {
            await UntilAsync(() => owner.IsOnline && visitor.IsOnline);
            Require(owner.DeviceId == id && owner.Peers.Count == 0 && visitor.Peers.Count == 0 && visitor.Mappings.Count == 0, "WebSocket automatically reconnects with the same identity without restoring authorization or mappings");
            await visitor.ConnectPeerAsync(id, "test-reconnect-password"); await UntilAsync(() => visitor.RemoteServices.Count == 2);
            Require(visitor.Peers.Single().State == "已连接", "fresh authorization works after relay restart using this run's password");
            await visitor.StopAsync(); await owner.StopAsync();
        }
        finally { if (!restarted.HasExited) restarted.Kill(); await restarted.WaitForExitAsync(); await Task.WhenAll(stdout, stderr); }
    }
    internal static async Task<ProcessStartInfo> RelayStartAsync(string directory, int port, long bandwidth, string admissionToken = "")
    {
        var configPath = Path.Combine(directory, "relay.json");
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new { listen = "127.0.0.1:" + port, database = Path.Combine(directory, "relay.sqlite"), admissionKey = admissionToken, bandwidthBitsPerSecond = bandwidth }));
        var start = new ProcessStartInfo(Path.Combine(FindRepository(), "artifacts", "relay-test.exe")) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var key in new[] { "EMBEDDESK_LISTEN", "EMBEDDESK_DATABASE", "EMBEDDESK_ADMISSION_KEY", "EMBEDDESK_BANDWIDTH_BITS_PER_SECOND", "EMBEDDESK_TLS_CERT", "EMBEDDESK_TLS_KEY" }) start.Environment.Remove(key);
        start.Environment["EMBEDDESK_CONFIG"] = configPath;
        return start;
    }
    private static async Task CheckPathAsync(string url, bool direct, bool failDirect = false, string admission = "")
    {
        var hardware = new TestSerial(); using var serial = new SharedSerialPortService(hardware);
        await using var local = serial.CreateChannel(new() { PortName = "COM7" }); await local.OpenAsync();
        Expect<InvalidOperationException>(() => serial.SetSharedPorts(["COM7"])); await local.CloseAsync();
        await using var owner = new DeviceNode(serial); await using var visitor = new DeviceNode(new SharedSerialPortService(new TestSerial()));
        owner.OperationFailed += (_, error) => Console.WriteLine("OWNER " + error);
        visitor.OperationFailed += (_, error) => Console.WriteLine("VISITOR " + error);
        var ownerStatus = ""; var visitorStatus = "";
        owner.Changed += (_, _) => { if (owner.Status != ownerStatus) { ownerStatus = owner.Status; Console.WriteLine("OWNER STATUS " + ownerStatus); } };
        visitor.Changed += (_, _) => { if (visitor.Status != visitorStatus) { visitorStatus = visitor.Status; Console.WriteLine("VISITOR STATUS " + visitorStatus); } };
        var echo = new TcpListener(IPAddress.Loopback, 0); echo.Start(); using var echoLife = new CancellationTokenSource(); var echoTask = EchoAsync(echo, echoLife.Token);
        var held = new List<TcpClient>();
        try
        {
            var serialService = new SharedDeviceService { Name = "测试串口", Kind = SharedServiceKind.Serial, PortName = "COM3" };
            var tcpService = new SharedDeviceService { Name = "回环 TCP", Kind = SharedServiceKind.Tcp, TargetPort = ((IPEndPoint)echo.LocalEndpoint).Port };
            owner.ConfigureServices([serialService, tcpService]); Require(hardware.OpenCount == 1, "reservation does not pre-open COM");
            await using var blocked = serial.CreateChannel(new() { PortName = "COM3" }); await ExpectAsync<InvalidOperationException>(() => blocked.OpenAsync());
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
            await owner.StartAsync(Options(url, password, direct, admission)); await visitor.StartAsync(Options(url, password, direct, admission));
            await UntilAsync(() => owner.IsOnline && visitor.IsOnline);
            if (failDirect)
            {
                var listener = (TcpListener)typeof(DeviceNode).GetField("_listener", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(owner)!;
                for (var index = 0; index < 32; index++) { var pending = new TcpClient(); await pending.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port); held.Add(pending); }
                var accepting = typeof(DeviceNode).GetField("_directAcceptTasks", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(owner)!;
                await UntilAsync(() => (int)accepting.GetType().GetProperty("Count")!.GetValue(accepting)! == 32);
            }
            await ExpectAsync<UnauthorizedAccessException>(() => visitor.ConnectPeerAsync(owner.DeviceId, "wrong-test-code"));
            await UntilAsync(() => owner.Peers.Count == 0 && visitor.Peers.Count == 0);
            await visitor.ConnectPeerAsync(owner.DeviceId, password);
            await UntilAsync(() => visitor.RemoteServices.Count == 2);
            var peer = visitor.Peers.Single(); Require(peer.Path == (direct && !failDirect ? "局域网直连" : "公网中继"), failDirect ? "failed direct authentication automatically falls back to relay" : "negotiated path");
            foreach (var pending in held) pending.Dispose(); held.Clear();
            hardware.FailOpen = true; var remote = visitor.RemoteServices.Single(s => s.Service.Kind == SharedServiceKind.Serial);
            await using (var failed = visitor.CreateSerialChannel(remote.PortName, new() { PortName = remote.PortName, BaudRate = 230400 })) await ExpectAsync<IOException>(() => failed.OpenAsync());
            Require(visitor.Peers.Single().State == "已连接" && serial.IsShared("COM3") && !serial.IsInUse("COM3"), "open failure releases lease and preserves session and reservation");
            hardware.FailOpen = false;
            await using (var channel = visitor.CreateSerialChannel(remote.PortName, new() { PortName = remote.PortName, BaudRate = 230400, DtrEnable = false, RtsEnable = true }))
            {
                var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously); channel.DataReceived += (_, bytes) => received.TrySetResult(bytes.ToArray());
                await channel.OpenAsync(); Require(hardware.LastSettings?.BaudRate == 230400 && hardware.LastSettings.RtsEnable, "remote serial parameters arrive only on open");
                await channel.SendAsync(Encoding.ASCII.GetBytes("serial-test")); Require(Encoding.ASCII.GetString(await received.Task.WaitAsync(TimeSpan.FromSeconds(8))) == "serial-test", "serial duplex");
                hardware.FailSend = true;
                var fault = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                ((ISerialPortFaultNotifier)channel).Faulted += (_, reason) => fault.TrySetResult(reason);
                await channel.SendAsync(new byte[] { 1 });
                Require((await fault.Task.WaitAsync(TimeSpan.FromSeconds(8))).Contains("收发失败", StringComparison.Ordinal) && !channel.IsOpen, "remote hardware write failure faults the caller");
                hardware.FailSend = false;
                await UntilAsync(() => !serial.IsInUse("COM3"));
                var recovered = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                channel.DataReceived += (_, bytes) => recovered.TrySetResult(bytes.ToArray());
                await channel.OpenAsync(); await channel.SendAsync(new byte[] { 2, 3 });
                Require((await recovered.Task.WaitAsync(TimeSpan.FromSeconds(8))).SequenceEqual(new byte[] { 2, 3 }), "failed serial channel reopens with a fresh protocol channel and flow credits");
            }
            await UntilAsync(() => !serial.IsInUse("COM3"));
            await VirtualSerialChecks.CheckDeviceBridgeAsync(visitor, remote, () => hardware.LastSettings, () => serial.IsInUse("COM3"));
            var localPort = FreePort(); var mapping = await visitor.MapPortAsync(peer.SessionId, tcpService.Id, localPort);
            Require(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(endpoint => endpoint.Port == localPort)
                .All(endpoint => endpoint.Address.Equals(IPAddress.Loopback)), "TCP mapping only listens on IPv4 loopback");
            using var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, localPort); var payload = RandomNumberGenerator.GetBytes(direct && !failDirect ? 4 * 1024 * 1024 : 96 * 1024);
            var incoming = new byte[payload.Length]; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var reading = tcp.GetStream().ReadExactlyAsync(incoming, timeout.Token).AsTask(); await tcp.GetStream().WriteAsync(payload, timeout.Token); await reading;
            Require(incoming.SequenceEqual(payload), "mapped TCP multiplexed over the same peer");
            await visitor.UnmapPortAsync(mapping.Id); var eof = await tcp.GetStream().ReadAsync(new byte[1], timeout.Token); Require(eof == 0, "unmap closes established streams");
            await visitor.DisconnectPeerAsync(peer.SessionId); await UntilAsync(() => owner.Peers.Count == 0);
            Require(owner.IsOnline && visitor.IsOnline, "closing one peer preserves the global relay socket");
            var approval = new TaskCompletionSource<DeviceApproval>(TaskCreationOptions.RunContinuationsAsynchronously);
            owner.ApprovalRequested += (_, request) => approval.TrySetResult(request);
            var connecting = visitor.ConnectPeerAsync(owner.DeviceId, "");
            if (await Task.WhenAny(connecting, approval.Task).WaitAsync(TimeSpan.FromSeconds(10)) == connecting) await connecting;
            var request = await approval.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Require(!connecting.IsCompleted, "ID-only waits for owner approval"); await owner.ApproveAsync(request.SessionId, true); await connecting;
            await UntilAsync(() => visitor.RemoteServices.Count == 2);
            var activeSerial = visitor.RemoteServices.Single(s => s.Service.Kind == SharedServiceKind.Serial);
            await using var activeChannel = visitor.CreateSerialChannel(activeSerial.PortName, new() { PortName = activeSerial.PortName }); await activeChannel.OpenAsync();
            await visitor.StopAsync(); await UntilAsync(() => owner.Peers.Count == 0);
            Require(!activeChannel.IsOpen, "device disconnect faults the active remote serial");
            Require(serial.IsShared("COM3") && !serial.IsInUse("COM3"), "disconnect retains reservation without hardware ownership");
            owner.ConfigureServices([serialService, tcpService]);
        }
        finally { foreach (var pending in held) pending.Dispose(); echoLife.Cancel(); echo.Stop(); await echoTask; }
    }
    private static async Task CheckUdpPathAsync(string url, bool direct, string admission = "")
    {
        await using var owner = new DeviceNode(new SharedSerialPortService(new TestSerial()));
        await using var visitor = new DeviceNode(new SharedSerialPortService(new TestSerial()));
        using var udpEcho = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var tcpEcho = new TcpListener(IPAddress.Loopback, 0); tcpEcho.Start();
        using var echoLife = new CancellationTokenSource();
        var senders = new System.Collections.Concurrent.ConcurrentDictionary<IPEndPoint, int>();
        var udpTask = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var packet = await udpEcho.ReceiveAsync(echoLife.Token);
                    senders.AddOrUpdate(packet.RemoteEndPoint, 1, (_, count) => count + 1);
                    await udpEcho.SendAsync(packet.Buffer, packet.RemoteEndPoint, echoLife.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        });
        var tcpTask = EchoAsync(tcpEcho, echoLife.Token);
        try
        {
            var udpService = new SharedDeviceService { Name = "datagram echo", Kind = SharedServiceKind.Udp, TargetPort = ((IPEndPoint)udpEcho.Client.LocalEndPoint!).Port };
            var tcpService = new SharedDeviceService { Name = "stream echo", Kind = SharedServiceKind.Tcp, TargetPort = ((IPEndPoint)tcpEcho.LocalEndpoint).Port };
            owner.ConfigureServices([udpService, tcpService]);
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
            await owner.StartAsync(Options(url, password, direct, admission)); await visitor.StartAsync(Options(url, password, direct, admission));
            await UntilAsync(() => owner.IsOnline && visitor.IsOnline);
            await visitor.ConnectPeerAsync(owner.DeviceId, password); await UntilAsync(() => visitor.RemoteServices.Count == 2);
            var peer = visitor.Peers.Single(); var port = FreePort();
            var tcpMapping = await visitor.MapPortAsync(peer.SessionId, tcpService.Id, port);
            var udpMapping = await visitor.MapPortAsync(peer.SessionId, udpService.Id, port);
            Require(IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners().Where(endpoint => endpoint.Port == port)
                .All(endpoint => endpoint.Address.Equals(IPAddress.Loopback)), "UDP mapping only listens on IPv4 loopback");
            using var first = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); first.Connect(IPAddress.Loopback, port);
            using var second = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); second.Connect(IPAddress.Loopback, port);
            using var stream = new TcpClient(); await stream.ConnectAsync(IPAddress.Loopback, port);
            foreach (var size in new[] { 0, 1, 8192, 8193, 65507 })
                await ExchangeAsync(first, RandomNumberGenerator.GetBytes(size));
            Require(visitor.Mappings.Single(m => m.Id == udpMapping.Id).Kind == SharedServiceKind.Udp,
                $"UDP over {(direct ? "direct" : "relay")} preserves empty and 65,507-byte datagrams through bounded fragments");
            await Task.WhenAll(ExchangeAsync(first, Encoding.ASCII.GetBytes("first sender")), ExchangeAsync(second, Encoding.ASCII.GetBytes("second sender")));
            Require(senders.Count == 2, "UDP local senders use separate target sockets and receive only their own replies");
            await UntilAsync(() => visitor.Mappings.Single(m => m.Id == udpMapping.Id) is { } current
                && current.SentBytes == 8192 + 8193 + 65507 + 1 + 12 + 13 && current.ReceivedBytes == current.SentBytes);
            var stats = visitor.Mappings.Single(m => m.Id == udpMapping.Id);
            Require(stats.SentBytes == stats.ReceivedBytes, "UDP counters count payload bytes once after reassembly");
            await visitor.UnmapPortAsync(udpMapping.Id);
            using (var rebound = new UdpClient(new IPEndPoint(IPAddress.Loopback, port)))
                Require(visitor.Mappings.Count == 1, "unmap releases the UDP listener and retains the TCP mapping at the same port");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await stream.GetStream().WriteAsync(new byte[] { 42 }, timeout.Token);
            var echoed = new byte[1]; await stream.GetStream().ReadExactlyAsync(echoed, timeout.Token);
            Require(echoed[0] == 42, "stopping UDP preserves the shared peer and active TCP stream");
            udpMapping = await visitor.MapPortAsync(peer.SessionId, udpService.Id, port);
            await ExchangeAsync(first, Encoding.ASCII.GetBytes("fresh channel"));
            var burst = new byte[1024];
            for (var i = 0; i < 500; i++) await first.SendAsync(burst, echoLife.Token);
            await UntilAsync(() => visitor.Mappings.Single(m => m.Id == udpMapping.Id).DroppedDatagrams > 0);
            Require(visitor.Peers.Single().State == "已连接", "UDP burst drops are bounded and reported without disconnecting the peer");
            await visitor.StopAsync(); await UntilAsync(() => owner.Peers.Count == 0);
            using var finalBind = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
            Require(visitor.Mappings.Count == 0, "disconnect cancels queued UDP sends and releases the local listener");
        }
        finally { echoLife.Cancel(); tcpEcho.Stop(); await Task.WhenAll(udpTask, tcpTask); }

        static async Task ExchangeAsync(UdpClient client, byte[] payload)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await client.SendAsync(payload, timeout.Token);
            var response = await client.ReceiveAsync(timeout.Token);
            if (!response.Buffer.SequenceEqual(payload)) throw new InvalidOperationException("UDP packet was split, merged or returned to the wrong sender.");
        }
    }

    private static async Task CheckViewModelAsync(string url, string admission = "")
    {
        var directory = Path.Combine(Path.GetTempPath(), "EmbedDesk-Connection-VM-Test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var settings = new AppSettingsStore(directory);
            await using var owner = new DeviceNode(new SharedSerialPortService(new TestSerial()));
            await using var visitor = new DeviceNode(new SharedSerialPortService(new TestSerial()));
            var virtualEndpoints = new List<VirtualSerialChecks.Endpoint>();
            await using var model = new ConnectionCenterViewModel(visitor, settings, _ =>
            { var endpoint = new VirtualSerialChecks.Endpoint(); virtualEndpoints.Add(endpoint); return endpoint; });
            var tcpService = new SharedDeviceService { Name = "saved rule", Kind = SharedServiceKind.Tcp };
            var udpService = new SharedDeviceService { Name = "saved UDP rule", Kind = SharedServiceKind.Udp, TargetPort = 9000 };
            var serialService = new SharedDeviceService { Name = "saved virtual COM rule", Kind = SharedServiceKind.Serial, PortName = "COM3" };
            owner.ConfigureServices([tcpService, udpService, serialService]);
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
            await owner.StartAsync(Options(url, password, false, admission)); await visitor.StartAsync(Options(url, password, false, admission));
            await UntilAsync(() => owner.IsOnline && visitor.IsOnline);
            model.TargetId = owner.DeviceId;
            var request = model.ConnectAsync("");
            await UntilAsync(() => owner.PendingApprovals.Count == 1);
            Require(model.IsConnecting && !model.IsBusy, "waiting for approval leaves other connection controls available");
            model.CancelConnect(); await request;
            await UntilAsync(() => owner.Peers.Count == 0 && visitor.Peers.Count == 0);
            Require(!model.IsConnecting && visitor.IsOnline && model.Error.Contains("取消", StringComparison.Ordinal), "cancel removes pending authorization and preserves relay");
            await model.ConnectAsync(password); await UntilAsync(() => visitor.RemoteServices.Count == 3);
            var tcpRemote = visitor.RemoteServices.Single(s => s.Service.Kind == SharedServiceKind.Tcp);
            var udpRemote = visitor.RemoteServices.Single(s => s.Service.Kind == SharedServiceKind.Udp);
            var serialRemote = visitor.RemoteServices.Single(s => s.Service.Kind == SharedServiceKind.Serial);
            Require(tcpRemote.TargetEndpoint == "127.0.0.1:22" && udpRemote.TargetEndpoint == "127.0.0.1:9000" && serialRemote.TargetEndpoint == "COM3",
                "authenticated service catalog exposes the actual selected remote COM and network targets");
            Require((tcpRemote with { Service = tcpRemote.Service with { TargetHost = string.Empty, TargetPort = 0 } }).TargetDescription == "对端未提供目标地址",
                "legacy catalogs show an unknown target instead of inventing port zero");
            model.SelectedRemoteService = serialRemote;
            Require(await model.MapAsync(tcpRemote, FreePort()), "mapping dialog submits its explicit target despite another global selection");
            Require(model.MappingRules.Count == 1 && model.MappingRules[0].MappingId is not null && settings.Snapshot().PortMappingRules.Count == 1, "view model saves and activates the selected service rule");
            Require(model.MappingRules[0].Rule.RemoteEndpoint == "127.0.0.1:22" && model.MappingRules[0].DisplayName.Contains("127.0.0.1:22", StringComparison.Ordinal),
                "saved mapping describes both the local listener and remote target");
            await model.DisableRuleAsync(model.MappingRules[0]);
            Require(visitor.Mappings.Count == 0 && model.MappingRules[0].MappingId is null, "disabling a rule retains only its configuration");
            Require(await model.MapAsync(udpRemote, FreePort()), "explicit UDP selection retains its protocol");
            Require(model.MappingRules[1].MappingId is not null && settings.Snapshot().PortMappingRules[1].Kind == SharedServiceKind.Udp,
                "view model saves and enables UDP rules with their protocol");
            await model.DisableRuleAsync(model.MappingRules[1]);
            var beforeInvalid = model.MappingRules.Count;
            Require(!await model.MapAsync(tcpRemote with { SessionId = Guid.NewGuid().ToString("N") }, FreePort()) && model.MappingRules.Count == beforeInvalid,
                "stale dialog target cannot silently map a different current session");
            Require(!await model.MapAsync(tcpRemote, 65536) && model.MappingRules.Count == beforeInvalid,
                "invalid local port is rejected before saving a rule");
            var occupied = new TcpListener(IPAddress.Loopback, 0); occupied.Start();
            try
            {
                var occupiedPort = ((IPEndPoint)occupied.LocalEndpoint).Port;
                Require(!await model.MapAsync(tcpRemote, occupiedPort) && model.Error.Length > 0,
                    "occupied local port returns failure for the dialog to display");
                var failedRule = model.MappingRules.Single(r => r.Rule.LocalPort == occupiedPort && r.Rule.Kind == SharedServiceKind.Tcp);
                Require(failedRule.MappingId is null && failedRule.Status.StartsWith("失败", StringComparison.Ordinal), "failed listener is never shown as enabled");
                await model.DeleteRuleAsync(failedRule);
            }
            finally { occupied.Stop(); }
            model.SelectedRemoteService = visitor.RemoteServices.Single(s => s.Service.Kind == SharedServiceKind.Serial);
            Require(await model.MapVirtualSerialAsync(serialRemote, new("COM101", "CNCB0", "CNCB0", string.Empty)), "explicit serial target creates the selected local COM mapping");
            var virtualRule = model.MappingRules.Single(r => r.Rule.Kind == SharedServiceKind.Serial);
            Require(virtualRule.MappingId is not null && virtualEndpoints.Count == 1 && !virtualEndpoints[0].Ready
                && settings.Snapshot().PortMappingRules.Single(r => r.Kind == SharedServiceKind.Serial).VirtualPort == "COM101",
                "view model saves and enables optional system COM while waiting for external open");
            Require(model.SerialMappingRules.Cast<PortMappingRuleViewModel>().Single() == virtualRule && model.NetworkMappingRules.Cast<PortMappingRuleViewModel>().Count() == 2
                && virtualRule.Rule.RemoteEndpoint == "COM3", "serial and network mapping views stay independent and retain the remote endpoint");
            await model.DisableRuleAsync(virtualRule);
            Require(virtualRule.MappingId is null && virtualEndpoints[0].Disposals == 1, "disabling system COM releases its bridge while retaining its saved rule");
            await model.EnableRuleAsync(virtualRule);
            await visitor.DisconnectPeerAsync(visitor.Peers.Single().SessionId);
            await UntilAsync(() => virtualEndpoints[1].Disposals == 1);
            Require(virtualEndpoints[1].Disposals == 1, "peer disconnection releases a re-enabled system COM mapping");
            await UntilAsync(() => owner.Peers.Count == 0);
            var stoppingRequest = model.ConnectAsync(""); await UntilAsync(() => owner.PendingApprovals.Count == 1);
            await model.StopAsync(); await stoppingRequest;
            Require(!model.IsConnecting && !visitor.IsOnline, "stop works during an outstanding device connection request");
            var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            try
            {
                model.SaveSecret("smoke-only", secret); settings.SaveNow();
                Require(model.ReadSecret("smoke-only") == secret && !(await File.ReadAllTextAsync(settings.FilePath)).Contains(secret, StringComparison.Ordinal), "connection credentials persist in Windows credential storage without entering JSON");
            }
            finally { model.SaveSecret("smoke-only", ""); }
        }
        finally { Directory.Delete(directory, true); }
    }
    private static DeviceNodeOptions Options(string url, string password, bool direct, string admission = "") => new() { ServerUrl = url, AdmissionKey = admission, Credential = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), Password = password, PreferDirect = direct };
    private static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    private static async Task EchoAsync(TcpListener listener, CancellationToken token)
    {
        var tasks = new List<Task>();
        try { while (true) { var client = await listener.AcceptTcpClientAsync(token); tasks.Add(Task.Run(async () => { using (client) { var bytes = new byte[8192]; try { int count; while ((count = await client.GetStream().ReadAsync(bytes, token)) > 0) await client.GetStream().WriteAsync(bytes.AsMemory(0, count), token); } catch (Exception ex) when (ex is IOException or OperationCanceledException) { } } })); } }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException) { }
        finally { await Task.WhenAll(tasks); }
    }
    internal static async Task UntilAsync(Func<bool> condition) { using var timer = new CancellationTokenSource(TimeSpan.FromSeconds(12)); while (!condition()) await Task.Delay(30, timer.Token); }
    internal static void Require(bool value, string text) { if (!value) throw new InvalidOperationException(text); Console.WriteLine("PASS " + text); }
    private static void Expect<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    internal static async Task ExpectAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    internal static string FindRepository() { for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent) if (File.Exists(Path.Combine(folder.FullName, "EmbedDesk.sln"))) return folder.FullName; throw new DirectoryNotFoundException("Repository not found"); }
    private sealed class TestSerial : ISerialPortService
    {
        public int OpenCount; public bool FailOpen, FailSend; public SerialPortSettings? LastSettings;
        public Task<IReadOnlyList<SerialPortDescriptor>> ScanAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<SerialPortDescriptor>>([]);
        public ISerialPortChannel CreateChannel(SerialPortSettings settings) => new TestChannel(this, settings);
        private sealed class TestChannel(TestSerial owner, SerialPortSettings settings) : ISerialPortChannel, ISerialPortConfiguration
        {
            public string Id { get; } = Guid.NewGuid().ToString("N"); public SerialPortSettings Settings => settings; public bool IsOpen { get; private set; }
            public event EventHandler<ReadOnlyMemory<byte>>? DataReceived;
            public Task OpenAsync(CancellationToken token = default) { if (owner.FailOpen) throw new IOException("Test: port is occupied externally"); owner.OpenCount++; owner.LastSettings = settings; IsOpen = true; return Task.CompletedTask; }
            public Task CloseAsync(CancellationToken token = default) { IsOpen = false; return Task.CompletedTask; }
            public Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default) { if (owner.FailSend) throw new IOException("Test: hardware write failed"); DataReceived?.Invoke(this, bytes); return Task.CompletedTask; }
            public Task ConfigureAsync(SerialPortSettings next, CancellationToken token = default) { settings = next; owner.LastSettings = next; return Task.CompletedTask; }
            public ValueTask DisposeAsync() { IsOpen = false; return ValueTask.CompletedTask; }
        }
    }
}
