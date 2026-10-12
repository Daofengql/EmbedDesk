using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EmbedDesk.Infrastructure.Networking;

namespace EmbedDesk.App.SmokeTests;

internal static class NetworkToolsChecks
{
    public static async Task RunAsync()
    {
        foreach (var kind in new[] { NatKind.FullCone, NatKind.RestrictedCone, NatKind.PortRestrictedCone, NatKind.Symmetric })
        {
            await using var server = new StunFixture(kind);
            var report = await Detect(server);
            Require(report.Kind == kind, $"UDP STUN fixture classifies {kind}: {report.Label} / {report.Evidence}");
            Require(server.MappingPorts.Count == 1, "Cross-node mapping probes reuse one source port");
        }
        await using (var server = new StunFixture(NatKind.Unknown, advertise: false))
            Require((await Detect(server)).Kind == NatKind.Unknown, "Stable mapping without alternate-source support is inconclusive");
        await using (var server = new StunFixture(NatKind.FullCone, ignoreChange: true))
            Require((await Detect(server)).Kind == NatKind.Unknown, "A server ignoring CHANGE-REQUEST cannot yield Full Cone");
        await using (var server = new StunFixture(NatKind.Symmetric, rebind: true))
            Require((await Detect(server)).Kind == NatKind.Unknown, "Rebinding at the initial destination is not mistaken for Symmetric NAT");
        await using (var server = new StunFixture(NatKind.OpenInternet))
            Require((await Detect(server)).Kind == NatKind.OpenInternet, "Unchanged local address and port are detected as no NAT");
        await using (var server = new StunFixture(NatKind.FullCone, multipleExits: true))
        {
            var report = await Detect(server);
            Require(report.Exits.Count == 2 && report.Exits.All(exit => exit.Kind == NatKind.Unknown && exit.TargetCount == 1),
                "Each exit needs its own mapping comparison; one target per exit cannot imply Symmetric NAT");
            Require(report.Label.Contains("2 个公网出口"), "Multiple exits have an explicit summary instead of a single inconclusive NAT verdict");
        }
        await CheckGroupedExitsAsync();
        await using (var server = new StunFixture(NatKind.FullCone, badPackets: true))
            Require((await Detect(server)).Kind == NatKind.Unknown, "Wrong transaction IDs and truncated attributes are rejected");
        await using (var server = new StunFixture(NatKind.FullCone, silent: true))
        {
            Require((await Detect(server)).Kind == NatKind.Unknown, "Unresponsive servers time out without inventing a NAT type");
            using var cancel = new CancellationTokenSource(60);
            try
            {
                await new NatDetector().DetectAsync([server.Primary.ToString()], null, cancel.Token);
                throw new InvalidOperationException("Expected cancellation");
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { Console.WriteLine("PASS STUN cancellation interrupts pending UDP receives"); }
        }

        var picker = new NetworkTypeSelectionWindow();
        picker.Show();
        try
        {
            picker.UpdateLayout();
            foreach (var button in Descendants<Button>(picker).Where(b => b.Tag is string))
                foreach (var label in Descendants<TextBlock>(button))
                {
                    var bounds = label.TransformToAncestor(button).TransformBounds(new Rect(label.RenderSize));
                    Require(bounds.Top >= 0 && bounds.Bottom <= button.ActualHeight && bounds.Left >= 0 && bounds.Right <= button.ActualWidth,
                        $"Protocol picker displays the entire label: {label.Text}");
                }
            await FileWorkspaceChecks.CaptureAsync(picker, "embeddesk-protocol-picker-fixed.png");
        }
        finally { picker.Close(); }

        var probe = new NetworkProbeToolWindow();
        probe.Show();
        try
        {
            ((TextBox)probe.FindName("CountBox")).Text = "1";
            Click(probe, "StartButton");
            await WaitAsync(() => ((Button)probe.FindName("StartButton")).IsEnabled);
            Require(probe.Samples.Single().Outcome == "成功", "Merged tool still performs a real ICMP loopback probe");
            ((ComboBox)probe.FindName("ModeBox")).SelectedIndex = 2;
            ((CheckBox)probe.FindName("ResolveNamesBox")).IsChecked = false;
            Click(probe, "StartButton");
            await WaitAsync(() => ((Button)probe.FindName("StartButton")).IsEnabled);
            Require(probe.Hops.Single().Status == "到达目标" && probe.Hops[0].Address == "127.0.0.1", "Merged traceroute reaches loopback on hop one");
            Require(((FrameworkElement)probe.FindName("PingResults")).Visibility == Visibility.Collapsed &&
                ((FrameworkElement)probe.FindName("TraceResults")).IsVisible, "Trace mode displays hop columns within the same window");
            await FileWorkspaceChecks.CaptureAsync(probe, "embeddesk-ping-trace.png");
            ((ComboBox)probe.FindName("ModeBox")).SelectedIndex = 0;
            ((TextBox)probe.FindName("CountBox")).Text = "0";
            Click(probe, "StartButton");
            Click(probe, "StopButton");
            await WaitAsync(() => ((Button)probe.FindName("StartButton")).IsEnabled);
            Require(((TextBlock)probe.FindName("StatusText")).Text == "已停止", "Stop cancels continuous Ping and restores controls");
        }
        finally { probe.Close(); }

        await using (var server = new StunFixture(NatKind.FullCone))
        {
            var nat = new NatDetectionWindow();
            nat.Show();
            try
            {
                ((TextBox)nat.FindName("ServersBox")).Text = server.Primary.ToString();
                Click(nat, "StartButton");
                await WaitAsync(() => ((Button)nat.FindName("StartButton")).IsEnabled);
                Require(((TextBlock)nat.FindName("NatTypeText")).Text.Contains("Full Cone"), "NAT window shows the bilingual result from actual UDP exchanges");
                await FileWorkspaceChecks.CaptureAsync(nat, "embeddesk-nat-four-types.png");
            }
            finally { nat.Close(); }
        }
        await using (var first = new StunFixture(NatKind.FullCone))
        await using (var second = new StunFixture(NatKind.FullCone, publicIp: "198.51.100.20"))
        {
            var nat = new NatDetectionWindow();
            nat.Show();
            try
            {
                ((TextBox)nat.FindName("ServersBox")).Text = string.Join(Environment.NewLine, first.Primary, second.Primary);
                Click(nat, "StartButton");
                await WaitAsync(() => ((Button)nat.FindName("StartButton")).IsEnabled);
                Require(nat.Exits.Count == 2 && nat.Exits.All(exit => exit.Kind == NatKind.FullCone), "NAT window presents both independently classified exits");
                Require(nat.Results.Count == 2 && nat.Results.All(row => row.Detail == "映射成功"), "Only configured node results appear; auxiliary filtering rows stay removed");
                // Reproduce a full default-node report, including the longer inferred verdict.
                nat.Exits[0] = nat.Exits[0] with
                {
                    PublicIp = "175.44.158.80", Kind = NatKind.PortRestrictedCone,
                    Nodes = NatDetector.DefaultServers.Take(3).ToArray(), TargetCount = 4,
                    Evidence = "此出口在不同目标间的映射端口一致，首节点复测稳定。不同 IP / 端口的响应均超时，原来源复测成功（基于超时推断，丢包或防火墙可能影响结果）。"
                };
                nat.Exits[1] = nat.Exits[1] with
                {
                    PublicIp = "13.212.127.159", Kind = NatKind.Unknown,
                    Nodes = NatDetector.DefaultServers.Skip(3).ToArray(), TargetCount = 6,
                    Evidence = "此出口在不同目标间的映射端口一致，首节点复测稳定（锥形候选）；缺少属于此出口的有效过滤证据，暂不能区分三种锥形 NAT。"
                };
                var summary = (TextBlock)nat.FindName("SummaryText");
                summary.Text = summary.Text.Replace("2/2", "1/2", StringComparison.Ordinal);
                nat.Results.Clear();
                foreach (var node in NatDetector.DefaultServers)
                {
                    var exit = nat.Exits.Single(group => group.Nodes.Contains(node));
                    nat.Results.Add(new(node, $"{exit.PublicIp}:{exit.Ports[0]}", "330.2 ms", "映射成功"));
                }
                nat.UpdateLayout();
                var scroll = (ScrollViewer)nat.FindName("ReportScroll");
                await FileWorkspaceChecks.CaptureAsync(nat, "embeddesk-nat-default-layout.png");
                Require(scroll.ScrollableHeight < 1, "Default size shows both detailed exit verdicts and all nine default node rows without scrolling");
                nat.Width = nat.MinWidth;
                nat.Height = nat.MinHeight;
                foreach (var expander in Descendants<Expander>((ItemsControl)nat.FindName("ExitReports"))) expander.IsExpanded = true;
                nat.UpdateLayout();
                Require(scroll.ScrollableHeight > 0 && scroll.ViewportHeight > 100, "Expanded details remain accessible through the report scrollbar at minimum window size");
                scroll.ScrollToBottom();
                await FileWorkspaceChecks.CaptureAsync(nat, "embeddesk-nat-grouped-small.png");
                Require(Descendants<DataGrid>(nat).Single().ActualHeight > 100, "Node table retains usable space at minimum window size");
                ((TextBox)nat.FindName("ServersBox")).Text = first.Primary.ToString();
                Click(nat, "StartButton");
                Require(nat.Exits.Count == 0, "A new detection clears previous exit groups");
                Click(nat, "StopButton");
                await WaitAsync(() => ((Button)nat.FindName("StartButton")).IsEnabled);
                Require(nat.Exits.Count == 0, "Canceled detection does not restore stale group verdicts");
            }
            finally { nat.Close(); }
        }
    }

    private static async Task CheckGroupedExitsAsync()
    {
        await using (var first = new StunFixture(NatKind.FullCone))
        await using (var second = new StunFixture(NatKind.PortRestrictedCone, publicIp: "198.51.100.20"))
        {
            var report = await Detect(first, second);
            Require(report.Exits.Count == 2 && report.Exits[0].Kind == NatKind.FullCone && report.Exits[1].Kind == NatKind.PortRestrictedCone,
                "Two exits retain different cone classifications without mixing filtering evidence");
            Require(report.Exits.All(exit => exit.Ports.Count == 1 && exit.TargetCount == 2 && exit.Nodes.Count == 2),
                "Each exit reports its own targets, mapped ports and configured nodes");
        }
        await using (var first = new StunFixture(NatKind.Symmetric))
        await using (var second = new StunFixture(NatKind.Unknown, advertise: false, publicIp: "198.51.100.20"))
        {
            var report = await Detect(first, second);
            Require(report.Exits[0].Kind == NatKind.Symmetric && report.Exits[0].Ports.Count == 2 && report.Exits[1].Kind == NatKind.Unknown,
                "Port changes within one exit identify Symmetric NAT even if another exit lacks filtering evidence");
        }
        await using (var first = new StunFixture(NatKind.FullCone, filteringIp: "198.51.100.20"))
        await using (var second = new StunFixture(NatKind.Unknown, advertise: false, publicIp: "198.51.100.20"))
        {
            var report = await Detect(first, second);
            Require(report.Exits.Count == 2 && report.Exits.All(exit => exit.Kind == NatKind.Unknown),
                "A filtering socket routed to another exit cannot classify either mapping group");
        }
        await using (var first = new StunFixture(NatKind.FullCone, rebind: true))
        await using (var second = new StunFixture(NatKind.FullCone, publicIp: "198.51.100.20"))
        {
            var report = await Detect(first, second);
            Require(report.Exits[0].Kind == NatKind.Unknown && report.Exits[1].Kind == NatKind.FullCone,
                "A rebind in one exit does not discard the other exit's valid verdict");
        }
        await using (var first = new StunFixture(NatKind.FullCone))
        await using (var sameExit = new StunFixture(NatKind.FullCone))
        await using (var nextExit = new StunFixture(NatKind.RestrictedCone, publicIp: "198.51.100.20"))
        {
            var report = await Detect(first, sameExit, nextExit);
            Require(report.Exits[1].Kind == NatKind.RestrictedCone, "Two behavior tests on the first exit do not exhaust the next exit's test budget");
        }
        await using (var first = new StunFixture(NatKind.FullCone))
        await using (var conflicting = new StunFixture(NatKind.RestrictedCone))
        await using (var nextExit = new StunFixture(NatKind.FullCone, publicIp: "198.51.100.20"))
        {
            var report = await Detect(first, conflicting, nextExit);
            Require(report.Exits[0].Kind == NatKind.Unknown && report.Exits[1].Kind == NatKind.FullCone,
                "Conflicting filtering evidence only invalidates the affected exit");
        }
    }

    private static Task<NatReport> Detect(StunFixture fixture) =>
        new NatDetector(TimeSpan.FromMilliseconds(120)).DetectAsync(
            [fixture.Primary.ToString(), fixture.Other.ToString()], null, CancellationToken.None);

    private static Task<NatReport> Detect(params StunFixture[] fixtures) =>
        new NatDetector(TimeSpan.FromMilliseconds(120)).DetectAsync(
            fixtures.SelectMany(fixture => new[] { fixture.Primary.ToString(), fixture.Other.ToString() }), null, CancellationToken.None);

    private static void Click(Window window, string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task WaitAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!predicate()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("Network tool did not finish"); await Task.Delay(20); }
    }
    private static void Require(bool condition, string detail)
    {
        if (!condition) throw new InvalidOperationException(detail);
        Console.WriteLine("PASS " + detail);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T item) yield return item;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    // Independent fake STUN endpoints use two IPs and two ports; dropping alternate
    // responses simulates the filtering evidence observed through each NAT type.
    private sealed class StunFixture : IAsyncDisposable
    {
        private readonly UdpClient _primary = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly UdpClient _other = new(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
        private readonly UdpClient _portOnly;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _worker;
        private readonly NatKind _kind;
        private readonly bool _advertise, _ignoreChange, _rebind, _badPackets, _silent, _multipleExits;
        private readonly IPAddress _publicIp;
        private readonly IPAddress? _filteringIp;
        private int _mappingPort, _primaryHits;
        public HashSet<int> MappingPorts { get; } = [];
        public IPEndPoint Primary => (IPEndPoint)_primary.Client.LocalEndPoint!;
        public IPEndPoint Other => (IPEndPoint)_other.Client.LocalEndPoint!;

        public StunFixture(NatKind kind, bool advertise = true, bool ignoreChange = false, bool rebind = false, bool badPackets = false, bool silent = false, bool multipleExits = false,
            string publicIp = "203.0.113.10", string? filteringIp = null)
        {
            _kind = kind; _advertise = advertise; _ignoreChange = ignoreChange; _rebind = rebind; _badPackets = badPackets; _silent = silent;
            _multipleExits = multipleExits;
            _publicIp = IPAddress.Parse(publicIp);
            _filteringIp = filteringIp is null ? null : IPAddress.Parse(filteringIp);
            _portOnly = new(new IPEndPoint(IPAddress.Loopback, Other.Port));
            _worker = Task.WhenAll(Serve(_primary, false), Serve(_other, true));
        }

        private async Task Serve(UdpClient receiver, bool other)
        {
            try
            {
                while (true)
                {
                    UdpReceiveResult packet;
                    try { packet = await receiver.ReceiveAsync(_stop.Token); }
                    // Windows can report an ICMP port-unreachable after the canceled client closes.
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset) { continue; }
                    if (_silent || packet.Buffer.Length < 20) continue;
                    var request = packet.Buffer;
                    var change = request.Length == 28 ? BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(24)) : 0;
                    var sourcePort = packet.RemoteEndPoint.Port;
                    if (_mappingPort == 0) _mappingPort = sourcePort;
                    if (sourcePort == _mappingPort || other) MappingPorts.Add(sourcePort);
                    if (!other && sourcePort == _mappingPort && change == 0) _primaryHits++;
                    if (change == 6 && _kind is NatKind.RestrictedCone or NatKind.PortRestrictedCone or NatKind.Symmetric) continue;
                    if (change == 2 && _kind is NatKind.PortRestrictedCone or NatKind.Symmetric) continue;
                    var responseSocket = change != 0 && !_ignoreChange ? change == 6 ? _other : _portOnly : receiver;
                    var mappedPort = 10000 + sourcePort % 40000 + (_kind == NatKind.Symmetric && other ? 1 : 0);
                    if (_rebind && _primaryHits > 1 && sourcePort == _mappingPort) mappedPort += 2;
                    var mapped = _kind == NatKind.OpenInternet ? packet.RemoteEndPoint : new IPEndPoint(
                        sourcePort != _mappingPort && _filteringIp is not null ? _filteringIp : _publicIp, mappedPort);
                    if (_multipleExits && other) mapped = new(IPAddress.Parse("198.51.100.20"), mappedPort);
                    var bytes = Response(request, mapped, other ? Primary : Other, _advertise && !other);
                    if (_badPackets)
                    {
                        bytes[8] ^= 0x80; // unrelated transaction
                        await responseSocket.SendAsync(bytes, packet.RemoteEndPoint, _stop.Token);
                        bytes[8] ^= 0x80;
                        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(22), 250); // oversized attribute
                    }
                    await responseSocket.SendAsync(bytes, packet.RemoteEndPoint, _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }

        private static byte[] Response(byte[] request, IPEndPoint mapped, IPEndPoint other, bool advertise)
        {
            var packet = new byte[advertise ? 44 : 32];
            BinaryPrimitives.WriteUInt16BigEndian(packet, 0x0101);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)(packet.Length - 20));
            request.AsSpan(4, 16).CopyTo(packet.AsSpan(4));
            // XOR-MAPPED-ADDRESS encoded independently byte by byte.
            packet[21] = 0x20; packet[23] = 8; packet[25] = 1;
            packet[26] = (byte)((mapped.Port >> 8) ^ 0x21);
            packet[27] = (byte)((mapped.Port & 255) ^ 0x12);
            var address = mapped.Address.GetAddressBytes();
            var mask = new byte[] { 0x21, 0x12, 0xa4, 0x42 };
            for (var i = 0; i < 4; i++) packet[28 + i] = (byte)(address[i] ^ mask[i]);
            if (advertise)
            {
                packet[32] = 0x80; packet[33] = 0x2c; packet[35] = 8; packet[37] = 1;
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(38), (ushort)other.Port);
                other.Address.GetAddressBytes().CopyTo(packet, 40);
            }
            return packet;
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            await _worker;
            _primary.Dispose(); _other.Dispose(); _portOnly.Dispose(); _stop.Dispose();
        }
    }
}
