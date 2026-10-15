using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Markup;
using System.Xml.Linq;
using EmbedDesk.App.Controls;
using EmbedDesk.App.ViewModels;
using EmbedDesk.Core.Abstractions;
using EmbedDesk.Core.Models;
using EmbedDesk.Infrastructure.Bridging;
using EmbedDesk.Infrastructure.Networking;

namespace EmbedDesk.App.SmokeTests;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "embeddesk-smoke-crash.txt"), e.ExceptionObject.ToString());
        var app = new SmokeApplication();
        app.DispatcherUnhandledException += (_, e) => { System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "embeddesk-dispatcher-failure.txt"), e.Exception.ToString()); Console.Error.WriteLine(e.Exception); e.Handled = true; app.Shutdown(1); };
        using var styles = typeof(Program).Assembly.GetManifestResourceStream("App.styles.xaml")!;
        var document = XDocument.Load(styles);
        var dictionary = document.Root!.Elements().Single().Elements().Single();
        dictionary.SetAttributeValue("xmlns", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        dictionary.SetAttributeValue(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
        app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        return app.Run();
    }
}

internal sealed class SmokeApplication : Application
{
    private int _exitCode;
    private void SetExitCode(int exitCode) => _exitCode = Math.Max(_exitCode, exitCode);
    protected override async void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var serial = new FakeSerialService();
        await using var remote = new RemoteSerialClient();
        var remoteDebug = new FakeRemoteDebugService();
        var settingsDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "EmbedDesk-Smoke", Guid.NewGuid().ToString("N"));
        await using var viewModel = new MainWindowViewModel(new TcpUdpTransportService(), serial, new FakeBridge(), remote, remoteDebug, settingsDirectory);
        try
        {
            if (e.Args.Contains("--debug-tools-check")) { await DebugToolsChecks.RunAsync(viewModel); SetExitCode(0); return; }
            if (e.Args.Contains("--debug-connections-check")) { await DebugConnectionChecks.RunAsync(); SetExitCode(0); return; }
            viewModel.AddSerialSession();
            if (e.Args.Contains("--device-connections-check")) { await DeviceConnectionsChecks.RunAsync(); SetExitCode(0); return; }
            if (e.Args.Contains("--virtual-serial-check")) { await VirtualSerialChecks.RunAsync(); SetExitCode(0); return; }
            if (e.Args.Contains("--device-security-check")) { await DeviceSecurityChecks.RunAsync(); SetExitCode(0); return; }
            if (e.Args.Contains("--lan-files-check")) { await LanFileChecks.RunAsync(); SetExitCode(0); return; }
            if (e.Args.Contains("--connection-center-window-check")) { await ConnectionCenterUiChecks.RunAsync(viewModel); SetExitCode(0); return; }
            if (e.Args.Contains("--shell-directory-check"))
            {
                await ShellDirectoryChecks.RunAsync(viewModel);
                SetExitCode(0); return;
            }
            if (e.Args.Contains("--remote-tabs-check"))
            {
                await RemoteTabsChecks.RunAsync(viewModel, remoteDebug);
                SetExitCode(0); return;
            }
            if (e.Args.Contains("--remote-empty-check"))
            {
                await RemoteTabsChecks.RunEmptyStateAsync(viewModel);
                SetExitCode(0); return;
            }
            if (e.Args.Contains("--crypto-check"))
            {
                await CryptoChecks.RunAsync();
                SetExitCode(0); return;
            }
            if (e.Args.Contains("--app-settings-check"))
            {
                await AppSettingsChecks.RunAsync();
                SetExitCode(0); return;
            }
            if (e.Args.Contains("--hosts-check"))
            {
                await HostsChecks.RunAsync();
                SetExitCode(0); return;
            }
            if (e.Args.Contains("--configuration-transfer-window-check"))
            {
                var transferWindow = new ConfigurationTransferWindow(viewModel) { Owner = null };
                var transferClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                transferWindow.Closed += (_, _) => transferClosed.TrySetResult();
                transferWindow.Loaded += (_, _) => transferWindow.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var width = Math.Max(1, (int)Math.Ceiling(transferWindow.ActualWidth));
                    var height = Math.Max(1, (int)Math.Ceiling(transferWindow.ActualHeight));
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(transferWindow);
                    var screenshotPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "embeddesk-configuration-transfer-window.png");
                    using (var stream = new System.IO.FileStream(screenshotPath, System.IO.FileMode.Create, System.IO.FileAccess.Write))
                        new PngBitmapEncoder { Frames = { BitmapFrame.Create(bitmap) } }.Save(stream);
                    Console.WriteLine($"UI SCREENSHOT {screenshotPath}");
                    transferWindow.Close();
                }));
                transferWindow.Show();
                await transferClosed.Task;
                return;
            }
            if (e.Args.Contains("--configuration-import-preview-check"))
            {
                var previewWindow = new ConfigurationImportPreviewWindow(new ImportedConfiguration(
                    [new RemoteConnectionProfile { Name = "示例 SSH", Protocol = RemoteProtocol.Ssh, Host = "HOST", Port = 22, Username = "user" }],
                    [new NetworkConnectionPreset { Name = "示例 TCP", Protocol = TransportProtocol.Tcp, Mode = TransportMode.Client, RemoteHost = "HOST", RemotePort = 9000 }],
                    [new SerialBridgeClientPreset { Name = "示例串口", Host = "HOST", Port = 9100 }])) { Owner = null };
                var previewClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                previewWindow.Closed += (_, _) => previewClosed.TrySetResult();
                previewWindow.Loaded += (_, _) => previewWindow.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var width = Math.Max(1, (int)Math.Ceiling(previewWindow.ActualWidth));
                    var height = Math.Max(1, (int)Math.Ceiling(previewWindow.ActualHeight));
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(previewWindow);
                    var screenshotPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "embeddesk-configuration-import-preview.png");
                    using (var stream = new System.IO.FileStream(screenshotPath, System.IO.FileMode.Create, System.IO.FileAccess.Write))
                        new PngBitmapEncoder { Frames = { BitmapFrame.Create(bitmap) } }.Save(stream);
                    Console.WriteLine($"UI SCREENSHOT {screenshotPath}");
                    previewWindow.Close();
                }));
                previewWindow.Show();
                await previewClosed.Task;
                return;
            }
            if (e.Args.Contains("--local-info-window-check"))
            {
                var localInfoWindow = new LocalInfoWindow();
                var localInfoClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                localInfoWindow.Closed += (_, _) => localInfoClosed.TrySetResult();
                localInfoWindow.Loaded += (_, _) => localInfoWindow.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var width = Math.Max(1, (int)Math.Ceiling(localInfoWindow.ActualWidth));
                    var height = Math.Max(1, (int)Math.Ceiling(localInfoWindow.ActualHeight));
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(localInfoWindow);
                    var screenshotPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "embeddesk-local-info-window.png");
                    using (var stream = new System.IO.FileStream(screenshotPath, System.IO.FileMode.Create, System.IO.FileAccess.Write))
                        new PngBitmapEncoder { Frames = { BitmapFrame.Create(bitmap) } }.Save(stream);
                    Console.WriteLine($"UI SCREENSHOT {screenshotPath}");
                    localInfoWindow.Close();
                }));
                localInfoWindow.Show();
                await localInfoClosed.Task;
                return;
            }
            if (e.Args.Contains("--hosts-window-check"))
            {
                await HostsChecks.RunWindowAsync();
                SetExitCode(0); return;
            }
            if (e.Args.Contains("--preset-layout-check"))
            {
                new NetworkConnectionWindow(TransportProtocol.Tcp, viewModel).ShowDialog();
                new BridgeWindow { DataContext = viewModel }.ShowDialog();
                SetExitCode(0); return;
            }
            if (e.Args.Contains("--collaboration-protocol-check"))
            {
                await CollaborationProtocolChecks.RunAsync();
                SetExitCode(0); return;
            }
            if (e.Args.Contains("--ssh-protocol-check") && int.TryParse(Environment.GetEnvironmentVariable("EDBG_SSH_PORT"), out var protocolPort))
            {
                await SshIntegrationChecks.RunProtocolAsync(protocolPort);
                SetExitCode(0);
                return;
            }
            if (e.Args.Contains("--network-tools-check"))
            {
                await NetworkToolsChecks.RunAsync();
                SetExitCode(0); return;
            }
            if (e.Args.Contains("--iperf3-check"))
            {
                await Iperf3Checks.RunAsync();
                await Iperf3UiChecks.RunAsync();
                SetExitCode(0); return;
            }
            if (e.Args.Length == 3 && e.Args[0] == "--iperf3-peer-check")
            {
                await Iperf3Checks.CheckPeerAsync(e.Args[1], int.Parse(e.Args[2]));
                SetExitCode(0); return;
            }
            if (e.Args.Contains("--dns-query-check"))
            {
                await DnsQueryChecks.RunAsync();
                SetExitCode(0); return;
            }
            if (e.Args.Contains("--utility-tools-check"))
            {
                await UtilityToolsChecks.RunAsync(viewModel);
                SetExitCode(0); return;
            }
            if (e.Args.Contains("--serial-bridge-check"))
            {
                await SerialBridgeChecks.RunAsync();
                SetExitCode(0); return;
            }
            if (e.Args.Contains("--firmware-check"))
            {
                await FirmwareChecks.RunAsync();
                await Stm32Checks.RunAsync();
                await StcChecks.RunAsync();
                await Ch347Checks.RunAsync();
                await EspChecks.RunAsync();
                await RecordingMediaChecks.RunAsync(viewModel);
                SetExitCode(0); return;
            }
            if (e.Args.Contains("--ch347-check"))
            {
                await Ch347Checks.RunAsync();
                SetExitCode(0); return;
            }
            if (e.Args.Contains("--esp-check"))
            {
                await EspChecks.RunAsync();
                SetExitCode(0); return;
            }
            if (e.Args.Contains("--recording-media-check"))
            {
                await RecordingMediaChecks.RunAsync(viewModel);
                SetExitCode(0); return;
            }
            if (e.Args.Contains("--live-doh-check"))
            {
                await DnsQueryChecks.RunLiveDohAsync();
                SetExitCode(0); return;
            }
            if (e.Args.Contains("--live-dns-check"))
            {
                await DnsQueryChecks.RunLiveAsync();
                SetExitCode(0); return;
            }
            if (e.Args.Contains("--live-nat-check"))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                var report = await new NatDetector().DetectAsync(NatDetector.DefaultServers,
                    new Progress<NatObservation>(o => Console.WriteLine($"{o.Server} | {o.MappedEndpoint} | {o.Detail}")), timeout.Token);
                Console.WriteLine(report.Label + " · " + report.Evidence);
                foreach (var exit in report.Exits)
                    Console.WriteLine($"{exit.Heading} | {exit.Label}\n{exit.MappingDetails}\n{exit.NodesText}\n{exit.LocalDetails}\n{exit.Evidence}");
                SetExitCode(0); return;
            }
            if (e.Args.Contains("--connection-layout-check"))
            {
                await ConnectionLayoutChecks.RunAsync(viewModel.RemoteDebug!);
                SetExitCode(0); return;
            }
            if (e.Args.Contains("--s3-check"))
            {
                await viewModel.InitializeAsync();
                var s3Window = new MainWindow(viewModel) { Left = 10, Top = 10, Width = 1240, Height = 820 };
                s3Window.Show();
                await Task.Delay(200);
                Find<TabControl>(s3Window).SelectedIndex = 2;
                await Task.Delay(100);
                await S3Checks.RunAsync(s3Window, Find<RemoteDebugWorkspace>(s3Window), viewModel.RemoteDebug!, settingsDirectory);
                s3Window.Close();
                SetExitCode(0); return;
            }
            if (e.Args.Contains("--serial-tabs-check"))
            {
                await SerialTabsChecks.RunAsync();
                SetExitCode(0); return;
            }
            if (e.Args.Contains("--performance-check"))
            {
                await PerformanceChecks.RunAsync(viewModel);
                SetExitCode(0);
                return;
            }
            if (e.Args.Contains("--network-quality-check"))
            {
                await NetworkQualityChecks.RunAsync();
                SetExitCode(0);
                return;
            }
            if (e.Args.Contains("--desktop-network-check"))
            {
                await DesktopNetworkChecks.RunAsync(viewModel);
                SetExitCode(0);
                return;
            }
            if (e.Args.Contains("--packaging-check"))
            {
                await SingleFilePackagingChecks.RunAsync(viewModel);
                SetExitCode(0);
                return;
            }
            await TerminalRegressionChecks.RunAsync();
            if (int.TryParse(Environment.GetEnvironmentVariable("EDBG_SSH_PORT"), out var sshPort)) await SshIntegrationChecks.RunAsync(sshPort);
            await using (var delayedTerminal = new RemoteWorkspaceViewModel(remoteDebug))
            {
                await delayedTerminal.ResizeTerminalAsync(91, 23);
                await delayedTerminal.ConnectCommand.ExecuteAsync(null);
                Require(remoteDebug.LastResize == (91, 23), "Terminal size measured before SSH connects is applied after connection");
                await delayedTerminal.DisconnectCommand.ExecuteAsync(null);
                await delayedTerminal.ResizeTerminalAsync(83, 19);
                await delayedTerminal.ConnectCommand.ExecuteAsync(null);
                Require(remoteDebug.LastResize == (83, 19), "Reconnected SSH session receives the current visible dimensions");
            }
            await ConnectionLayoutChecks.RunAsync(viewModel.RemoteDebug!);
            await SerialTabsChecks.RunAsync();
            await viewModel.InitializeAsync();
            var window = new MainWindow(viewModel) { Left = 10, Top = 10, Width = 1240, Height = 820 };
            ((TabControl)window.FindName("MainWorkspaceTabs")).SelectedIndex = 1;
            window.Show();
            await Task.Delay(200);
            var workspace = Find<SerialWorkspace>(window);
            var mainTabs = Find<TabControl>(window);
            mainTabs.SelectedIndex = 2;
            await Task.Delay(100);
            var remoteWorkspace = Find<RemoteDebugWorkspace>(window);
            var platformIcons = new HostPlatformIconConverter();
            foreach (var platform in new[] { "ubuntu", "debian", "windows", "alpine", "arch", "fedora", "centos", "rhel", "rocky", "opensuse-leap", "darwin", "freebsd", "linux", "unknown" })
                Require(platformIcons.Convert(platform, typeof(ImageSource), null!, System.Globalization.CultureInfo.InvariantCulture) is BitmapSource { PixelWidth: 96, PixelHeight: 96 }, $"Packaged system logo loads: {platform}");
            Require(viewModel.RemoteDebug is { HasSessions: false, Profiles.Count: > 0 }, "Remote manager starts with local profiles and no sessions");
            Require(remoteWorkspace.DataContext == viewModel.RemoteDebug, "Remote manager is bound to the main view model");
            await viewModel.RemoteDebug!.OpenProfileCommand.ExecuteAsync(null);
            Require(!viewModel.RemoteDebug.IsManagerView, "Opening a profile switches to the session workspace");
            await Task.Delay(100);
            window.UpdateLayout();
            var activeRemoteSession = viewModel.RemoteDebug.SelectedSession!;
            var refreshTask = activeRemoteSession.RefreshDirectoryCommand.ExecuteAsync(null);
            Require(activeRemoteSession.IsBusy && activeRemoteSession.IsWorkspaceReady, "Directory refresh keeps the terminal workspace visible");
            await refreshTask;
            var showSessionPaneButton = (Button)remoteWorkspace.FindName("SessionPaneToggle");
            viewModel.RemoteDebug.ToggleSessionPaneCommand.Execute(null);
            window.UpdateLayout();
            Require(!viewModel.RemoteDebug.IsSessionPaneVisible && showSessionPaneButton.Visibility == Visibility.Visible, "Collapsed session list exposes a restore button");
            viewModel.RemoteDebug.ToggleSessionPaneCommand.Execute(null);
            window.UpdateLayout();
            Require(viewModel.RemoteDebug.IsSessionPaneVisible && showSessionPaneButton.IsVisible && Equals(showSessionPaneButton.ToolTip, "收起会话列表"), "The same edge toggle restores and collapses the session list");
            var activeRemoteWorkspace = Find<RemoteWorkspace>(window);
            var showDirectoryButton = (Button)activeRemoteWorkspace.FindName("DirectoryRestore");
            window.UpdateLayout();
            Require(!activeRemoteSession.IsDirectoryPaneExpanded && showDirectoryButton.Visibility == Visibility.Visible, "New sessions start with the directory collapsed and the center restore button visible");
            activeRemoteSession.ToggleDirectoryPaneCommand.Execute(null);
            window.UpdateLayout();
            Require(activeRemoteSession.IsDirectoryPaneExpanded && showDirectoryButton.Visibility == Visibility.Visible && Equals(showDirectoryButton.ToolTip, "收起目录"), "The same center toggle remains available to collapse the reopened pane");
            await FullscreenChecks.RunAsync(window, remoteWorkspace, viewModel.RemoteDebug, activeRemoteWorkspace, remoteDebug);
            await FileWorkspaceChecks.RunAsync(window, remoteWorkspace, viewModel.RemoteDebug, activeRemoteWorkspace, remoteDebug);
            await viewModel.RemoteDebug.OpenProfileCommand.ExecuteAsync(null);
            Require(viewModel.RemoteDebug.Sessions.Count == 2, "Remote manager opens multiple independent sessions");
            Require(viewModel.RemoteDebug.Sessions.All(session => session.IsConnected), "Opened sessions expose connected state");
            viewModel.RemoteDebug.ShowManagerCommand.Execute(null);
            Require(viewModel.RemoteDebug.IsManagerView, "Connection manager remains available after opening a session");
            viewModel.RemoteDebug.SelectSessionCommand.Execute(viewModel.RemoteDebug.Sessions[1]);
            Require(ReferenceEquals(viewModel.RemoteDebug.SelectedSession, viewModel.RemoteDebug.Sessions[1]) && !viewModel.RemoteDebug.IsManagerView, "Session tabs switch back to an existing session");
            await viewModel.RemoteDebug.CloseSessionCommand.ExecuteAsync(viewModel.RemoteDebug.Sessions[0]);
            Require(viewModel.RemoteDebug.Sessions.Count == 1, "Remote manager closes one session without closing the other");
            await viewModel.RemoteDebug.CloseSessionCommand.ExecuteAsync(viewModel.RemoteDebug.Sessions[0]);
            Require(viewModel.RemoteDebug.IsManagerView, "Closing the last session returns to the connection manager");
            var failedProfile = new RemoteConnectionProfile { Name = "失败连接", Host = "fail", Port = 22, Username = "root" };
            viewModel.RemoteDebug.Profiles.Add(failedProfile);
            viewModel.RemoteDebug.SelectedProfile = failedProfile;
            await viewModel.RemoteDebug.OpenProfileCommand.ExecuteAsync(null);
            Require(viewModel.RemoteDebug.Sessions.Count == 0 && viewModel.RemoteDebug.IsManagerView && viewModel.RemoteDebug.Status.StartsWith("连接失败"), "Failed connections stay in the connection manager");
            viewModel.RemoteDebug.DeleteProfileCommand.Execute(null);
            viewModel.RemoteDebug.DeleteProfileCommand.Execute(null);
            Require(viewModel.RemoteDebug.Profiles.Count == 0 && viewModel.RemoteDebug.SelectedProfile is null, "The last profile can be removed safely");
            await using (var reloaded = new RemoteDebugWorkspaceViewModel(remoteDebug, settingsDirectory))
                Require(reloaded.Profiles.Count == 0, "An intentionally empty profile list stays empty after reload");
            mainTabs.SelectedIndex = 1;
            await Task.Delay(100);
            var selector = (ComboBox)workspace.FindName("PortSelector");
            Console.WriteLine($"BINDING selected={viewModel.SelectedSerialSession!.SelectedSerialPort?.PortName ?? "NULL"}, selector={((SerialPortRowViewModel?)selector.SelectedItem)?.PortName ?? "NULL"}, workspaceData={workspace.DataContext?.GetType().Name}");
            Require(viewModel.SelectedSerialSession!.SelectedSerialPort is not null, "Default serial port is selected");
            Require(viewModel.SelectedSerialSession!.OpenSelectedSerialCommand.CanExecute(null), "Connect command is enabled");
            viewModel.SelectedSerialSession!.SelectedSerialPort!.Parity = System.IO.Ports.Parity.Even;
            viewModel.SelectedSerialSession!.SelectedSerialPort.DataBits = 7;
            viewModel.SelectedSerialSession!.SelectedSerialPort.Handshake = System.IO.Ports.Handshake.RequestToSend;
            await viewModel.SelectedSerialSession!.OpenSelectedSerialCommand.ExecuteAsync(null);
            Require(serial.LastSettings is { DataBits: 7, Parity: System.IO.Ports.Parity.Even, Handshake: System.IO.Ports.Handshake.RequestToSend }, "Serial options reach channel");
            viewModel.SelectedSerialSession!.SerialSendText = "AT";
            viewModel.SelectedSerialSession!.SerialAppendCrLf = true;
            await viewModel.SelectedSerialSession!.SendSelectedSerialCommand.ExecuteAsync(null);
            Require(Encoding.UTF8.GetString(serial.Writes.Last()) == "AT\r\n", "CRLF send");
            viewModel.SelectedSerialSession!.SerialSendAsHex = true;
            viewModel.SelectedSerialSession!.SerialSendText = "00 ff 10";
            await viewModel.SelectedSerialSession!.SendSelectedSerialCommand.ExecuteAsync(null);
            Require(serial.Writes.Last().SequenceEqual(new byte[] { 0, 255, 16 }), "HEX send");
            var writeCount = serial.Writes.Count;
            viewModel.SelectedSerialSession!.SerialSendText = "xyz";
            await viewModel.SelectedSerialSession!.SendSelectedSerialCommand.ExecuteAsync(null);
            Require(serial.Writes.Count == writeCount, "Invalid HEX does not send");
            viewModel.SelectedSerialSession!.SerialSendAsHex = false;
            viewModel.SelectedSerialSession!.SerialAppendCrLf = false;
            viewModel.SelectedSerialSession!.SerialSendText = "ping";
            viewModel.SelectedSerialSession!.SerialIntervalMs = "50";
            viewModel.SelectedSerialSession!.SerialLoopEnabled = true;
            await Task.Delay(180);
            viewModel.SelectedSerialSession!.StopSerialJobCommand.Execute(null);
            await Task.Delay(80);
            writeCount = serial.Writes.Count;
            await Task.Delay(100);
            Require(serial.Writes.Count == writeCount && !viewModel.SelectedSerialSession!.SerialLoopEnabled, "Loop stops");
            viewModel.SelectedSerialSession!.SerialSendQueue.Add(new SerialSendQueueItem { Payload = "first" });
            viewModel.SelectedSerialSession!.SerialSendQueue.Add(new SerialSendQueueItem { Payload = "skip", Enabled = false });
            viewModel.SelectedSerialSession!.SerialSendQueue.Add(new SerialSendQueueItem { Payload = "second" });
            await viewModel.SelectedSerialSession!.RunQueueCommand.ExecuteAsync(null);
            Require(Encoding.UTF8.GetString(serial.Writes[^2]) == "first" && Encoding.UTF8.GetString(serial.Writes[^1]) == "second", "Queue order and enabled state");
            viewModel.SelectedSerialSession!.QueueRepeat = true;
            var queueTask = viewModel.SelectedSerialSession!.RunQueueCommand.ExecuteAsync(null);
            await Task.Delay(90);
            viewModel.SelectedSerialSession!.StopSerialJobCommand.Execute(null);
            await queueTask;
            Require(!viewModel.SelectedSerialSession!.RunQueueCommand.IsRunning, "Queue cancellation");
            var buffer = viewModel.SelectedSerialSession!.Terminal;
            await viewModel.ScanSerialPortsCommand.ExecuteAsync(null);
            Require(viewModel.SelectedSerialSession!.SelectedSerialPort?.IsOpen == true && viewModel.SelectedSerialSession!.Terminal == buffer, "Refresh preserves open port and buffer");
            await viewModel.SelectedSerialSession!.CloseSelectedSerialCommand.ExecuteAsync(null);
            viewModel.SelectedSerialSession!.SelectedSerialPort!.DataBits = 8;
            viewModel.SelectedSerialSession!.SelectedSerialPort.Parity = System.IO.Ports.Parity.None;
            viewModel.SelectedSerialSession!.SelectedSerialPort.Handshake = System.IO.Ports.Handshake.None;
            viewModel.SelectedSerialSession!.SerialSendQueue.Clear();
            viewModel.SelectedSerialSession!.SerialSendQueue.Add(new SerialSendQueueItem { Payload = "AT+STATUS" });
            viewModel.SelectedSerialSession!.SerialSendQueue.Add(new SerialSendQueueItem { Payload = "AT+VERSION" });
            viewModel.SelectedSerialSession!.SerialSendQueue.Add(new SerialSendQueueItem { Payload = "AT+RESET", Enabled = false });
            buffer.ClearCommand.Execute(null);
            buffer.Add(DateTimeOffset.Now, true, Encoding.UTF8.GetBytes("Booting device...\r\nFirmware v2.4.0\r\nReady.\r\n"));
            viewModel.SelectedSerialSession!.SerialSendText = "AT+STATUS";
            Console.WriteLine("READY. Commands: normal, small, tools, shell, bridge, client, network, quit");
            Window? auxiliary = null;
            while (true)
            {
                var command = await Task.Run(Console.ReadLine);
                if (command is null or "quit") break;
                auxiliary?.Close();
                auxiliary = null;
                switch (command)
                {
                    case "normal": window.Width = 1240; window.Height = 820; viewModel.SelectedSerialSession!.ShellMode = false; break;
                    case "small": window.Width = 1080; window.Height = 680; viewModel.SelectedSerialSession!.ShellMode = false; break;
                    case "tools": window.Width = 1080; window.Height = 680; viewModel.SelectedSerialSession!.ShellMode = false; break;
                    case "shell":
                        viewModel.SelectedSerialSession!.ShellMode = true;
                        buffer.ClearCommand.Execute(null);
                        buffer.Add(DateTimeOffset.Now, true, Encoding.UTF8.GetBytes("\u001b[32mEmbedded Linux 6.6\u001b[0m\r\nroot@device:~# uname -a\r\nLinux device 6.6.21 aarch64 GNU/Linux\r\nroot@device:~# "));
                        await Task.Delay(2000);
                        var shell = (ShellTerminal)workspace.FindName("Shell");
                        var web = (Microsoft.Web.WebView2.Wpf.WebView2)shell.FindName("Browser");
                        Require(web.CoreWebView2 is not null, "Shell WebView initialized");
                        var screen = await web.ExecuteScriptAsync("terminal.buffer.active.getLine(0).translateToString()");
                        Require(screen.Contains("Embedded Linux"), "xterm renders ANSI data");
                        Console.WriteLine($"SHELL pixels={web.ActualWidth}x{web.ActualHeight}, firstLine={screen}");
                        break;
                    case "bridge": viewModel.BridgeMode = "服务端"; auxiliary = new BridgeWindow { DataContext = viewModel, Owner = window }; auxiliary.Show(); break;
                    case "client": viewModel.BridgeMode = "客户端"; auxiliary = new BridgeWindow { DataContext = viewModel, Owner = window }; auxiliary.Show(); break;
                    case "network":
                        Find<TabControl>(window).SelectedIndex = 1;
                        break;
                }
                await Task.Delay(100);
                Console.WriteLine($"STAGE {command}");
            }
            window.Close();
            Console.WriteLine("PASS serial workflow and UI smoke checks");
            SetExitCode(0);
        }
        catch (Exception ex) { System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "embeddesk-current-failure.txt"), ex.ToString()); Console.Error.WriteLine(ex); SetExitCode(1); }
        finally
        {
            try
            {
                await viewModel.DisposeAsync();
                foreach (var file in new[] { "remote-profiles.json", "ssh-keys.json", "app-settings.json", "app-settings.json.tmp", "hosts-manager.json", "hosts-manager.json.lock" })
                {
                    var path = System.IO.Path.Combine(settingsDirectory, file);
                    if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                }
                if (System.IO.Directory.Exists(settingsDirectory)) System.IO.Directory.Delete(settingsDirectory);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); SetExitCode(1); }
            finally { Shutdown(_exitCode); }
        }
    }

    private static void Require(bool value, string description)
    {
        if (!value) throw new InvalidOperationException(description);
        Console.WriteLine($"PASS {description}");
    }

    private static T Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            try { return Find<T>(VisualTreeHelper.GetChild(root, i)); }
            catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException($"No {typeof(T).Name}");
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in FindAll<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}

internal sealed class FakeSerialService : ISerialPortService
{
    public List<byte[]> Writes { get; } = new();
    public SerialPortSettings? LastSettings { get; private set; }
    public Task<IReadOnlyList<SerialPortDescriptor>> ScanAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SerialPortDescriptor>>([new("COM3", "USB Serial Device (COM3)", "USB Serial Device", "STMicroelectronics"), new("COM7", "CP210x USB to UART (COM7)", "CP210x USB to UART Bridge", "Silicon Labs")]);
    public ISerialPortChannel CreateChannel(SerialPortSettings settings) { LastSettings = settings; return new FakeChannel(settings, Writes); }
    private sealed class FakeChannel(SerialPortSettings settings, List<byte[]> writes) : ISerialPortChannel
    {
        public string Id => settings.PortName;
        public SerialPortSettings Settings => settings;
        public bool IsOpen { get; private set; }
        public event EventHandler<ReadOnlyMemory<byte>>? DataReceived;
        public Task OpenAsync(CancellationToken cancellationToken = default) { IsOpen = true; return Task.CompletedTask; }
        public Task CloseAsync(CancellationToken cancellationToken = default) { IsOpen = false; return Task.CompletedTask; }
        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) { writes.Add(data.ToArray()); DataReceived?.Invoke(this, data); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { IsOpen = false; return ValueTask.CompletedTask; }
    }
}

internal sealed class FakeBridge : ISerialBridgeService
{
    public Task<string> StartAsync(SerialBridgeProfile profile, CancellationToken cancellationToken = default) => Task.FromResult("test-bridge");
    public Task StopAsync(string bridgeId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

#pragma warning disable CS0067
internal sealed class FakeRemoteDebugService : IRemoteDebugService
{
    public (int Columns, int Rows)? LastResize { get; private set; }
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
    public List<string> PreviewDownloads { get; } = [];
    public bool FailUpload { get; set; }
    public int PreviewDelayMs { get; set; }
    public IRemoteDebugSession CreateSession(RemoteConnectionProfile profile) => new FakeRemoteDebugSession(profile, this);

    private sealed class FakeRemoteDebugSession(RemoteConnectionProfile profile, FakeRemoteDebugService owner) : IRemoteDebugSession
    {
        private RemoteSessionState _state = RemoteSessionState.Disconnected;
        public RemoteConnectionProfile Profile { get; } = profile;
        public RemoteSessionState State => _state;
        public string? HomeDirectory => null;
        public string? HostPlatform => "linux";
        public bool SupportsDirectoryManagement => true;
        public event EventHandler<string>? TextReceived;
        public event EventHandler<string>? ConnectionLogReceived;
        public event EventHandler<RemotePerformanceSnapshot>? PerformanceChanged;
        public event EventHandler<RemoteSessionState>? StateChanged;
        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (Profile.Host == "fail") throw new InvalidOperationException("测试连接失败");
            _state = RemoteSessionState.Connected; StateChanged?.Invoke(this, _state); return Task.CompletedTask;
        }
        public Task DisconnectAsync(CancellationToken cancellationToken = default) { _state = RemoteSessionState.Disconnected; StateChanged?.Invoke(this, _state); return Task.CompletedTask; }
        public Task SendAsync(string text, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
        {
            owner.LastResize = (columns, rows);
            return Task.CompletedTask;
        }
        public async Task<IReadOnlyList<RemoteDirectoryEntry>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
        {
            await Task.Delay(80, cancellationToken);
            return owner.Files.Where(pair => System.IO.Path.GetDirectoryName(pair.Key)?.Replace('\\', '/') == path.TrimEnd('/') || path == "/" && pair.Key.LastIndexOf('/') == 0)
                .Select(pair => new RemoteDirectoryEntry(pair.Key.Split('/').Last(), pair.Key, false, pair.Value.Length, DateTimeOffset.UtcNow, "rw-r--r--")).ToArray();
        }
        public Task DownloadFileAsync(string remotePath, string localPath, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task DownloadPreviewAsync(string remotePath, string localPath, long maxBytes, CancellationToken cancellationToken = default)
        {
            owner.PreviewDownloads.Add(remotePath);
            await Task.Delay(owner.PreviewDelayMs, cancellationToken);
            var bytes = owner.Files[remotePath];
            if (bytes.Length > maxBytes) throw new System.IO.IOException("Preview limit exceeded");
            await System.IO.File.WriteAllBytesAsync(localPath, bytes, cancellationToken);
        }
        public async Task UploadFileAsync(string localPath, string remotePath, CancellationToken cancellationToken = default)
        {
            if (owner.FailUpload) throw new System.IO.IOException("Upload failed");
            owner.Files[remotePath] = await System.IO.File.ReadAllBytesAsync(localPath, cancellationToken);
        }
        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(string path, bool directory, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
#pragma warning restore CS0067
