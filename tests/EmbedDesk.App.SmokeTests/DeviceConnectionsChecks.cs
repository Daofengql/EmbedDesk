using System.Diagnostics;
using System.IO;
using System.Net;
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
            await RelaySelectionChecks.RunAsync("http://127.0.0.1:" + port);
            await CheckPathAsync("http://127.0.0.1:" + port, false);
            await CheckPathAsync("http://127.0.0.1:" + port, true);
            await CheckPathAsync("http://127.0.0.1:" + port, true, true);
            await CheckUdpPathAsync("http://127.0.0.1:" + port, false);
            await CheckUdpPathAsync("http://127.0.0.1:" + port, true);
            await CheckViewModelAsync("http://127.0.0.1:" + port);
            await CheckReconnectAsync(start, process, "http://127.0.0.1:" + port);
            Console.WriteLine("PASS device node: Go relay, password/manual auth, direct/fallback, TCP/UDP mapping, serial failures and cleanup");
        }
        finally { if (!process.HasExited) process.Kill(); await process.WaitForExitAsync(); await Task.WhenAll(stdout, stderr); Directory.Delete(directory, true); }
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
    private static async Task CheckPathAsync(string url, bool direct, bool failDirect = false)
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
            await owner.StartAsync(Options(url, password, direct)); await visitor.StartAsync(Options(url, password, direct));
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
    private static async Task CheckUdpPathAsync(string url, bool direct)
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
            await owner.StartAsync(Options(url, password, direct)); await visitor.StartAsync(Options(url, password, direct));
            await UntilAsync(() => owner.IsOnline && visitor.IsOnline);
            await visitor.ConnectPeerAsync(owner.DeviceId, password); await UntilAsync(() => visitor.RemoteServices.Count == 2);
            var peer = visitor.Peers.Single(); var port = FreePort();
            var tcpMapping = await visitor.MapPortAsync(peer.SessionId, tcpService.Id, port);
            var udpMapping = await visitor.MapPortAsync(peer.SessionId, udpService.Id, port);
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

    private static async Task CheckViewModelAsync(string url)
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
            var udpService = new SharedDeviceService { Name = "saved UDP rule", Kind = SharedServiceKind.Udp };
            var serialService = new SharedDeviceService { Name = "saved virtual COM rule", Kind = SharedServiceKind.Serial, PortName = "COM3" };
            owner.ConfigureServices([tcpService, udpService, serialService]);
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
            await owner.StartAsync(Options(url, password, false)); await visitor.StartAsync(Options(url, password, false));
            await UntilAsync(() => owner.IsOnline && visitor.IsOnline);
            model.TargetId = owner.DeviceId;
            var request = model.ConnectAsync("");
            await UntilAsync(() => owner.PendingApprovals.Count == 1);
            Require(model.IsConnecting && !model.IsBusy, "waiting for approval leaves other connection controls available");
            model.CancelConnect(); await request;
            await UntilAsync(() => owner.Peers.Count == 0 && visitor.Peers.Count == 0);
            Require(!model.IsConnecting && visitor.IsOnline && model.Error.Contains("取消", StringComparison.Ordinal), "cancel removes pending authorization and preserves relay");
            await model.ConnectAsync(password); await UntilAsync(() => visitor.RemoteServices.Count == 3);
            model.SelectedRemoteService = visitor.RemoteServices.Single(s => s.Service.Kind == SharedServiceKind.Tcp); await model.MapAsync(FreePort());
            Require(model.MappingRules.Count == 1 && model.MappingRules[0].MappingId is not null && settings.Snapshot().PortMappingRules.Count == 1, "view model saves and activates the selected service rule");
            await model.DisableRuleAsync(model.MappingRules[0]);
            Require(visitor.Mappings.Count == 0 && model.MappingRules[0].MappingId is null, "disabling a rule retains only its configuration");
            model.SelectedRemoteService = visitor.RemoteServices.Single(s => s.Service.Kind == SharedServiceKind.Udp); await model.MapAsync(FreePort());
            Require(model.MappingRules[1].MappingId is not null && settings.Snapshot().PortMappingRules[1].Kind == SharedServiceKind.Udp,
                "view model saves and enables UDP rules with their protocol");
            await model.DisableRuleAsync(model.MappingRules[1]);
            model.SelectedRemoteService = visitor.RemoteServices.Single(s => s.Service.Kind == SharedServiceKind.Serial);
            await model.MapVirtualSerialAsync(new("COM101", "CNCB0", "CNCB0", string.Empty));
            var virtualRule = model.MappingRules.Single(r => r.Rule.Kind == SharedServiceKind.Serial);
            Require(virtualRule.MappingId is not null && virtualEndpoints.Count == 1 && !virtualEndpoints[0].Ready
                && settings.Snapshot().PortMappingRules.Single(r => r.Kind == SharedServiceKind.Serial).VirtualPort == "COM101",
                "view model saves and enables optional system COM while waiting for external open");
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
    private static DeviceNodeOptions Options(string url, string password, bool direct) => new() { ServerUrl = url, Credential = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), Password = password, PreferDirect = direct };
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
