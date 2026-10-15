using System.IO;
using System.IO.Ports;
using System.Text;
using System.Windows;
using EmbedDesk.App;
using EmbedDesk.App.ViewModels;
using EmbedDesk.Infrastructure.Bridging;
using EmbedDesk.Infrastructure.Networking;
using EmbedDesk.Core.Models;
using EmbedDesk.Protocols;

namespace EmbedDesk.App.SmokeTests;

internal static class AppSettingsChecks
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "EmbedDesk-AppSettings-Smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using (var store = new AppSettingsStore(directory))
            {
                store.Update(settings =>
                {
                    settings.WindowWidth = 1180;
                    settings.WindowHeight = 820;
                    settings.WindowLeft = 24;
                    settings.WindowTop = 42;
                    settings.WindowState = System.Windows.WindowState.Maximized;
                    settings.LastWorkspaceIndex = 2;
                    settings.RemoteSessionPaneWidth = 204;
                    settings.RemoteSessionPaneExpanded = false;
                    settings.SerialPorts["COM7"] = new SerialPortPreference
                    {
                        BaudRate = 230400,
                        CustomBaudRate = 0,
                        DataBits = 7,
                        Parity = Parity.Even,
                        StopBits = StopBits.Two,
                        Handshake = Handshake.None
                    };
                });
                store.SaveNetworkPreset(new NetworkConnectionPreset
                {
                    Name = "本机 TCP",
                    Protocol = TransportProtocol.Tcp,
                    Mode = TransportMode.Server,
                    LocalHost = "127.0.0.1",
                    LocalPort = 19000,
                    ClientLocalAddress = "127.0.0.1",
                    RemoteHost = "127.0.0.1",
                    RemotePort = 19000,
                    AutoReconnect = false
                });
                store.SaveSerialBridgePreset(new SerialBridgeClientPreset { Name = "实验室串口", Host = "HOST", Port = 19100 });
                store.SaveStcpTunnel(new StcpTunnelProfile
                {
                    Name = "实验 STCP",
                    Role = StcpTunnelRole.Visitor,
                    ServerAddress = "HOST",
                    ServerPort = 7000,
                    ServiceName = "ssh",
                    LocalPort = 22022,
                    TargetHost = "127.0.0.1",
                    TargetPort = 22,
                    MaxBytesPerSecond = 128 * 1024,
                    CredentialTarget = string.Empty
                });
                store.Update(settings => settings.NetworkDisplayPreferences = new NetworkDisplayPreferences { HexDisplay = true, ShowTime = false, ShowSent = true, AutoScroll = false });
                store.SaveNetworkProbePreferences(new NetworkProbePreferences
                {
                    Host = "HOST", Mode = NetworkProbeToolMode.Tcping, AddressFamily = EmbedDesk.Core.Abstractions.NetworkProbeAddressFamily.IPv4,
                    LocalAddress = "127.0.0.1", TcpPort = 23, PacketSize = 64, Count = 5, IntervalMilliseconds = 750,
                    MaxHops = 20, TimeoutMilliseconds = 900, ResolveNames = false
                });
                store.SaveCidrCalculatorPreferences(new CidrCalculatorPreferences { Input = "10.20.0.0/16" });
                store.SaveDnsQueryPreferences(new DnsQueryPreferences
                {
                    Query = "HOST", RecordType = "AAAA", QueryClassIndex = 1, TransportIndex = 2,
                    DnsServer = "127.0.0.1", DohEndpoint = "https://dns.example.test/query", Port = 5353,
                    TimeoutMilliseconds = 2500, Retries = 3, RecursionDesired = false
                });
                store.SaveTcpPortScanPreferences(new TcpPortScanPreferences
                {
                    Host = "HOST", Ports = "22,80-82", AddressFamilyIndex = 2, TimeoutMilliseconds = 1500, Concurrency = 32
                });
                store.SaveTlsDiagnosticPreferences(new TlsDiagnosticPreferences
                {
                    Host = "HOST", Port = 8443, ServerName = "service.example.test", TimeoutMilliseconds = 5000, CheckRevocation = true
                });
                store.SaveIperf3ToolPreferences(new Iperf3ToolPreferences
                {
                    ModeIndex = 1, Host = "127.0.0.1", Port = 5202, ProtocolIndex = 1, DirectionIndex = 1,
                    DurationSeconds = 15, BlockSizeBytes = 1200, Rate = "500", RateUnitIndex = 4
                });
                store.SaveNetworkDiscoveryPreferences(new NetworkDiscoveryPreferences { LocalAddress = "127.0.0.1", Concurrency = 48 });
                store.SaveNatDetectionPreferences(new NatDetectionPreferences { Servers = "stun.example.test:3478", ShowServerSettings = true });
                store.SaveUpnpDiscoveryPreferences(new UpnpDiscoveryPreferences { SearchTypeIndex = 2 });
                store.SaveNow();
            }

            var path = Path.Combine(directory, "app-settings.json");
            var json = await File.ReadAllTextAsync(path);
            Require(json.Contains("\"SchemaVersion\": 3", StringComparison.Ordinal), "settings document includes a schema version");
            Require(!json.Contains("\"Password\":", StringComparison.OrdinalIgnoreCase)
                && !json.Contains("secret", StringComparison.OrdinalIgnoreCase)
                && !json.Contains("payload", StringComparison.OrdinalIgnoreCase)
                && !json.Contains("privateKey", StringComparison.OrdinalIgnoreCase)
                && !json.Contains("hmacKey", StringComparison.OrdinalIgnoreCase), "settings document contains no credentials, keys, or send payload");
            Require(!File.Exists(path + ".tmp"), "atomic settings write leaves no temporary file");

            var transferBytes = ConfigurationTransferService.Export(
                [new RemoteConnectionProfile
                {
                    Name = "S3 测试",
                    Protocol = RemoteProtocol.S3,
                    Host = string.Empty,
                    Port = 443,
                    S3Region = "us-east-1",
                    S3Bucket = "firmware",
                    Password = "super-secret",
                    S3AccessKeyId = "AKIA-EXAMPLE",
                    S3SecretAccessKey = "secret-key"
                }],
                [new NetworkConnectionPreset
                {
                    Name = "服务端空远端",
                    Protocol = TransportProtocol.Tcp,
                    Mode = TransportMode.Server,
                    LocalHost = "0.0.0.0",
                    LocalPort = 19200,
                    RemoteHost = string.Empty,
                    RemotePort = 19200
                }],
                [new SerialBridgeClientPreset { Name = "桥接测试", Host = "HOST", Port = 19201 }],
                [new StcpTunnelProfile
                {
                    Name = "导出 STCP", Role = StcpTunnelRole.Service, ServerAddress = "HOST", ServerPort = 7000,
                    ServiceName = "ssh", TargetHost = "127.0.0.1", TargetPort = 22, MaxBytesPerSecond = 64 * 1024,
                    CredentialTarget = "EmbedDesk/STCP/example"
                }]);
                
            var transferJson = Encoding.UTF8.GetString(transferBytes);
            Require(!transferJson.Contains("super-secret", StringComparison.Ordinal)
                && !transferJson.Contains("AKIA-EXAMPLE", StringComparison.Ordinal)
                && !transferJson.Contains("secret-key", StringComparison.Ordinal)
                && !transferJson.Contains("EmbedDesk/STCP/example", StringComparison.Ordinal)
                && !transferJson.Contains("CredentialTarget", StringComparison.Ordinal),
                "configuration export excludes passwords, S3 credentials, and STCP credential indexes");
            var imported = ConfigurationTransferService.Import(transferBytes);
            Require(imported.RemoteProfiles.Single().Password.Length == 0
                && imported.RemoteProfiles.Single().S3AccessKeyId.Length == 0
                && imported.NetworkPresets.Single().RemoteHost.Length == 0
                && imported.StcpTunnels.Single().ServiceName == "ssh"
                && imported.StcpTunnels.Single().CredentialTarget.Length == 0,
                "configuration import validates S3, server, and STCP presets while retaining no secrets");
            RequireThrows<InvalidDataException>(() => ConfigurationTransferService.Import(Encoding.UTF8.GetBytes("{}")),
                "invalid configuration format is rejected");

            using var reopened = new AppSettingsStore(directory);
            var saved = reopened.Snapshot();
            Require(saved.WindowWidth == 1180 && saved.WindowHeight == 820 && saved.LastWorkspaceIndex == 2,
                "window bounds and main workspace selection round-trip");
            Require(saved.WindowState == System.Windows.WindowState.Maximized && saved.WindowLeft == 24 && saved.WindowTop == 42,
                "window state and position round-trip");
            Require(saved.RemoteSessionPaneWidth == 204 && !saved.RemoteSessionPaneExpanded,
                "remote session pane preferences round-trip");
            Require(saved.SerialPorts.TryGetValue("com7", out var serial) && serial.BaudRate == 230400
                && serial.DataBits == 7 && serial.Parity == Parity.Even && serial.StopBits == StopBits.Two,
                "per-port serial communication parameters round-trip case-insensitively");
            Require(saved.NetworkPresets.Single().Name == "本机 TCP" && saved.NetworkPresets[0].LocalPort == 19000
                && saved.NetworkPresets[0].ClientLocalAddress == "127.0.0.1",
                "network connection presets round-trip with a client source address and no session data");
            Require(saved.SerialBridgePresets.Single().Host == "HOST" && saved.SerialBridgePresets[0].Port == 19100,
                "network serial client presets round-trip");
            Require(saved.StcpTunnels.Single() is { Name: "实验 STCP", Role: StcpTunnelRole.Visitor, ServiceName: "ssh", LocalPort: 22022 }
                && saved.StcpTunnels[0].CredentialTarget.Length == 0,
                "STCP profiles round-trip without shared keys");
            var networkPreset = saved.NetworkPresets.Single();
            reopened.SaveNetworkPreset(networkPreset with { LocalPort = 19001, Name = "本机 TCP 更新" });
            Require(reopened.GetNetworkPresets().Count == 1 && reopened.GetNetworkPresets()[0].Id == networkPreset.Id
                && reopened.GetNetworkPresets()[0].LocalPort == 19001, "network presets update in place by stable ID");
            reopened.DeleteNetworkPreset(networkPreset.Id);
            Require(reopened.GetNetworkPresets().Count == 0, "network presets can be deleted by stable ID");
            var bridgePreset = saved.SerialBridgePresets.Single();
            reopened.SaveSerialBridgePreset(bridgePreset with { Port = 19101, Name = "实验室串口更新" });
            Require(reopened.GetSerialBridgePresets().Count == 1 && reopened.GetSerialBridgePresets()[0].Id == bridgePreset.Id
                && reopened.GetSerialBridgePresets()[0].Port == 19101, "network serial presets update in place by stable ID");
            reopened.DeleteSerialBridgePreset(bridgePreset.Id);
            Require(reopened.GetSerialBridgePresets().Count == 0, "network serial presets can be deleted by stable ID");
            var mergeResult = reopened.MergeNetworkPresets(imported.NetworkPresets);
            mergeResult = mergeResult.Add(reopened.MergeSerialBridgePresets(imported.SerialBridgePresets))
                .Add(reopened.MergeStcpTunnels(imported.StcpTunnels));
            var duplicateResult = reopened.MergeNetworkPresets(imported.NetworkPresets);
            var duplicateStcpResult = reopened.MergeStcpTunnels(imported.StcpTunnels);
            Require(mergeResult.Added == 3 && mergeResult.Skipped == 0 && duplicateResult.Added == 0 && duplicateResult.Skipped == 1
                && duplicateStcpResult.Added == 0 && duplicateStcpResult.Skipped == 1,
                "configuration import merges presets and keeps duplicate names local");
            Require(saved.NetworkDisplayPreferences.HexDisplay && !saved.NetworkDisplayPreferences.ShowTime && !saved.NetworkDisplayPreferences.AutoScroll,
                "network display preferences round-trip");
            Require(reopened.GetNetworkProbePreferences() is { Host: "HOST", Mode: NetworkProbeToolMode.Tcping, TcpPort: 23, LocalAddress: "127.0.0.1", Count: 5, ResolveNames: false },
                "Ping / TCPing recent options round-trip without result history");
            var networkTools = reopened.GetNetworkToolPreferences();
            Require(networkTools.CidrCalculator.Input == "10.20.0.0/16"
                && networkTools.DnsQuery is { Query: "HOST", RecordType: "AAAA", TransportIndex: 2, DnsServer: "127.0.0.1", Port: 5353, Retries: 3, RecursionDesired: false },
                "CIDR and DNS query inputs round-trip without response history");
            Require(networkTools.TcpPortScan is { Host: "HOST", Ports: "22,80-82", AddressFamilyIndex: 2, Concurrency: 32 }
                && networkTools.TlsDiagnostic is { Host: "HOST", Port: 8443, ServerName: "service.example.test", CheckRevocation: true },
                "port scan and TLS diagnostic options round-trip without results or certificate data");
            Require(networkTools.Iperf3 is { ModeIndex: 1, Port: 5202, ProtocolIndex: 1, DirectionIndex: 1, DurationSeconds: 15, BlockSizeBytes: 1200, Rate: "500", RateUnitIndex: 4 }
                && networkTools.NetworkDiscovery is { LocalAddress: "127.0.0.1", Concurrency: 48 },
                "iperf3 and network discovery inputs round-trip without traffic or device results");
            Require(networkTools.NatDetection is { Servers: "stun.example.test:3478", ShowServerSettings: true }
                && networkTools.UpnpDiscovery.SearchTypeIndex == 2,
                "NAT and UPnP query options round-trip without discovery results");
            NetworkToolPreferencesChecks.Run();
            var migration = Path.Combine(directory, "schema-one"); Directory.CreateDirectory(migration);
            await File.WriteAllTextAsync(Path.Combine(migration, "app-settings.json"), "{\"SchemaVersion\":1,\"LastWorkspaceIndex\":1,\"WindowWidth\":1200}");
            using (var migrated = new AppSettingsStore(migration)) Require(migrated.Snapshot().LastWorkspaceIndex == 2 && migrated.Snapshot().WindowWidth == 1200, "schema 1 navigation migrates without losing existing preferences");
            using (var newOptions = new AppSettingsStore(Path.Combine(directory, "connection-options")))
            {
                newOptions.Update(s => { s.ConnectionPreferences = new() { ServerUrl = "https://relay.example.test", CloseAction = MainWindowCloseAction.Tray }; s.SharedServices = [new() { Name = "board", Kind = EmbedDesk.Infrastructure.Connections.SharedServiceKind.Tcp, TargetHost = "BOARD_HOST" }]; s.PortMappingRules = [new(Guid.NewGuid().ToString("N"), "ABCDEF012345", Guid.NewGuid().ToString("N"), "board", 22022)]; }); newOptions.SaveNow();
                using var restored = new AppSettingsStore(Path.GetDirectoryName(newOptions.FilePath)); Require(restored.Snapshot().SharedServices.Count == 1 && restored.Snapshot().PortMappingRules.Count == 1 && restored.Snapshot().ConnectionPreferences.CloseAction == MainWindowCloseAction.Tray, "connection options, shared targets and inactive mapping rules persist");
                var migratedRelay = restored.Snapshot().ConnectionPreferences;
                var udpService = new EmbedDesk.Infrastructure.Connections.SharedDeviceService { Name = "UDP board", Kind = EmbedDesk.Infrastructure.Connections.SharedServiceKind.Udp, TargetPort = 9000 };
                restored.Update(s =>
                {
                    s.SharedServices.Add(udpService);
                    s.SharedServices.Add(new() { Name = "serial board", Kind = EmbedDesk.Infrastructure.Connections.SharedServiceKind.Serial, PortName = "COM3", Description = "transient scan metadata" });
                    s.PortMappingRules.Add(new(Guid.NewGuid().ToString("N"), "ABCDEF012345", udpService.Id, udpService.Name, 22023, udpService.Kind));
                });
                restored.SaveNow();
                using (var udpReload = new AppSettingsStore(Path.GetDirectoryName(newOptions.FilePath)))
                {
                    Require(udpReload.Snapshot().SharedServices.Count == 3 && udpReload.Snapshot().PortMappingRules.Last().Kind == EmbedDesk.Infrastructure.Connections.SharedServiceKind.Udp,
                        "UDP targets and protocol-specific mapping rules round-trip");
                    Require(udpReload.Snapshot().PortMappingRules.First().Kind == EmbedDesk.Infrastructure.Connections.SharedServiceKind.Tcp
                        && !(await File.ReadAllTextAsync(restored.FilePath)).Contains("transient scan metadata", StringComparison.Ordinal),
                        "existing TCP rules retain their protocol and scanned serial metadata never enters settings JSON");
                }
                Require(migratedRelay.RelayServers.Single().Url == "https://relay.example.test"
                    && migratedRelay.DefaultRelayServerId == migratedRelay.RelayServers[0].Id, "legacy relay URL migrates to a stable default server entry");
                var virtualId = Guid.NewGuid().ToString("N");
                restored.Update(s =>
                {
                    s.PortMappingRules.Add(new(virtualId, "ABCDEF012345", Guid.NewGuid().ToString("N"), "remote serial", 0,
                        EmbedDesk.Infrastructure.Connections.SharedServiceKind.Serial, "COM101", "CNCB0"));
                    s.PortMappingRules.Add(new(Guid.NewGuid().ToString("N"), "ABCDEF012345", Guid.NewGuid().ToString("N"), "invalid serial", 0,
                        EmbedDesk.Infrastructure.Connections.SharedServiceKind.Serial, "COM101", "../device"));
                });
                restored.SaveNow();
                using (var virtualReload = new AppSettingsStore(Path.GetDirectoryName(newOptions.FilePath)))
                {
                    Require(virtualReload.Snapshot().PortMappingRules.Count == 3
                        && virtualReload.Snapshot().PortMappingRules.Single(r => r.Id == virtualId) is { VirtualPort: "COM101", BridgePort: "CNCB0" },
                        "virtual COM rules retain their pair across restart, reject invalid names and stay inactive");
                }
                var fallback = new RelayServerProfile { Name = "备用中继", Url = "http://127.0.0.1:7081" };
                restored.Update(s => s.ConnectionPreferences = s.ConnectionPreferences with
                { RelayServers = s.ConnectionPreferences.RelayServers.Append(fallback).ToList(), DefaultRelayServerId = fallback.Id });
                restored.SaveNow();
                var isolatedSnapshot = restored.Snapshot(); isolatedSnapshot.ConnectionPreferences.RelayServers.Clear();
                Require(restored.Snapshot().ConnectionPreferences.RelayServers.Count == 2, "relay list snapshots do not mutate the settings store");
                using var relayReload = new AppSettingsStore(Path.GetDirectoryName(newOptions.FilePath));
                var relays = relayReload.Snapshot().ConnectionPreferences;
                Require(relays.RelayServers.Count == 2 && relays.DefaultRelayServerId == fallback.Id
                    && relays.RelayServers.Single(p => p.Id == fallback.Id).Name == "备用中继", "relay server list and default selection round-trip");
            }
            var relayMigration = Path.Combine(directory, "relay-migration"); Directory.CreateDirectory(relayMigration);
            await File.WriteAllTextAsync(Path.Combine(relayMigration, "app-settings.json"),
                "{\"SchemaVersion\":2,\"ConnectionPreferences\":{\"ServerUrl\":\"http://127.0.0.1:7080\",\"AutoConnect\":false}}");
            using (var legacyRelay = new AppSettingsStore(relayMigration))
                Require(legacyRelay.Snapshot().ConnectionPreferences.RelayServers.Single().Url == "http://127.0.0.1:7080",
                    "old JSON relay configuration migrates even when the retired auto-connect option was disabled");

            var unknownSchemaDirectory = Path.Combine(directory, "future-schema");
            Directory.CreateDirectory(unknownSchemaDirectory);
            var unknownSchemaPath = Path.Combine(unknownSchemaDirectory, "app-settings.json");
            await File.WriteAllTextAsync(unknownSchemaPath, "{\"SchemaVersion\":99}");
            using var newerSchema = new AppSettingsStore(unknownSchemaDirectory);
            Require(newerSchema.Snapshot().LastWorkspaceIndex == 0,
                "unknown settings schema falls back to safe defaults");

            await using var remoteSerial = new RemoteSerialClient();
            await using var viewModel = new MainWindowViewModel(new TcpUdpTransportService(), new FakeSerialService(),
                new FakeBridge(), remoteSerial, new FakeRemoteDebugService(), directory);
            await viewModel.InitializeAsync();
            viewModel.AddSerialSession();
            var remoteMerge = viewModel.RemoteDebug!.MergeProfiles(imported.RemoteProfiles);
            Require(remoteMerge.Added == 1 && viewModel.RemoteDebug.Profiles.Any(profile => profile.Name == "S3 测试" && profile.S3AccessKeyId.Length == 0),
                "remote connection import merges metadata without credentials");
            Require(viewModel.SelectedSerialSession!.SerialPorts.Single(port => port.PortName == "COM7").BaudRate == 230400,
                "saved per-port serial parameters are applied when devices are enumerated");
            Require(viewModel.RemoteDebug!.IsSessionPaneExpanded == false,
                "remote workspace restores its collapsed sidebar state");
            viewModel.RemoteDebug.IsSessionPaneExpanded = true;
            Require(viewModel.RemoteDebug.SessionPaneWidth.Value == 204,
                "remote workspace restores the saved sidebar width");
            var window = new MainWindow(viewModel);
            var tabs = (System.Windows.Controls.TabControl)window.FindName("MainWorkspaceTabs");
            var workArea = SystemParameters.WorkArea;
            var expectedWidth = Math.Clamp(1180, window.MinWidth, Math.Max(window.MinWidth, workArea.Width - 32));
            var expectedHeight = Math.Clamp(820, window.MinHeight, Math.Max(window.MinHeight, workArea.Height - 32));
            Require(tabs.SelectedIndex == 2 && Math.Abs(window.Width - expectedWidth) < 1 && Math.Abs(window.Height - expectedHeight) < 1,
                "main window restores its workspace and dimensions");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"App settings check failed: {message}");
        Console.WriteLine($"PASS {message}");
    }

    private static void RequireThrows<TException>(Action action, string message) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            Console.WriteLine($"PASS {message}");
            return;
        }

        throw new InvalidOperationException($"App settings check failed: {message}");
    }
}
