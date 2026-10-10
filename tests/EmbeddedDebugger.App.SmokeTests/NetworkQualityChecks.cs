using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using EmbeddedDebugger.App.Controls;
using EmbeddedDebugger.App.ViewModels;
using EmbeddedDebugger.Core.Abstractions;
using EmbeddedDebugger.Core.Models;
using EmbeddedDebugger.Infrastructure.Networking;

namespace EmbeddedDebugger.App.SmokeTests;

internal static class NetworkQualityChecks
{
    public static async Task RunAsync()
    {
        await CheckRealProbesAsync();
        await CheckLifecycleAsync();
        await CheckTargetsAsync();
    }

    private static async Task CheckRealProbesAsync()
    {
        var probe = new NetworkProbe();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = listener.AcceptTcpClientAsync();
        var tcp = await probe.ProbeAsync(new("127.0.0.1", port), CancellationToken.None);
        using (var peer = await accepted.WaitAsync(TimeSpan.FromSeconds(3)))
        {
            var count = await peer.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            Require(tcp.Outcome == NetworkProbeOutcome.Success && tcp.Milliseconds >= 0 && count == 0, "TCPing measures a real handshake, sends no payload and closes its socket");
        }
        listener.Stop();
        var refused = await probe.ProbeAsync(new("127.0.0.1", port), CancellationToken.None);
        Require(refused.Outcome == NetworkProbeOutcome.NoReply && refused.Detail.Contains("拒绝"), "A closed TCP port reports refusal without an invented latency");
        var ping = await probe.ProbeAsync(new("127.0.0.1"), CancellationToken.None);
        Require(ping.Outcome == NetworkProbeOutcome.Success && ping.Milliseconds >= 0, "ICMP loopback provides a measured round-trip time");
        foreach (var host in new[] { "0.0.0.0", "::", "255.255.255.255", "224.0.0.1", "ff02::1" })
            Require((await probe.ProbeAsync(new(host), CancellationToken.None)).Outcome == NetworkProbeOutcome.Unavailable, $"Probe rejects non-unicast target {host}");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await probe.ProbeAsync(new("127.0.0.1", port), canceled.Token); throw new InvalidOperationException("Canceled probe completed."); }
        catch (OperationCanceledException) { Console.WriteLine("PASS Canceled probes do not become failed samples"); }
    }

    private static async Task CheckLifecycleAsync()
    {
        var probe = new ControlledProbe();
        using var status = new NetworkStatusViewModel(probe, TimeSpan.FromMilliseconds(10));
        status.Track(new("first", 22), "SSH first");
        var stale = await probe.NextAsync();
        status.Track(new("second", 3389), "RDP second");
        var current = await probe.NextAsync();
        Require(stale.Token.IsCancellationRequested && current.Target.TcpPort == 3389, "Changing targets cancels the old probe and preserves the service port");
        current.Reply.TrySetResult(new(NetworkProbeOutcome.Success, 25, "new target"));
        await WaitAsync(() => status.Milliseconds == 25);
        stale.Reply.TrySetResult(new(NetworkProbeOutcome.Success, 999, "stale target"));
        var next = await probe.NextAsync();
        Require(status.Milliseconds == 25 && status.SampleCount == 1 && !status.Tooltip.Contains("stale"), "Late results cannot overwrite another target's statistics");
        next.Reply.TrySetResult(new(NetworkProbeOutcome.Success, 35, "new target"));
        await WaitAsync(() => status.Milliseconds == 35);
        Require(status.Tooltip.Contains("平均 30.0") && status.Tooltip.Contains("抖动 10.0"), "Statistics show average RTT and adjacent-sample jitter");
        var failure = await probe.NextAsync();
        failure.Reply.TrySetResult(new(NetworkProbeOutcome.NoReply, null, "filtered"));
        await WaitAsync(() => status.SampleCount == 3);
        Require(status.Milliseconds is null && status.Label == "TCP 不可测" && status.Tooltip.Contains("1/3"), "Failed probes clear stale latency and report probe failures separately");
        for (var i = 1; i <= 21; i++)
        {
            var request = await probe.NextAsync();
            request.Reply.TrySetResult(new(NetworkProbeOutcome.Success, 40 + i, "recovered"));
            await WaitAsync(() => status.Milliseconds == 40 + i);
        }
        Require(status.SampleCount == 20 && status.Tooltip.Contains("0/20"), "The rolling window is bounded and old failures age out");
        var inFlight = await probe.NextAsync();
        status.UseIcmpCommand.Execute(null);
        var icmp = await probe.NextAsync();
        Require(inFlight.Token.IsCancellationRequested && icmp.Target.TcpPort is null && status.SampleCount == 0, "Switching to ICMP cancels TCPing and resets incompatible measurements");
        inFlight.Reply.TrySetResult(new(NetworkProbeOutcome.Success, 800, "stale TCP"));
        status.DisableCommand.Execute(null);
        icmp.Reply.TrySetResult(new(NetworkProbeOutcome.Success, 900, "stale ICMP"));
        await Task.Delay(50);
        Require(!status.IsMonitoring && status.SampleCount == 0 && status.Label == "探测已关闭", "Disabling probes suppresses queued results and cancels background work");
        status.UseAutomaticCommand.Execute(null);
        var active = await probe.NextAsync();
        status.Dispose();
        active.Reply.TrySetResult(new(NetworkProbeOutcome.Success, 1000, "after close"));
        status.Track(new("closed", 22), "closed");
        await Task.Delay(50);
        Require(active.Token.IsCancellationRequested && !status.IsMonitoring && status.Milliseconds is null, "Disposing a session stops probes permanently");
    }

    private static async Task CheckTargetsAsync()
    {
        var transport = new TestTransport(new() { Mode = TransportMode.Server });
        await using var server = new NetworkSessionViewModel(transport, (_, _, _) => { });
        server.NetworkStatus.DisableCommand.Execute(null);
        transport.Emit(TransportEventKind.StateChanged, TransportSessionState.Listening);
        Require(server.NetworkStatus.Target is null, "TCP listeners never probe their own bind address");
        transport.Emit(TransportEventKind.Connected, TransportSessionState.Connected, "[::1]:50001");
        Require(server.NetworkStatus.Target == new NetworkProbeTarget("::1"), "TCP server probes an IPv6 peer using ICMP, not its ephemeral source port");
        transport.Emit(TransportEventKind.Connected, TransportSessionState.Connected, "127.0.0.1:50002");
        Require(server.NetworkStatus.Target is null, "A multi-peer listener does not claim one measurement describes every client");
        transport.Emit(TransportEventKind.Disconnected, TransportSessionState.Connected, "[::1]:50001");
        Require(server.NetworkStatus.Target == new NetworkProbeTarget("127.0.0.1"), "The remaining TCP peer becomes the probe target");
        await server.StopAsync();
        Require(server.NetworkStatus.Target is null && !server.NetworkStatus.IsMonitoring, "Stopping a transport clears its probe target");

        var udp = new TestTransport(new() { Mode = TransportMode.Server, Protocol = TransportProtocol.Udp });
        await using var udpVm = new NetworkSessionViewModel(udp, (_, _, _) => { });
        udpVm.NetworkStatus.DisableCommand.Execute(null);
        udp.Emit(TransportEventKind.StateChanged, TransportSessionState.Listening);
        Require(udpVm.NetworkStatus.Target is null, "UDP server waits for an actual peer");
        udp.Emit(TransportEventKind.Received, TransportSessionState.Listening, "127.0.0.2:50000");
        udp.Emit(TransportEventKind.Received, TransportSessionState.Listening, "[::1]:50001");
        Require(udpVm.NetworkStatus.Target == new NetworkProbeTarget("::1"), "UDP server follows its most recent peer without probing a UDP port as TCP");

        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var remote = new RemoteWorkspaceViewModel(new FakeRemoteDebugService(), new() { Host = "127.0.0.1", Port = port });
        await remote.ConnectCommand.ExecuteAsync(null);
        await WaitAsync(() => remote.NetworkStatus.Milliseconds.HasValue);
        Require(remote.NetworkStatus.Target?.TcpPort == port, "SSH automatically probes the configured service port");
        var control = new RemoteWorkspace { DataContext = remote };
        var window = new Window { Title = "Network quality UI check", Content = control, Width = 840, Height = 580 };
        window.Show();
        try
        {
            await FileWorkspaceChecks.CaptureAsync(window, "embedded-debugger-ssh-network-quality.png");
            var indicator = FindIndicator(control)!;
            var menu = indicator.ContextMenu!;
            menu.PlacementTarget = indicator; menu.IsOpen = true;
            await Task.Delay(50);
            var icmp = (MenuItem)menu.Items[1];
            Require(icmp.Command is not null && ((MenuItem)menu.Items[0]).IsChecked, "The indicator context menu binds to the active session");
            icmp.Command!.Execute(null);
            await Task.Delay(50);
            Require(icmp.IsChecked && remote.NetworkStatus.IsIcmp, "The context menu selects ICMP and updates its checkmark");
            ((MenuItem)menu.Items[3]).Command!.Execute(null);
            await Task.Delay(50);
            Require(!remote.NetworkStatus.IsMonitoring && ((MenuItem)menu.Items[3]).IsChecked, "The context menu disables ongoing probes");
            menu.IsOpen = false;
        }
        finally { window.Close(); }
        await remote.DisconnectCommand.ExecuteAsync(null);
        Require(!remote.NetworkStatus.IsMonitoring, "SSH disconnect stops its probe loop");
        foreach (var protocol in new[] { RemoteProtocol.Rdp, RemoteProtocol.Vnc })
        {
            using var desktop = new DesktopSessionViewModel(new() { Protocol = protocol, Host = "127.0.0.1", Port = port });
            desktop.SetState("已连接", "ready");
            await WaitAsync(() => desktop.NetworkStatus.Milliseconds.HasValue);
            Require(desktop.IsConnected && desktop.NetworkStatus.Target?.TcpPort == port, $"{protocol} starts measuring only after connection succeeds");
            desktop.SetState("连接失败", "closed");
            Require(!desktop.NetworkStatus.IsMonitoring, $"{protocol} failure stops its probe loop");
        }
        listener.Stop();
        using var connectedDesktop = new DesktopSessionViewModel(new() { Protocol = RemoteProtocol.Rdp, Host = "127.0.0.1", Port = port });
        connectedDesktop.SetState("已连接", "session is independent of probes");
        await WaitAsync(() => connectedDesktop.NetworkStatus.SampleCount > 0);
        Require(connectedDesktop.IsConnected && connectedDesktop.NetworkStatus.Milliseconds is null, "A refused probe never changes a desktop session's connected state");
    }

    private static NetworkStatusIndicator? FindIndicator(DependencyObject root)
    {
        if (root is NetworkStatusIndicator indicator) return indicator;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindIndicator(System.Windows.Media.VisualTreeHelper.GetChild(root, i)) is { } match) return match;
        return null;
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition()) { if (DateTime.UtcNow > until) throw new TimeoutException("Network status did not update."); await Task.Delay(10); }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); Console.WriteLine($"PASS {message}"); }

    private sealed record Request(NetworkProbeTarget Target, CancellationToken Token, TaskCompletionSource<NetworkProbeResult> Reply);
    private sealed class ControlledProbe : INetworkProbe
    {
        private readonly Channel<Request> _requests = Channel.CreateUnbounded<Request>();
        public Task<NetworkProbeResult> ProbeAsync(NetworkProbeTarget target, CancellationToken cancellationToken)
        {
            // Intentionally ignore cancellation to exercise late-completion defenses.
            var reply = new TaskCompletionSource<NetworkProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _requests.Writer.TryWrite(new(target, cancellationToken, reply));
            return reply.Task;
        }
        public Task<Request> NextAsync() => _requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class TestTransport(TransportOptions options) : ITransportSession
    {
        public string Id => "network-probe-test";
        public TransportOptions Options => options;
        public TransportSessionState State { get; private set; }
        public event EventHandler<TransportMessage>? MessageReceived;
        public void Emit(TransportEventKind kind, TransportSessionState state, string? peer = null)
        {
            State = state;
            MessageReceived?.Invoke(this, new(DateTimeOffset.Now, Id, kind, state, peer, ReadOnlyMemory<byte>.Empty, null, null));
        }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) { Emit(TransportEventKind.StateChanged, TransportSessionState.Stopped); return Task.CompletedTask; }
        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
