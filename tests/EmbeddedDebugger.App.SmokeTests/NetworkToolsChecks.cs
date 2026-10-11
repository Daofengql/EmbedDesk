using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EmbeddedDebugger.Infrastructure.Networking;

namespace EmbeddedDebugger.App.SmokeTests;

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
            Require((await Detect(server)).Kind == NatKind.Unknown, "Different public exit IPs are not automatically classified as Symmetric NAT");
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
            await FileWorkspaceChecks.CaptureAsync(picker, "embedded-debugger-protocol-picker-fixed.png");
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
            await FileWorkspaceChecks.CaptureAsync(probe, "embedded-debugger-ping-trace.png");
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
                await FileWorkspaceChecks.CaptureAsync(nat, "embedded-debugger-nat-four-types.png");
            }
            finally { nat.Close(); }
        }
    }

    private static Task<NatReport> Detect(StunFixture fixture) =>
        new NatDetector(TimeSpan.FromMilliseconds(120)).DetectAsync(
            [fixture.Primary.ToString(), fixture.Other.ToString()], null, CancellationToken.None);

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
        private int _mappingPort, _primaryHits;
        public HashSet<int> MappingPorts { get; } = [];
        public IPEndPoint Primary => (IPEndPoint)_primary.Client.LocalEndPoint!;
        public IPEndPoint Other => (IPEndPoint)_other.Client.LocalEndPoint!;

        public StunFixture(NatKind kind, bool advertise = true, bool ignoreChange = false, bool rebind = false, bool badPackets = false, bool silent = false, bool multipleExits = false)
        {
            _kind = kind; _advertise = advertise; _ignoreChange = ignoreChange; _rebind = rebind; _badPackets = badPackets; _silent = silent;
            _multipleExits = multipleExits;
            _portOnly = new(new IPEndPoint(IPAddress.Loopback, Other.Port));
            _worker = Task.WhenAll(Serve(_primary, false), Serve(_other, true));
        }

        private async Task Serve(UdpClient receiver, bool other)
        {
            try
            {
                while (true)
                {
                    var packet = await receiver.ReceiveAsync(_stop.Token);
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
                    var mapped = _kind == NatKind.OpenInternet ? packet.RemoteEndPoint : new IPEndPoint(IPAddress.Parse("203.0.113.10"), mappedPort);
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
