using System.IO;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EmbedDesk.App.Controls;
using EmbedDesk.App.ViewModels;
using EmbedDesk.Core.Abstractions;
using EmbedDesk.Core.Models;
using EmbedDesk.Infrastructure.Bridging;
using EmbedDesk.Infrastructure.Diagnostics;
using EmbedDesk.Infrastructure.Networking;

namespace EmbedDesk.App.SmokeTests;

internal static class DebugConnectionChecks
{
    internal static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "EmbedDesk-DebugConnections-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await CheckStorageAsync(directory);
            await CheckQueueAsync();
            await CheckUdpAsync();
            var ports = new FakeSerialService();
            await using var remote = new RemoteSerialClient();
            await using var vm = new MainWindowViewModel(new TcpUdpTransportService(), ports, new FakeBridge(), remote, remoteSettingsDirectory: Path.Combine(directory, "workspace"));
            var main = new MainWindow(vm); main.Show();
            var workspace = (DebugAssistantWorkspace)main.FindName("DebugAssistant");
            try
            {
                ((TabControl)main.FindName("MainWorkspaceTabs")).SelectedIndex = 1;
                await vm.ScanSerialPortsCommand.ExecuteAsync(null);
                await Task.Delay(80); main.UpdateLayout();
                await CheckEditorAsync(vm, main);
                await CheckSessionsAsync(vm, workspace, main, ports);
                await CheckNetworkRolesAsync(vm, workspace, main);
                await CheckDeviceRolesAsync(workspace, main);
                await workspace.ShutdownAsync();
            }
            finally { await workspace.ShutdownAsync(); main.Close(); await Until(() => !main.IsVisible); }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task CheckStorageAsync(string directory)
    {
        var preset = new DebugConnectionPreset { Name = "回环目标", Endpoint = new(DebugToolTransport.TcpClient, "127.0.0.1", 9000) { LocalHost = "127.0.0.1", TimeoutMilliseconds = 2500 } };
        using (var store = new AppSettingsStore(directory)) { store.SaveDebugPreset(preset); store.SaveNow(); }
        using (var store = new AppSettingsStore(directory))
        {
            Require(store.GetDebugPresets().Single() == preset, "Connection presets survive reopening with binding and timeout intact");
            var updated = preset with { Endpoint = preset.Endpoint with { Port = 9001 } };
            store.SaveDebugPreset(updated);
            Require(store.GetDebugPresets().Single() == updated && preset.Endpoint.Port == 9000, "Updating a preset preserves existing immutable endpoint snapshots");
            var package = ConfigurationTransferService.Export([], [], [], debugPresets: store.GetDebugPresets());
            var imported = ConfigurationTransferService.Import(package).DebugPresets.Single();
            Require(imported.Id != updated.Id && imported.Endpoint == updated.Endpoint, "Portable configuration preserves debug parameters and regenerates IDs");
            store.DeleteDebugPreset(preset.Id); store.SaveNow();
        }
        using (var store = new AppSettingsStore(directory)) Require(store.GetDebugPresets().Count == 0, "Deleting a preset persists without restoring a stale entry");
        foreach (var (version, before, after) in new[] { (1, 0, 1), (1, 1, 2), (1, 2, 1), (2, 1, 1), (2, 3, 1), (2, 4, 3), (2, 5, 4) })
        {
            var migration = Path.Combine(directory, $"migration-{version}-{before}"); Directory.CreateDirectory(migration);
            await File.WriteAllTextAsync(Path.Combine(migration, "app-settings.json"), JsonSerializer.Serialize(new
            {
                SchemaVersion = version, LastWorkspaceIndex = before,
                NetworkPresets = new[] { new NetworkConnectionPreset { Name = "旧网络连接", LocalHost = "127.0.0.1", LocalPort = 9000, Mode = TransportMode.Server } }
            }));
            using var store = new AppSettingsStore(migration);
            Require(store.Snapshot().LastWorkspaceIndex == after && store.GetDebugPresets().Single().Endpoint.Transport == DebugToolTransport.TcpServer,
                $"Schema {version} navigation {before} migrates to {after} and retains the TCP preset");
        }
    }

    private static async Task CheckQueueAsync()
    {
        var captures = 0; var consumed = 0; var faults = 0;
        using (var queue = new BoundedDispatcherQueue<byte[]>(Application.Current.Dispatcher, _ => consumed++, () => faults++))
        {
            for (var index = 0; index < 200; index++) queue.Post(() => { captures++; return new byte[65536]; }, 65536);
            Require(captures == 64 && consumed == 0, "Receiver queues cap pending bytes before copying payloads");
            await Until(() => faults == 1);
            Require(consumed == 64, "A bounded queue drains in batches and reports overload once");
        }
        var old = new TestTransport();
        await using var session = new NetworkSessionViewModel(old, (_, _, _) => { });
        old.Emit([1, 2, 3]);
        var replacement = new TestTransport();
        await session.ReconfigureAsync(replacement);
        await session.StartAsync(); replacement.Emit([4]);
        await Until(() => session.Terminal.ReceivedBytes == 1);
        Require(session.Terminal.Entries.Single().Data[0] == 4, "Reconfiguration discards callbacks queued by the previous connection");
        for (var index = 0; index < 1100; index++) replacement.Emit([1]);
        await Until(() => session.State == "连接失败");
        Require(replacement.Stops > 0 && session.LastMessage.Contains("处理速度", StringComparison.Ordinal), "Receive overload stops only the owning connection with an explicit error");
        await session.StartAsync(); replacement.Emit([5]);
        await Until(() => session.State == "已连接");
        Require(session.CanSend, "A stopped overloaded connection can start a fresh bounded receiver");
    }

    private static async Task CheckEditorAsync(MainWindowViewModel vm, MainWindow main)
    {
        var workspace = (DebugAssistantWorkspace)main.FindName("DebugAssistant");
        Require(vm.SerialSessions.Count == 0 && workspace.Tabs.Count == 0 && ((Border)workspace.FindName("EmptyState")).IsVisible,
            "Debug assistant starts empty without opening any serial or network connection");
        Require(workspace.FindName("ConnectionPresetBox") is null && workspace.FindName("ConnectionBar") is null,
            "The debug assistant has no connection preset row");
        await FileWorkspaceChecks.CaptureAsync(main, "embeddesk-debug-empty.png");
        ((Button)workspace.FindName("EmptyNewSessionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var dialog = workspace.SessionDialog!;
        main.UpdateLayout();
        var windows = Application.Current.Windows.Count;
        Require(dialog.IsVisible && !((Grid)workspace.FindName("WorkspaceSurface")).IsEnabled && main.OwnedWindows.Count == 0,
            "Empty-state creation opens an internal modal and blocks the workspace without a native window");
        await FileWorkspaceChecks.CaptureAsync(main, "embeddesk-debug-new-session.png");
        main.Width = main.MinWidth; main.Height = main.MinHeight;
        await FileWorkspaceChecks.CaptureAsync(main, "embeddesk-debug-new-session-small.png");
        var kinds = new[] { DebugSessionKind.Serial, DebugSessionKind.NetworkClient, DebugSessionKind.NetworkServer, DebugSessionKind.Pid, DebugSessionKind.Camera, DebugSessionKind.Mqtt };
        Require(Find<Button>(dialog).Where(b => b.Tag is string).Select(b => b.Tag).SequenceEqual(kinds.Select(kind => (object)kind.ToString())) && !Find<TabControl>(workspace).Any(),
            "The picker groups TCP/UDP by role and omits HTTP without nested tabs");
        foreach (var kind in kinds)
        {
            Find<Button>(dialog).Single(b => Equals(b.Tag, kind.ToString())).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            main.UpdateLayout();
            if (kind == DebugSessionKind.Serial) ((ComboBox)dialog.FindName("PortBox")).Text = "COM99";
            Require(dialog.ReadEndpoint().Transport == kind switch
            {
                DebugSessionKind.NetworkClient or DebugSessionKind.Mqtt => DebugToolTransport.TcpClient,
                DebugSessionKind.NetworkServer => DebugToolTransport.TcpServer,
                _ => DebugToolTransport.Serial
            }, $"The internal form builds {kind} settings");
            var action = (Button)dialog.FindName("ApplyButton");
            Require(action.IsVisible && action.TransformToAncestor(workspace).TransformBounds(new Rect(action.RenderSize)).Bottom <= workspace.ActualHeight,
                "Dialog actions remain inside the minimum main window");
            await FileWorkspaceChecks.CaptureAsync(main, $"embeddesk-debug-new-{kind}-small.png");
            if (kind is DebugSessionKind.NetworkClient or DebugSessionKind.NetworkServer)
            {
                var protocol = (ComboBox)dialog.FindName("TransportBox");
                Require(protocol.Items.Cast<KeyValuePair<DebugToolTransport, string>>().Select(p => p.Value).SequenceEqual(new[] { "TCP", "UDP" }),
                    $"{kind} selects TCP or UDP inside its connection form");
                protocol.SelectedValue = kind == DebugSessionKind.NetworkClient ? DebugToolTransport.UdpTarget : DebugToolTransport.UdpListen;
                main.UpdateLayout();
                Require(dialog.ReadEndpoint().Transport == (kind == DebugSessionKind.NetworkClient ? DebugToolTransport.UdpTarget : DebugToolTransport.UdpListen)
                    && ((TextBlock)dialog.FindName("HostLabel")).Text == (kind == DebugSessionKind.NetworkClient ? "远程地址" : "本机监听地址")
                    && !((Grid)dialog.FindName("TimeoutFields")).IsVisible && !((CheckBox)dialog.FindName("ReconnectBox")).IsVisible,
                    $"{kind} uses the correct UDP role and hides TCP-only settings");
                await FileWorkspaceChecks.CaptureAsync(main, $"embeddesk-debug-new-{kind}-Udp-small.png");
                if (kind == DebugSessionKind.NetworkServer)
                {
                    var locked = (CheckBox)dialog.FindName("LockUdpPeerBox");
                    locked.IsChecked = true;
                    Require(dialog.ReadEndpoint().Transport == DebugToolTransport.Udp, "UDP server can explicitly lock to its first sender");
                    protocol.SelectedValue = DebugToolTransport.TcpServer;
                    Require(dialog.ReadEndpoint().Transport == DebugToolTransport.TcpServer && !locked.IsVisible, "UDP peer locking does not alter TCP server settings");
                }
            }
            ((Button)dialog.FindName("BackButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        Require(Application.Current.Windows.Count == windows && vm.Sessions.Count == 0 && vm.SerialSessions.Count == 0,
            "Browsing types creates neither native windows nor sessions");
        ((Button)dialog.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(workspace.SessionDialog is null && ((Grid)workspace.FindName("WorkspaceSurface")).IsEnabled && workspace.Tabs.Count == 0,
            "Cancel restores the empty workspace without creating a tab");

        ((Button)workspace.FindName("NewSessionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        main.UpdateLayout();
        dialog = workspace.SessionDialog!;
        dialog.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(main)!, 0, System.Windows.Input.Key.Escape)
            { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
        Require(workspace.SessionDialog is null, "Escape dismisses the internal dialog");
        ((Button)workspace.FindName("NewSessionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        main.UpdateLayout();
        dialog = workspace.SessionDialog!;
        dialog.ShowSettings(DebugSessionKind.Pid);
        var transportBox = (ComboBox)dialog.FindName("TransportBox");
        foreach (var transport in new[] { DebugToolTransport.Serial, DebugToolTransport.TcpClient, DebugToolTransport.TcpServer, DebugToolTransport.Udp, DebugToolTransport.UdpTarget })
        {
            transportBox.SelectedValue = transport switch
            {
                DebugToolTransport.Serial => DebugToolTransport.Serial,
                DebugToolTransport.TcpClient or DebugToolTransport.UdpTarget => DebugToolTransport.TcpClient,
                _ => DebugToolTransport.TcpServer
            };
            ((ComboBox)dialog.FindName("ProtocolBox")).SelectedValue = transport is DebugToolTransport.Udp or DebugToolTransport.UdpTarget ? TransportProtocol.Udp : TransportProtocol.Tcp;
            Require(dialog.ReadEndpoint().Transport == transport, $"PID connection form supports {transport}");
        }
        Require(!transportBox.Items.Cast<KeyValuePair<DebugToolTransport, string>>().Any(p => p.Key == DebugToolTransport.UdpListen),
            "Tool sessions exclude mixed multi-peer UDP streams");
        ((TextBox)dialog.FindName("PortNumberBox")).Text = "invalid";
        ((Button)dialog.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        main.UpdateLayout();
        Require(((TextBlock)dialog.FindName("ErrorText")).IsVisible && workspace.Tabs.Count == 0,
            "Validation keeps the internal form open without creating a partial tab");
        ((Button)dialog.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((Button)workspace.FindName("NewSessionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        main.UpdateLayout();
        dialog = workspace.SessionDialog!;
        Find<Button>(dialog).Single(b => Equals(b.Tag, "Serial")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((Button)dialog.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => workspace.SessionDialog is null && workspace.Tabs.Count == 1);
        Require(vm.SelectedSerialSession is { IsOpen: false }, "Creating a serial session does not open the port");
        await FileWorkspaceChecks.CaptureAsync(main, "embeddesk-debug-serial-no-presets-small.png");
    }
    private static async Task CheckSessionsAsync(MainWindowViewModel vm, DebugAssistantWorkspace workspace, MainWindow main, FakeSerialService ports)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var endpoint = new DebugToolEndpoint(DebugToolTransport.TcpClient, "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port) { TimeoutMilliseconds = 2500, LocalHost = "127.0.0.1" };

        var first = workspace.AddPidSession()!; var second = workspace.AddPidSession()!;
        var firstView = (PidDebugView)first.Content; var secondView = (PidDebugView)second.Content;
        var filter = (ComboBox)firstView.FindName("FilterBox"); filter.SelectedIndex = 2;
        var peers = new List<TcpClient>();
        foreach (var tab in new[] { first, second })
        {
            workspace.SelectTab(tab);
            await workspace.ApplyConnectionAsync(tab, endpoint);

            Require(workspace.CaptureConnection(tab) == endpoint, "Connection settings apply only to the selected stopped tab");
            Connect((PidDebugView)tab.Content);
            peers.Add(await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(4)));
        }
        using var peer1 = peers[0];
        using var peer2 = peers[1];
        await Until(() => firstView.ConnectionStatus == "已连接" && secondView.ConnectionStatus == "已连接");
        await peer1.GetStream().WriteAsync(DebugToolPacketReader.Encode(false, 1, 2, [10, 0, 0, 0]));
        await peer2.GetStream().WriteAsync(DebugToolPacketReader.Encode(false, 1, 2, [20, 0, 0, 0]));
        workspace.SelectTab(first);
        await Until(() => ((PidPlot)firstView.FindName("Plot")).Series.Any(s => s.Samples.Any(p => p.Actual == 10)));
        workspace.SelectTab(second);
        await Until(() => ((PidPlot)secondView.FindName("Plot")).Series.Any(s => s.Samples.Any(p => p.Actual == 20)));
        Require(!((PidPlot)secondView.FindName("Plot")).Series.Any(s => s.Samples.Any(p => p.Actual == 10)), "Two PID tabs using the same endpoint keep independent connections and histories");

        Require(workspace.CaptureConnection(first) == endpoint && workspace.CaptureConnection(second) == endpoint, "Each running connection retains its own parameter snapshot");
        try { await workspace.ApplyConnectionAsync(first, endpoint); throw new InvalidOperationException("Active settings unexpectedly applied"); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("先停止", StringComparison.Ordinal)) { }
        await firstView.ConnectionPanel.StopAsync();
        await workspace.ApplyConnectionAsync(first, endpoint with { TimeoutMilliseconds = 3000 });
        Require(filter.SelectedIndex == 2 && ((PidPlot)firstView.FindName("Plot")).Series.Any(s => s.Samples.Any(p => p.Actual == 10)), "Applying new connection settings preserves PID smoothing and samples");
        workspace.SelectTab(first); main.Width = main.MinWidth; main.Height = main.MinHeight;
        await FileWorkspaceChecks.CaptureAsync(main, "embeddesk-debug-assistant-pid-small.png");
        Require(Find<ScrollViewer>(firstView).All(s => s.ScrollableHeight < 1), "Embedded PID parameters fit at minimum size without scrolling");
        var status = (TextBlock)firstView.FindName("ChannelStatus");
        Require(status.IsVisible && status.TransformToAncestor(firstView).TransformBounds(new Rect(status.RenderSize)).Bottom <= firstView.ActualHeight, "PID actual value and device status stay visible above the plot");

        var serialTab = workspace.Tabs.Single(t => t.Owner is SerialSessionViewModel);
        var serial = (SerialSessionViewModel)serialTab.Owner;
        var serialEndpoint = new DebugToolEndpoint(DebugToolTransport.Serial, "127.0.0.1", 9000, new() { PortName = "COM3", BaudRate = 230400, DataBits = 7, Parity = Parity.Even, StopBits = StopBits.Two, Handshake = Handshake.XOnXOff });
        await workspace.ApplyConnectionAsync(serialTab, serialEndpoint); await serial.OpenSelectedSerialCommand.ExecuteAsync(null);
        Require(ports.LastSettings is { BaudRate: 230400, DataBits: 7, Parity: Parity.Even, StopBits: StopBits.Two, Handshake: Handshake.XOnXOff, DtrEnable: false, RtsEnable: false }, "Shared serial settings reach the channel without asserting DTR or RTS");
        await workspace.ApplyConnectionAsync(first, serialEndpoint); Connect(firstView);
        await Until(() => firstView.ConnectionStatus == "连接失败");
        Require(serial.IsOpen && secondView.ConnectionPanel.IsActive, "Serial ownership conflicts do not stop the owning tab or other PID tabs");
        await serial.CloseAsync(); Connect(firstView);
        await Until(() => firstView.ConnectionStatus == "已连接");
        await firstView.ShutdownAsync(); await firstView.ShutdownAsync();
        Require(secondView.ConnectionPanel.IsActive, "Repeated tool shutdown waits for cleanup and leaves other connections active");
        var cameraTab = workspace.AddCameraSession()!;
        var camera = (CameraDebugView)cameraTab.Content;
        await workspace.ApplyConnectionAsync(cameraTab, endpoint);
        ((Button)camera.ConnectionPanel.FindName("ConnectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        using (var cameraPeer = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(4)))
        {
            await Until(() => camera.ConnectionStatus == "已连接"); main.UpdateLayout();
            Click(camera, "读取");
            var request = new byte[10];
            await cameraPeer.GetStream().ReadExactlyAsync(request).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            await workspace.CloseTabAsync(cameraTab, false).WaitAsync(TimeSpan.FromSeconds(2));
            Require(!camera.ConnectionPanel.IsActive && secondView.ConnectionPanel.IsActive, "Closing a camera cancels its pending register reply and joins cleanup without affecting PID");
        }
        await workspace.CloseTabAsync(first, false); await workspace.CloseTabAsync(second, false);

        // Exercise the real plus button and the internal connection form.
        ((Button)workspace.FindName("NewSessionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        main.UpdateLayout();
        var dialog = workspace.SessionDialog!;
        Find<Button>(dialog).Single(b => Equals(b.Tag, "NetworkServer")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((TextBox)dialog.FindName("HostBox")).Text = "127.0.0.1";
        ((TextBox)dialog.FindName("PortNumberBox")).Text = "0";
        await FileWorkspaceChecks.CaptureAsync(main, "embeddesk-new-debug-tcp-server.png");
        ((Button)dialog.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => workspace.SessionDialog is null && vm.Sessions.Count == 1 && vm.Sessions[0].State == "监听中");
        Require(workspace.Tabs.Single(t => t.Owner is NetworkSessionViewModel).IsSelected && !Find<TabControl>(workspace).Any(),
            "The internal dialog creates a TCP server directly in the single session tab row");
        var serverTab = workspace.Tabs.Single(t => t.Owner is NetworkSessionViewModel);
        await vm.Sessions[0].StopAsync();
        ((Button)workspace.FindName("EditConnectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        dialog = workspace.SessionDialog!;
        ((TextBox)dialog.FindName("PortNumberBox")).Text = "9001";
        ((Button)dialog.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => workspace.SessionDialog is null);
        Require(vm.Sessions[0].Options.LocalPort == 9001 && vm.Sessions[0].CanStart, "Editing a stopped session applies parameters without restarting it");
        await workspace.CloseTabAsync(serverTab, false);
        await workspace.CloseTabAsync(serialTab, false);
        Require(workspace.Tabs.Count == 0 && vm.SerialSessions.Count == 0 && ((Border)workspace.FindName("EmptyState")).IsVisible,
            "Closing the last session returns to the empty start screen");
    }
    private static async Task CheckNetworkRolesAsync(MainWindowViewModel vm, DebugAssistantWorkspace workspace, MainWindow main)
    {
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        foreach (var client in new[] { true, false })
        {
            ((Button)workspace.FindName("NewSessionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            main.UpdateLayout();
            var dialog = workspace.SessionDialog!;
            var kind = client ? "NetworkClient" : "NetworkServer";
            Find<Button>(dialog).Single(b => Equals(b.Tag, kind)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((ComboBox)dialog.FindName("TransportBox")).SelectedValue = client ? DebugToolTransport.UdpTarget : DebugToolTransport.UdpListen;
            ((TextBox)dialog.FindName("HostBox")).Text = "127.0.0.1";
            ((TextBox)dialog.FindName("PortNumberBox")).Text = client ? ((IPEndPoint)peer.Client.LocalEndPoint!).Port.ToString() : "0";
            ((TextBox)dialog.FindName("LocalHostBox")).Text = "127.0.0.1";
            ((Button)dialog.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => workspace.SessionDialog is null && vm.Sessions.Count == 1 && vm.Sessions[0].CanSend);
            var session = vm.Sessions.Single();
            Require(session.Options.Protocol == TransportProtocol.Udp && session.Options.Mode == (client ? TransportMode.Client : TransportMode.Server)
                && !session.Options.LockFirstUdpPeer, $"The {kind} picker creates a UDP session with the selected role");
            if (client)
            {
                await session.SendAsync(new byte[] { 0x31 }, CancellationToken.None);
                var packet = await peer.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
                Require(packet.Buffer.SequenceEqual(new byte[] { 0x31 }), "UDP client sends to the address configured inside the client form");
                await peer.SendAsync(new byte[] { 0x32 }, packet.RemoteEndPoint);
            }
            else
            {
                await peer.SendAsync(new byte[] { 0x32 }, IPEndPoint.Parse(session.LocalEndpoint));
                await Until(() => session.Terminal.ReceivedBytes == 1);
                await session.SendAsync(new byte[] { 0x33 }, CancellationToken.None);
                Require((await peer.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer.SequenceEqual(new byte[] { 0x33 }),
                    "UDP server receives at its configured listener and replies to the sender");
            }
            await Until(() => session.Terminal.ReceivedBytes == 1);
            var tab = workspace.Tabs.Single();
            await session.StopAsync();
            var endpoint = workspace.CaptureConnection(tab);
            ((Button)workspace.FindName("EditConnectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            main.UpdateLayout();
            dialog = workspace.SessionDialog!;
            Require(((TextBlock)dialog.FindName("Heading")).Text.Contains(client ? "TCP/UDP 客户端" : "TCP/UDP 服务端", StringComparison.Ordinal)
                && dialog.ReadEndpoint() == endpoint, $"Editing {kind} restores UDP and its endpoint without changing roles");
            if (!client)
            {
                ((CheckBox)dialog.FindName("LockUdpPeerBox")).IsChecked = true;
                ((Button)dialog.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => workspace.SessionDialog is null);
                Require(session.Options.LockFirstUdpPeer && session.CanStart, "Applying UDP peer locking keeps the stopped server stopped");
                ((Button)workspace.FindName("EditConnectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                main.UpdateLayout();
                dialog = workspace.SessionDialog!;
                Require(((CheckBox)dialog.FindName("LockUdpPeerBox")).IsChecked == true && dialog.ReadEndpoint().Transport == DebugToolTransport.Udp,
                    "Editing a locked UDP server preserves its first-sender policy");
            }
            ((Button)dialog.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await workspace.CloseTabAsync(tab, false);
        }
    }
    private static async Task CheckDeviceRolesAsync(DebugAssistantWorkspace workspace, MainWindow main)
    {
        foreach (var kind in new[] { DebugSessionKind.Pid, DebugSessionKind.Camera })
        foreach (var client in new[] { true, false })
        foreach (var udp in new[] { false, true })
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var port = udp ? ((IPEndPoint)peer.Client.LocalEndPoint!).Port : ((IPEndPoint)listener.LocalEndpoint).Port;
            if (!client)
            {
                if (udp)
                {
                    using var reservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                    port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
                }
                else listener.Stop();
            }
            ((Button)workspace.FindName("NewSessionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            main.UpdateLayout();
            var dialog = workspace.SessionDialog!;
            Find<Button>(dialog).Single(b => Equals(b.Tag, kind.ToString())).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var roles = (ComboBox)dialog.FindName("TransportBox");
            Require(roles.Items.Cast<KeyValuePair<DebugToolTransport, string>>().Select(p => p.Value).ToArray()
                is ["串口", "TCP/UDP 客户端", "TCP/UDP 服务端"], $"{kind} offers serial, client and server connection types");
            roles.SelectedValue = client ? DebugToolTransport.TcpClient : DebugToolTransport.TcpServer;
            ((ComboBox)dialog.FindName("ProtocolBox")).SelectedValue = udp ? TransportProtocol.Udp : TransportProtocol.Tcp;
            ((TextBox)dialog.FindName("HostBox")).Text = "127.0.0.1";
            ((TextBox)dialog.FindName("PortNumberBox")).Text = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            main.UpdateLayout();
            Require(((Grid)dialog.FindName("ProtocolFields")).IsVisible
                && ((TextBlock)dialog.FindName("HostLabel")).Text == (client ? "远程地址" : "本机监听地址")
                && ((Grid)dialog.FindName("LocalFields")).IsVisible == client
                && ((Grid)dialog.FindName("TimeoutFields")).IsVisible == (client && !udp)
                && !((CheckBox)dialog.FindName("LockUdpPeerBox")).IsVisible, $"{kind} shows the fields for the selected role and protocol");
            var endpoint = dialog.ReadEndpoint();
            Require(endpoint.Transport == (udp ? client ? DebugToolTransport.UdpTarget : DebugToolTransport.Udp
                : client ? DebugToolTransport.TcpClient : DebugToolTransport.TcpServer), $"{kind} resolves the selected role and protocol");
            if (kind == DebugSessionKind.Pid)
            {
                var name = $"embeddesk-pid-{(client ? "client" : "server")}-{(udp ? "udp" : "tcp")}";
                main.Width = 1340; main.Height = 940;
                await FileWorkspaceChecks.CaptureAsync(main, name + "-default.png");
                main.Width = main.MinWidth; main.Height = main.MinHeight;
                await FileWorkspaceChecks.CaptureAsync(main, name + "-small.png");
                Require(((ScrollViewer)dialog.FindName("DialogScroll")).ScrollableHeight < 1, "PID network form fits at minimum window size");
            }
            ((Button)dialog.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => workspace.SessionDialog is null && workspace.Tabs.Count == 1);
            var tab = workspace.Tabs.Single();
            var connection = tab.Owner is PidDebugView pid ? pid.ConnectionPanel : ((CameraDebugView)tab.Owner).ConnectionPanel;
            Require(workspace.CaptureConnection(tab) == endpoint && !connection.IsActive, $"{kind} creates a stopped session with the configured endpoint");
            var received = new List<byte>(); var gate = new object();
            connection.ReceiveAsync = data => { lock (gate) received.AddRange(data.ToArray()); return Task.CompletedTask; };
            ((Button)connection.FindName("ConnectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (udp)
            {
                await Until(() => connection.Status == (client ? "已连接" : "监听中"));
                IPEndPoint local;
                if (client)
                {
                    await connection.SendAsync(new byte[] { 0x41 });
                    var packet = await peer.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
                    Require(packet.Buffer.SequenceEqual(new byte[] { 0x41 }), $"{kind} UDP client can send before receiving");
                    local = packet.RemoteEndPoint;
                }
                else local = new(IPAddress.Loopback, port);
                await peer.SendAsync(new byte[] { 0x42 }, local);
                await Until(() => { lock (gate) return received.Count == 1; });
                using var other = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                await other.SendAsync(new byte[] { 0x7F }, local);
                await peer.SendAsync(new byte[] { 0x43 }, local);
                await Until(() => { lock (gate) return received.Count >= 2; });
                lock (gate) Require(received.SequenceEqual(new byte[] { 0x42, 0x43 }), $"{kind} UDP isolates the configured or first device");
                await connection.SendAsync(new byte[] { 0x44 });
                Require((await peer.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer.SequenceEqual(new byte[] { 0x44 }), $"{kind} UDP replies to its device");
            }
            else
            {
                using var tcp = client ? await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(3)) : new TcpClient();
                if (!client)
                {
                    await Until(() => connection.Status == "监听中");
                    await tcp.ConnectAsync(IPAddress.Loopback, port);
                }
                await Until(() => connection.Status == "已连接");
                await tcp.GetStream().WriteAsync(new byte[] { 0x42 });
                await Until(() => { lock (gate) return received.Count == 1; });
                await connection.SendAsync(new byte[] { 0x44 });
                var reply = new byte[1]; await tcp.GetStream().ReadExactlyAsync(reply).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                Require(reply[0] == 0x44, $"{kind} TCP exchanges data using its selected role");
                await connection.StopAsync();
            }
            await connection.StopAsync();
            ((Button)workspace.FindName("EditConnectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            main.UpdateLayout();
            dialog = workspace.SessionDialog!;
            Require(dialog.ReadEndpoint() == endpoint, $"Editing {kind} restores its network role, protocol and address");
            ((Button)dialog.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => workspace.SessionDialog is null);
            Require(workspace.CaptureConnection(tab) == endpoint && !connection.IsActive, $"Applying {kind} settings preserves the stopped endpoint");
            await workspace.CloseTabAsync(tab, false);
        }
    }

    private static async Task CheckUdpAsync()
    {
        var service = new TcpUdpTransportService();
        await using var session = service.CreateSession(DebugConnectionPreset.ToOptions(new(DebugToolTransport.Udp, "127.0.0.1", 0)));
        var received = new List<byte>(); var gate = new object();
        session.MessageReceived += (_, message) => { if (message.Kind == TransportEventKind.Received) lock (gate) received.AddRange(message.Data.ToArray()); };
        using var first = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); using var second = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await session.StartAsync();
        var target = IPEndPoint.Parse(session.BoundLocalEndpoint!);
        await first.SendAsync(new byte[] { 1 }, target); await Until(() => { lock (gate) return received.Count == 1; });
        await second.SendAsync(new byte[] { 2 }, target); await first.SendAsync(new byte[] { 3 }, target);
        await Until(() => { lock (gate) return received.Count >= 2; });
        Require(received.SequenceEqual(new byte[] { 1, 3 }), "Raw UDP locks to the first peer and ignores other senders");
        await session.SendAsync(new byte[] { 4 });
        Require((await first.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3))).Buffer[0] == 4, "Locked UDP replies only to its selected peer");
        await session.StopAsync(); await session.StartAsync(); target = IPEndPoint.Parse(session.BoundLocalEndpoint!);
        await second.SendAsync(new byte[] { 5 }, target); await Until(() => { lock (gate) return received.Count == 3; });
        Require(received[^1] == 5, "Restarting UDP releases the old peer lock");

        using var tool = new DebugToolConnection(new FakeSerialService()); using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var delivered = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = (IPEndPoint)first.Client.LocalEndPoint!;
        var run = tool.RunAsync(new(DebugToolTransport.UdpTarget, "127.0.0.1", peer.Port) { LocalHost = "127.0.0.1" }, bytes => { delivered.TrySetResult(bytes.Span[0]); return Task.CompletedTask; }, _ => { }, cancel.Token);
        await tool.SendAsync(new byte[] { 7 }, cancel.Token);
        var packet = await first.ReceiveAsync(cancel.Token); await first.SendAsync(new byte[] { 8 }, packet.RemoteEndPoint);
        Require(await delivered.Task.WaitAsync(cancel.Token) == 8, "Tool UDP target mode sends before receiving and accepts the configured peer");
        cancel.Cancel(); try { await run; } catch (OperationCanceledException) { }

        var display = new TextBox(); var window = new Window { Content = display, Width = 300, Height = 180 }; window.Show();
        try
        {
            var buffer = new TerminalBufferViewModel(); var presenter = new BufferedTrafficPresenter(display); presenter.Bind(buffer); presenter.Configure(new(false, false, false, true), false);
            buffer.Add(DateTimeOffset.Now, true, Encoding.UTF8.GetBytes("first")); buffer.Add(DateTimeOffset.Now, true, Encoding.UTF8.GetBytes("second")); presenter.Flush();
            Require(display.Text == "first" + Environment.NewLine + "second" + Environment.NewLine, "UDP datagram boundaries survive when time, direction and HEX are disabled");
            presenter.Bind(null);
        }
        finally { window.Close(); }
    }

    private sealed class TestTransport : ITransportSession
    {
        public string Id { get; } = Guid.NewGuid().ToString();
        public TransportOptions Options { get; } = new();
        public TransportSessionState State { get; private set; }
        public int Stops { get; private set; }
        public event EventHandler<TransportMessage>? MessageReceived;
        public void Emit(byte[] bytes) => MessageReceived?.Invoke(this, new(DateTimeOffset.Now, Id, TransportEventKind.Received, State, null, bytes, null, null));
        public Task StartAsync(CancellationToken cancellationToken = default) { State = TransportSessionState.Connected; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default) { State = TransportSessionState.Stopped; Stops++; return Task.CompletedTask; }
        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static void Click(DependencyObject root, string content) => Find<Button>(root).Single(b => Equals(b.Content, content)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Connect(PidDebugView view) => ((Button)view.ConnectionPanel.FindName("ConnectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Find<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static async Task Until(Func<bool> predicate)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (!predicate()) await Task.Delay(20, cancel.Token);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); Console.WriteLine("PASS " + message); }
}
