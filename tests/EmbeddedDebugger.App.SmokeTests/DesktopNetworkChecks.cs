using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Forms.Integration;
using EmbeddedDebugger.App.Controls;
using EmbeddedDebugger.App.ViewModels;
using EmbeddedDebugger.Core.Models;
using EmbeddedDebugger.Infrastructure.Remote;
using MQTTnet;
using MQTTnet.Server;

namespace EmbeddedDebugger.App.SmokeTests;

internal static class DesktopNetworkChecks
{
    public static async Task RunAsync(MainWindowViewModel vm)
    {
        var window = new MainWindow(vm) { Width = 1340, Height = 880 };
        window.Show();
        try
        {
            var tabs = Descendants<TabControl>(window).First(); tabs.SelectedIndex = 2;
            await Task.Delay(150);
            var network = Descendants<NetworkWorkspace>(window).Single();
            Require(((FrameworkElement)network.FindName("EmptyState")).IsVisible, "Network workspace starts with a useful empty state");
            tabs.SelectedIndex = 3;
            await Task.Delay(100);
            var networkTools = Descendants<NetworkToolsWorkspace>(window).Single();
            var networkToolTags = Descendants<Button>(networkTools).Select(button => button.Tag?.ToString()).ToHashSet(StringComparer.Ordinal);
            Require(new[] { "Cidr", "Ping", "Iperf3", "Discovery", "Nat", "Upnp", "Dns" }.All(networkToolTags.Contains) && !networkToolTags.Contains("Trace"), "Network tools merge traceroute into Ping");
            foreach (var toolWindow in new Window[] { new NatDetectionWindow(), new UpnpDiscoveryWindow(), new DnsQueryWindow(), new NetworkProbeToolWindow(NetworkProbeToolMode.Traceroute) })
            {
                toolWindow.Owner = window;
                toolWindow.Show();
                toolWindow.UpdateLayout();
                toolWindow.Close();
            }
            Require(true, "NAT, UPnP, DNS and traceroute windows load successfully");
            Require(network.FindName("ToolList") is null, "Network workspace keeps protocol choices inside the new-session picker");
            await FileWorkspaceChecks.CaptureAsync(window, "embedded-debugger-network-tools.png");
            tabs.SelectedIndex = 2;
            await Task.Delay(100);
            var port = FreePort();
            var server = await vm.CreateNetworkSessionAsync(new TransportOptions { Mode = TransportMode.Server, LocalHost = "127.0.0.1", LocalPort = port, AutoReconnect = false });
            var client = await vm.CreateNetworkSessionAsync(new TransportOptions { LocalHost = "127.0.0.1", LocalPort = 0, RemoteHost = "127.0.0.1", RemotePort = port, AutoReconnect = false });
            await WaitAsync(() => client.CanSend && server.CanSend);
            await WaitAsync(() => client.NetworkStatus.Milliseconds.HasValue);
            Require(client.NetworkStatus.Target?.TcpPort == port && client.Terminal.ReceivedBytes == 0 && client.Terminal.SentBytes == 0, "TCP client probes its actual service port without contaminating debug traffic");
            client.SendText = "串口与网络 · TCP 回环测试"; client.AppendCrLf = true;
            await client.SendCommand.ExecuteAsync(null);
            await WaitAsync(() => server.Terminal.ReceivedBytes > 0);
            Require(server.Terminal.Entries.Any(e => e.DisplayText.Contains("TCP 回环测试")), "TCP Unicode text is received intact");
            using var secondPeer = new TcpClient();
            await secondPeer.ConnectAsync(IPAddress.Loopback, port);
            await WaitAsync(() => server.PeerTargets.Count >= 3);
            var secondPort = ((IPEndPoint)secondPeer.Client.LocalEndPoint!).Port;
            var selectedSecond = server.PeerTargets.FirstOrDefault(peer => IPEndPoint.TryParse(peer.Endpoint, out var endpoint) && endpoint.Port == secondPort)
                ?? throw new InvalidOperationException($"Expected peer port {secondPort}; peers: {string.Join(", ", server.PeerTargets.Select(peer => peer.Endpoint))}");
            server.SelectedPeerTarget = selectedSecond;
            var firstBytesBeforeTargeted = client.Terminal.ReceivedBytes;
            server.SendText = "targeted client";
            await server.SendCommand.ExecuteAsync(null);
            var targetedBuffer = new byte[64];
            var targetedRead = await secondPeer.GetStream().ReadAsync(targetedBuffer).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            Require(Encoding.UTF8.GetString(targetedBuffer, 0, targetedRead).Contains("targeted client", StringComparison.Ordinal) && client.Terminal.ReceivedBytes == firstBytesBeforeTargeted,
                "TCP server can target one connected client without sending to the others");
            server.SelectedPeerTarget = NetworkPeerTarget.Broadcast;
            server.SendText = "00 FF 01 7E"; server.SendAsHex = true;
            await server.SendCommand.ExecuteAsync(null);
            await WaitAsync(() => client.Terminal.ReceivedBytes == 4);
            Require(client.Terminal.Entries.Last().Data.SequenceEqual(new byte[] { 0, 255, 1, 126 }), "TCP server broadcasts exact HEX bytes");
            secondPeer.Close();
            await WaitAsync(() => server.PeerTargets.Count == 2);
            client.SendAsHex = true; client.SendText = "not hex";
            await client.SendCommand.ExecuteAsync(null);
            Require(client.Terminal.SentBytes == Encoding.UTF8.GetByteCount("串口与网络 · TCP 回环测试\r\n"), "Invalid HEX is rejected without writing to the socket");
            client.SendAsHex = false; client.AppendCrLf = false; client.SendText = "周期消息\r\n"; client.IntervalMs = "60";
            var repeating = client.RepeatSendCommand.ExecuteAsync(null);
            await WaitAsync(() => client.Terminal.SentBytes > 80);
            vm.SelectedSession = server;
            client.StopSendingCommand.Execute(null); await repeating;
            var count = client.Terminal.SentBytes; await Task.Delay(160);
            Require(client.Terminal.SentBytes == count && !client.IsSending, "Switching sessions does not redirect a send job; stop cancels it");
            client.SendQueue.Add(new SerialSendQueueItem { Payload = "queue-1\r\n" });
            client.SendQueue.Add(new SerialSendQueueItem { Payload = "queue-2\r\n" });
            await client.RunQueueCommand.ExecuteAsync(null);
            Require(client.JobStatus.Contains("2 次"), "Queue sends its enabled items and finishes");
            client.SendText = "下一条调试消息";
            vm.SelectedSession = client;
            await Task.Delay(120);
            Require(((TextBox)network.FindName("ReceiveText")).Text.Length > 0, "Received data reaches the redesigned receive pane");
            await FileWorkspaceChecks.CaptureAsync(window, "embedded-debugger-network-new.png");
            window.Width = 1080; window.Height = 680; window.UpdateLayout();
            await FileWorkspaceChecks.CaptureAsync(window, "embedded-debugger-network-compact.png");
            ((Button)network.FindName("SessionToggle")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(((FrameworkElement)network.FindName("SessionPane")).Visibility == Visibility.Collapsed, "Network sessions collapse through the center edge control");
            ((Button)network.FindName("SessionToggle")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(((FrameworkElement)network.FindName("SessionPane")).IsVisible, "The same edge control restores network sessions");
            await client.StopAsync();
            await WaitAsync(() => client.CanStart);
            await client.StartStopCommand.ExecuteAsync(null);
            await WaitAsync(() => client.CanSend);
            Require(client.CanSend, "A stopped TCP session can reconnect");
            await client.StopAsync(); await server.StopAsync();

            var udpPort = FreePort();
            var udpServer = await vm.CreateNetworkSessionAsync(new TransportOptions { Protocol = TransportProtocol.Udp, Mode = TransportMode.Server, LocalHost = "127.0.0.1", LocalPort = udpPort });
            var udpClient = await vm.CreateNetworkSessionAsync(new TransportOptions { Protocol = TransportProtocol.Udp, LocalHost = "127.0.0.1", LocalPort = 0, RemoteHost = "127.0.0.1", RemotePort = udpPort });
            udpClient.SendText = "udp-ping"; await udpClient.SendCommand.ExecuteAsync(null);
            await WaitAsync(() => udpServer.Terminal.ReceivedBytes == 8);
            udpServer.SendText = "udp-pong"; await udpServer.SendCommand.ExecuteAsync(null);
            await WaitAsync(() => udpClient.Terminal.ReceivedBytes == 8);
            Require(udpClient.Terminal.Entries.Last().DisplayText == "udp-pong", "UDP server replies to the actual last sender");
            await udpClient.StopAsync(); await udpServer.StopAsync();
            Require(!udpClient.LocalEndpoint.EndsWith(":0", StringComparison.Ordinal), "Ephemeral local ports expose their actual bound endpoint");
            var httpServer = await vm.CreateNetworkSessionAsync(new TransportOptions { Mode = TransportMode.Server, LocalHost = "127.0.0.1", LocalPort = 0, TreatAsHttp = true });
            var httpPort = int.Parse(httpServer.Endpoint.Split(':').Last(), System.Globalization.CultureInfo.InvariantCulture);
            var httpClient = await vm.CreateNetworkSessionAsync(new TransportOptions { LocalHost = "127.0.0.1", LocalPort = 0, RemoteHost = "127.0.0.1", RemotePort = httpPort, TreatAsHttp = true, AutoReconnect = false });
            await WaitAsync(() => httpClient.CanSend);
            httpClient.SendText = "GET /diagnostics HTTP/1.1\r\nHost: localhost\r\n\r\n";
            await httpClient.SendCommand.ExecuteAsync(null);
            await WaitAsync(() => httpClient.HttpHeadersText.Contains("Embedded Debugger OK", StringComparison.Ordinal));
            Require(httpClient.HttpStartLine == "HTTP/1.1 200 OK", "HTTP diagnostics retain both headers and body from received bytes");
            await httpClient.StopAsync(); await httpServer.StopAsync();

            var mqttPort = FreePort();
            using var mqttServer = new MqttFactory().CreateMqttServer(new MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointPort(mqttPort).Build());
            await mqttServer.StartAsync();
            var mqtt = await vm.CreateNetworkSessionAsync(new TransportOptions
            {
                Protocol = TransportProtocol.Mqtt,
                Mode = TransportMode.Client,
                RemoteHost = "127.0.0.1",
                RemotePort = mqttPort,
                MqttTopic = "embedded-debugger/smoke",
                MqttClientId = $"smoke-{Guid.NewGuid():N}",
                AutoReconnect = false
            });
            var mqttReceiver = await vm.CreateNetworkSessionAsync(new TransportOptions
            {
                Protocol = TransportProtocol.Mqtt,
                Mode = TransportMode.Client,
                RemoteHost = "127.0.0.1",
                RemotePort = mqttPort,
                MqttTopic = "embedded-debugger/smoke",
                MqttClientId = $"smoke-{Guid.NewGuid():N}",
                AutoReconnect = false
            });
            await WaitAsync(() => mqtt.CanSend && mqttReceiver.CanSend);
            mqtt.SendText = "mqtt roundtrip";
            await mqtt.SendCommand.ExecuteAsync(null);
            await WaitAsync(() => mqttReceiver.Terminal.Entries.Any(entry => entry.DisplayText.Contains("mqtt roundtrip", StringComparison.Ordinal)));
            Require(mqtt.Protocol == "MQTT", "MQTT client exposes its protocol in the network session");
            Require(mqtt.Detail.Contains("embedded-debugger/smoke"), "MQTT session detail exposes the configured topic");
            Require(mqttReceiver.Terminal.ReceivedBytes > 0, "MQTT clients subscribe and publish through the configured topic");
            await mqtt.StopAsync(); await mqttReceiver.StopAsync(); await mqttServer.StopAsync();

            var failed = await vm.CreateNetworkSessionAsync(new TransportOptions { LocalPort = 0, RemoteHost = "127.0.0.1", RemotePort = FreePort(), AutoReconnect = false });
            await WaitAsync(() => failed.State == "连接失败");
            Require(failed.CanStart && !failed.CanSend, "Failed TCP without retry exposes failure and allows restart");

            window.Width = 1340; window.Height = 880; tabs.SelectedIndex = 1;
            var manager = vm.RemoteDebug!;
            var connectionDialog = new ConnectionProfileWindow(manager, new RemoteConnectionProfile { Protocol = RemoteProtocol.Vnc, Port = 5900, Name = "VNC 连接" }, false) { Owner = window };
            connectionDialog.Show();
            await FileWorkspaceChecks.CaptureAsync(connectionDialog, "embedded-debugger-vnc-profile.png");
            Require(!connectionDialog.IsSsh && !connectionDialog.HasUsername && connectionDialog.IsPasswordAuthentication, "VNC profile hides SSH-only fields and username");
            ((TextBox)connectionDialog.FindName("PortEditor")).Text = "invalid";
            Descendants<Button>(connectionDialog).Single(b => Equals(b.Content, "保存")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(connectionDialog.IsVisible && ((TextBlock)connectionDialog.FindName("ValidationMessage")).Text.Contains("有效端口"), "Invalid port input cannot silently save the previous value");
            connectionDialog.Close();
            var networkDialog = new NetworkConnectionWindow { Owner = window }; networkDialog.Show();
            await FileWorkspaceChecks.CaptureAsync(networkDialog, "embedded-debugger-network-profile.png");
            networkDialog.Close();
            var profile = new RemoteConnectionProfile { Name = "远程桌面测试", Protocol = RemoteProtocol.Rdp, Host = "127.0.0.1", Port = 3389, Username = "regression", Password = "memory-only" };
            var editor = new RemoteProfileEditorViewModel(profile);
            editor.Protocol = RemoteProtocol.Vnc;
            Require(editor.Port == 5900, "Switching to VNC selects its default port");
            editor.Protocol = RemoteProtocol.Rdp;
            Require(editor.Port == 3389, "Switching back to RDP selects port 3389");
            using var rdpListener = new TcpListener(IPAddress.Loopback, 0); rdpListener.Start(); profile.Port = ((IPEndPoint)rdpListener.LocalEndpoint).Port;
            var accepted = rdpListener.AcceptTcpClientAsync();
            manager.OpenDesktop(profile);
            var rdpSession = manager.SelectedDesktop!;
            await WaitAsync(() => accepted.IsCompleted || rdpSession.ConnectionStatus == "连接失败");
            Require(accepted.IsCompletedSuccessfully, $"Native MSTSC initializes and begins a real RDP handshake: {rdpSession.Message}");
            using (var peer = await accepted)
            {
                var packet = new byte[512]; var received = await peer.GetStream().ReadAsync(packet).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                Require(received >= 11 && packet[0] == 3, "RDP handshake uses the native TPKT/X.224 transport");
                manager.SelectLocalShellCommand.Execute(null);
                manager.SelectTab(rdpSession);
                tabs.SelectedIndex = 2; await Task.Delay(120); tabs.SelectedIndex = 1; await Task.Delay(120);
                Require(rdpSession.IsConnecting, "Native RDP transport remains active when switching main function pages");
                Require(manager.SelectedDesktop == rdpSession && manager.Tabs.Contains(rdpSession), "Desktop tabs survive switching to local Shell and back");
            }
            await WaitAsync(() => rdpSession.ConnectionStatus == "连接失败", 25000);
            Require(!rdpSession.IsConnected && rdpSession.ReconnectCommand.CanExecute(null), "RDP rejection remains in a retryable error tab");
            await FileWorkspaceChecks.CaptureAsync(window, "embedded-debugger-rdp-error.png");
            manager.CloseDesktop(rdpSession);
            Require(!manager.Tabs.Contains(rdpSession) && rdpSession.IsDisposed, "Closing RDP removes the tab and disposes its native control");

            await using var fixture = new VncFixture(authenticate: true);
            manager.OpenDesktop(new RemoteConnectionProfile { Name = "VNC 回环桌面", Protocol = RemoteProtocol.Vnc, Host = "127.0.0.1", Port = fixture.Port, Password = "secret" });
            var vncSession = manager.SelectedDesktop!;
            await WaitAsync(() => vncSession.IsConnected || vncSession.ConnectionStatus == "连接失败");
            Require(vncSession.IsConnected, $"VNC authenticates using the fixed independent DES challenge vector: {vncSession.Message}");
            await WaitAsync(() => vncSession.NetworkStatus.Milliseconds.HasValue);
            await WaitAsync(() => Descendants<DesktopWorkspace>(window).Where(c => c.DataContext == vncSession).SelectMany(Descendants<Image>).Any(i => i.Source is not null));
            var desktop = Descendants<DesktopWorkspace>(window).Single(c => c.DataContext == vncSession);
            var framebuffer = Descendants<Image>(desktop).Single();
            Require(framebuffer.Source.Width == 320 && framebuffer.Source.Height == 200, "RFB Raw framebuffer is rendered inside the desktop tab");
            var source = PresentationSource.FromVisual(window)!;
            framebuffer.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Back) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            framebuffer.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Back) { RoutedEvent = Keyboard.PreviewKeyUpEvent });
            await WaitAsync(() => fixture.Keys.Count >= 2);
            Require(fixture.Keys.Take(2).SequenceEqual(new[] { (0xff08u, true), (0xff08u, false) }), "VNC forwards both key-down and key-up for Backspace");
            vncSession.IsFullscreen = true; window.UpdateLayout();
            Require(window.WindowStyle == WindowStyle.None && manager.IsTerminalFullscreen, "Desktop sessions reuse true application fullscreen");
            vncSession.IsFullscreen = false;
            await FileWorkspaceChecks.CaptureAsync(window, "embedded-debugger-vnc.png");
            manager.CloseDesktop(vncSession);
            Require(!manager.Tabs.Contains(vncSession), "Closing VNC removes the tab and stops the session");
            await CheckVncResizeAsync();
            await CheckVncIncrementalAsync();
        }
        finally { await vm.DisposeAsync(); window.Close(); }
    }
    private static async Task CheckVncResizeAsync()
    {
        await using var fixture = new VncFixture(authenticate: false, resize: true);
        await using var client = new VncClient();
        var frames = new List<VncFrame>();
        client.FrameReceived = frame => { lock (frames) frames.Add(frame with { Pixels = (byte[])frame.Pixels.Clone() }); return Task.CompletedTask; };
        await client.ConnectAsync("127.0.0.1", fixture.Port, "");
        await WaitAsync(() => { lock (frames) return frames.Count >= 2; });
        lock (frames)
        {
            Require(frames[1].Width == 2 && frames[1].Height == 2, "RFB DesktopSize safely reallocates the framebuffer");
            Require(frames[1].Pixels.AsSpan(0, 4).SequenceEqual(frames[1].Pixels.AsSpan(8, 4)), "RFB CopyRect copies the requested source pixels");
        }
        await client.SendPointerAsync(500, -1, 1); await client.SendClipboardAsync("clipboard test");
        await WaitAsync(() => fixture.Pointer is not null && fixture.Clipboard is not null);
        Require(fixture.Pointer == (1, 0, 1) && fixture.Clipboard == "clipboard test", "VNC clamps pointer coordinates and sends clipboard text");
    }
    private static async Task CheckVncIncrementalAsync()
    {
        await using var fixture = new VncFixture(authenticate: false, incremental: true);
        await using var client = new VncClient();
        byte[]? first = null;
        var rendered = 0;
        var bitmap = new System.Windows.Media.Imaging.WriteableBitmap(320, 200, 96, 96, PixelFormats.Bgr32, null);
        client.FrameReceived = async frame =>
        {
            if (first is null) first = frame.Pixels;
            else Require(ReferenceEquals(first, frame.Pixels) && frame.X == 5 && frame.Y == 7 && frame.UpdatedWidth == 2 && frame.UpdatedHeight == 2,
                "Incremental VNC reuses the framebuffer and reports only the dirty region");
            Require(!Application.Current.Dispatcher.CheckAccess(), "VNC parsing runs off the UI thread");
            await Application.Current.Dispatcher.InvokeAsync(() => bitmap.WritePixels(new Int32Rect(frame.X, frame.Y, frame.UpdatedWidth, frame.UpdatedHeight), frame.Pixels, frame.Width * 4, (frame.Y * frame.Width + frame.X) * 4));
            var before = frame.Pixels[0]; await Task.Delay(20);
            Require(frame.Pixels[0] == before, "Borrowed VNC pixels remain stable until rendering completes");
            rendered++;
        };
        await client.ConnectAsync("127.0.0.1", fixture.Port, "");
        await WaitAsync(() => rendered == 2);
        var pixels = new byte[320 * 200 * 4]; bitmap.CopyPixels(pixels, 320 * 4, 0);
        Require(pixels.AsSpan((7 * 320 + 5) * 4, 8).SequenceEqual(new byte[] { 1, 2, 3, 0, 4, 5, 6, 0 }) && pixels[0] == 40,
            "WPF updates the VNC region at the correct offset without changing surrounding pixels");
    }
    private static int FreePort() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
    private static async Task WaitAsync(Func<bool> predicate, int timeout = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeout);
        while (!predicate()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("Desktop/network regression timed out."); await Task.Delay(30); }
    }
    private static void Require(bool value, string description) { if (!value) throw new InvalidOperationException(description); Console.WriteLine($"PASS {description}"); }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T item) yield return item;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private sealed class VncFixture : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _worker;
        public int Port { get; }
        public List<(uint, bool)> Keys { get; } = [];
        public (int, int, byte)? Pointer { get; private set; }
        public string? Clipboard { get; private set; }
        public VncFixture(bool authenticate, bool resize = false, bool incremental = false)
        {
            _listener.Start(); Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _worker = RunAsync(authenticate, resize, incremental);
        }
        private async Task RunAsync(bool authenticate, bool resize, bool incremental)
        {
            try
            {
                using var peer = await _listener.AcceptTcpClientAsync(_stop.Token); var stream = peer.GetStream();
                async Task<byte[]> Read(int count) { var bytes = new byte[count]; await stream.ReadExactlyAsync(bytes, _stop.Token); return bytes; }
                async Task Write(byte[] bytes) { // Split protocol fields across writes to exercise ReadExactlyAsync.
                    if (bytes.Length > 1) { await stream.WriteAsync(bytes.AsMemory(0, 1), _stop.Token); await stream.WriteAsync(bytes.AsMemory(1), _stop.Token); }
                    else await stream.WriteAsync(bytes, _stop.Token);
                }
                await Write(Encoding.ASCII.GetBytes("RFB 003.008\n")); await Read(12);
                await Write([1, authenticate ? (byte)2 : (byte)1]); await Read(1);
                if (authenticate)
                {
                    await Write(Enumerable.Range(0, 16).Select(i => (byte)i).ToArray());
                    var response = await Read(16);
                    Require(Convert.ToHexString(response) == "EE22539F33A5983EC12F9C2EDBC995DD", "VNC password response matches independent DES fixture");
                }
                await Write([0, 0, 0, 0]); await Read(1);
                var name = Encoding.UTF8.GetBytes("Embedded Debugger · VNC test desktop");
                var init = new byte[24 + name.Length]; Put16(init, 0, 320); Put16(init, 2, 200); BinaryPrimitives.WriteUInt32BigEndian(init.AsSpan(20), (uint)name.Length); name.CopyTo(init, 24); await Write(init);
                await Read(20); var enc = await Read(4); await Read(BinaryPrimitives.ReadUInt16BigEndian(enc.AsSpan(2)) * 4);
                var sent = 0;
                while (!_stop.IsCancellationRequested)
                {
                    var type = (await Read(1))[0];
                    if (type == 3)
                    {
                        await Read(9);
                        if (sent == 0)
                        {
                            var packet = new byte[16 + 320 * 200 * 4]; packet[3] = 1; Put16(packet, 8, 320); Put16(packet, 10, 200);
                            for (var y = 0; y < 200; y++) for (var x = 0; x < 320; x++) { var i = 16 + (y * 320 + x) * 4; packet[i] = (byte)(40 + y / 2); packet[i + 1] = (byte)(80 + x / 3); packet[i + 2] = 35; }
                            await Write(packet); sent++;
                        }
                        else if (sent == 1 && incremental)
                        {
                            var packet = new byte[32]; packet[3] = 1;
                            Put16(packet, 4, 5); Put16(packet, 6, 7); Put16(packet, 8, 2); Put16(packet, 10, 2);
                            new byte[] { 1, 2, 3, 0, 4, 5, 6, 0, 7, 8, 9, 0, 10, 11, 12, 0 }.CopyTo(packet, 16);
                            await Write(packet); sent++;
                        }
                        else if (sent == 1 && resize)
                        {
                            var packet = new byte[4 + 12 + 12 + 16 + 12 + 4]; packet[3] = 3;
                            Put16(packet, 8, 2); Put16(packet, 10, 2); BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(12), -223);
                            Put16(packet, 20, 2); Put16(packet, 22, 2);
                            new byte[] { 1, 2, 3, 0, 4, 5, 6, 0, 7, 8, 9, 0, 10, 11, 12, 0 }.CopyTo(packet, 28);
                            Put16(packet, 46, 1); Put16(packet, 48, 2); Put16(packet, 50, 1); BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(52), 1);
                            await Write(packet); sent++;
                        }
                    }
                    else if (type == 4) { var bytes = await Read(7); Keys.Add((BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(3)), bytes[0] != 0)); }
                    else if (type == 5) { var bytes = await Read(5); Pointer = (BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(1)), BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(3)), bytes[0]); }
                    else if (type == 6) { var header = await Read(7); Clipboard = Encoding.Latin1.GetString(await Read((int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(3)))); }
                    else throw new InvalidOperationException($"Unexpected client RFB message {type}");
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException) { }
        }
        private static void Put16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset), value);
        public async ValueTask DisposeAsync() { _stop.Cancel(); _listener.Stop(); await _worker; _stop.Dispose(); }
    }
}
