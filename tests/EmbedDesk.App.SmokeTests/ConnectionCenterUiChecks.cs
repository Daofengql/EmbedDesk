using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EmbedDesk.App.ViewModels;
using EmbedDesk.App.Controls;
using EmbedDesk.Infrastructure.Connections;
using EmbedDesk.Infrastructure.Serial;

namespace EmbedDesk.App.SmokeTests;
internal static class ConnectionCenterUiChecks
{
    public static async Task RunAsync(MainWindowViewModel model)
    {
        await using var relayIsolation = new RelayTrafficIsolation(model);
        await model.InitializeAsync();
        await model.Connections.StopAsync();
        model.Connections.SaveService(new() { Name = "开发板 SSH", Kind = SharedServiceKind.Tcp, TargetHost = "BOARD_HOST", TargetPort = 22 });
        model.Connections.SaveService(new() { Name = "开发板 UDP", Kind = SharedServiceKind.Udp, TargetHost = "BOARD_HOST", TargetPort = 9000 });
        model.Connections.SaveService(new() { Name = "板卡控制台", Kind = SharedServiceKind.Serial, PortName = "COM3" });
        var serialDevice = model.SerialPorts.Single(p => p.PortName == "COM3");
        DeviceConnectionsChecks.Require(model.Connections.LocalSerialServices.Single().Description == $"{serialDevice.Description} · {serialDevice.Manufacturer}",
            "mapping serial devices display the same description and manufacturer as serial debugging");
        foreach (var service in model.Connections.LocalServices)
            model.Connections.RemoteServices.Add(new("example", "ABCDEF012345", service));
        model.Connections.Peers.Add(new("example", "ABCDEF012345", false, "已连接", "局域网直连", 1024, 2048));
        model.Connections.Approvals.Add(new("example-approval", "ABCDEF543210"));
        var main = new MainWindow(model); main.Show();
        var center = Find<ConnectionCenterWorkspace>(main);
        var identity = (TextBlock)center.FindName("DeviceIdDisplay");
        var password = (TextBlock)center.FindName("PasswordDisplay");
        var status = (TextBlock)center.FindName("DeviceStatusDisplay");
        // Only the screenshot uses synthetic values; session state remains the real view model.
        identity.SetCurrentValue(TextBlock.TextProperty, "ABCDEF012345");
        password.SetCurrentValue(TextBlock.TextProperty, "DEMO12345678");
        status.SetCurrentValue(TextBlock.TextProperty, "在线 · 可用带宽 4.096 Gbps");
        model.Connections.RelayLatency.Apply(new(true, 48, ""));
        await CaptureAsync(main, "center-default", 1340, 940);
        await CaptureAsync(main, "center-minimum", 1080, 680);
        var mappingCard = (Border)center.FindName("MappingControlCard");
        DeviceConnectionsChecks.Require(identity.FontSize >= 30 && mappingCard.ActualHeight >= 200,
            "large identity and bottom mapping controls fit the minimum main window");
        var latency = (TextBlock)center.FindName("RelayLatencyDisplay");
        foreach (var (milliseconds, expectedColor, screenshot) in new[]
        {
            (48, "#FF4CC9B0", "center-latency-green-minimum"),
            (150, "#FFF0C36D", "center-latency-yellow-minimum"),
            (320, "#FFFF8585", "center-latency-red-minimum")
        })
        {
            model.Connections.RelayLatency.Apply(new(true, milliseconds, ""));
            await CaptureAsync(main, screenshot, 1080, 680);
            DeviceConnectionsChecks.Require(latency.Text == milliseconds + " ms" && ((SolidColorBrush)latency.Foreground).Color.ToString() == expectedColor,
                "relay header renders latency and the matching quality color");
            CheckInsideWindow(main, latency);
        }
        model.Connections.RelayLatency.Apply(new(false, 0, "HTTP 503"));
        await CaptureAsync(main, "center-latency-unavailable-minimum", 1080, 680);
        DeviceConnectionsChecks.Require(latency.Text == "不可用" && ((SolidColorBrush)latency.Foreground).Color.ToString() == "#FF9AA5AF",
            "unavailable latency uses a neutral color instead of a stale good reading");
        model.Connections.Peers.Clear();
        model.Connections.Peers.Add(new("example", "ABCDEF012345", true, "等待本机授权", "协商中", 0, 0));
        await CaptureAsync(main, "center-receiving-role-minimum", 1080, 680);
        DeviceConnectionsChecks.Require(!((Button)center.FindName("ConnectDeviceButton")).IsEnabled
            && model.Connections.ConnectionHint.Contains("被连接期间", StringComparison.Ordinal),
            "receiving connections disable initiation and explain the role restriction");
        model.Connections.Peers.Clear();
        await CaptureAsync(main, "center-local-mapping-minimum", 1080, 680);
        model.Connections.Peers.Add(new("example", "ABCDEF012345", false, "已连接", "局域网直连", 1024, 2048));
        await CaptureAsync(main, "center-mappings-minimum", 1080, 680);
        identity.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
        password.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
        status.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
        var session = model.SelectedSerialSession!; session.SelectedSerialPort = session.SerialPorts.Single(p => p.PortName == "COM7"); await session.OpenSelectedSerialCommand.ExecuteAsync(null);
        DeviceConnectionsChecks.Require(session.IsOpen, "local serial opens outside reservations");
        main.HideToTray(); DeviceConnectionsChecks.Require(!main.IsVisible && session.IsOpen, "tray hide keeps live serial");
        main.RestoreFromTray(); DeviceConnectionsChecks.Require(main.IsVisible && session.IsOpen && model.SelectedSerialSession == session, "tray restore preserves existing session"); await session.CloseAsync();
        using (var store = new AppSettingsStore(Path.Combine(Path.GetTempPath(), "EmbedDesk-Schema-Test", Guid.NewGuid().ToString("N"))))
        { store.Update(s => { s.ConnectionPreferences = s.ConnectionPreferences with { CloseAction = MainWindowCloseAction.Tray }; }); store.SaveNow(); using var reload = new AppSettingsStore(Path.GetDirectoryName(store.FilePath)); DeviceConnectionsChecks.Require(reload.Snapshot().ConnectionPreferences.CloseAction == MainWindowCloseAction.Tray, "close preference persists"); Directory.Delete(Path.GetDirectoryName(store.FilePath)!, true); }
        var profiles = new[] { new RelayServerProfile { Name = "本机中继", Url = "http://127.0.0.1:7080" }, new RelayServerProfile { Name = "备用中继示例", Url = "http://127.0.0.1:1" } };
        using var credentials = new RelayCredentialCleanup(model.Connections, profiles);
        model.AppSettings.Update(s => s.ConnectionPreferences = s.ConnectionPreferences with { RelayServers = profiles.ToList(), DefaultRelayServerId = profiles[0].Id });
        model.Connections.SaveSecret("admission", "test-ui-legacy-token");
        var settings = new SettingsWindow(model); settings.Show(); await settings.ProbeTask;
        var tabs = Find<TabControl>(settings);
        await CaptureAsync(settings, "settings-relays-default", 820, 680);
        CheckRelayEditorAlignment(settings);
        for (var index = 0; index < tabs.Items.Count; index++) { tabs.SelectedIndex = index; await CaptureAsync(settings, "settings-" + index, 680, 570); if (index == 0) CheckRelayEditorAlignment(settings); }
        DeviceConnectionsChecks.Require(settings.FindName("AutoConnect") is null && settings.RelayStatuses.Count == 2 + RelayServerCatalog.BuiltInServers.Count
            && settings.RelayStatuses.Any(s => s.IsDefault), "relay settings list replaces the retired auto-connect control and marks the default server");
        DeviceConnectionsChecks.Require(settings.FindName("AdmissionKey") is null && settings.FindName("ClearAdmission") is null,
            "connection options no longer contain a global admission key");
        tabs.SelectedIndex = 0;
        var builtInStatus = settings.RelayStatuses.First(status => status.Profile.BuiltIn);
        var relayList = (ListBox)settings.FindName("RelayServers");
        relayList.SelectedItem = builtInStatus; relayList.ScrollIntoView(builtInStatus);
        await CaptureAsync(settings, "settings-built-in-minimum", 680, 570);
        DeviceConnectionsChecks.Require(!builtInStatus.CanDelete && !((Button)settings.FindName("EditRelayButton")).IsEnabled
            && RelayConnectionInfo.Parse(settings.ExportRelayCode(builtInStatus)).Url == builtInStatus.Profile.Url,
            "built-in free relay remains visible and immutable while its connection code can be copied");
        settings.ImportRelayCode(RelayConnectionInfo.Encode("导入的中继", profiles[0].Url, "test-ui-replacement-token"));
        DeviceConnectionsChecks.Require(settings.RelayStatuses.Count == 2 + RelayServerCatalog.BuiltInServers.Count && ((Button)settings.FindName("AddRelayButton")).Content.Equals("更新"), "importing an existing address stages a replacement without adding duplicates");
        await CaptureAsync(settings, "settings-relay-import-minimum", 680, 570);
        ((Button)settings.FindName("AddRelayButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        DeviceConnectionsChecks.Require(RelayConnectionInfo.Parse(settings.ExportRelayCode(settings.RelayStatuses[0])).AdmissionToken == "test-ui-replacement-token", "copyable relay code contains the staged per-relay token");
        ((TextBox)settings.FindName("RelayNameInput")).Text = "取消时不保存";
        ((TextBox)settings.FindName("RelayUrlInput")).Text = "http://127.0.0.1:7082";
        ((Button)settings.FindName("AddRelayButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((ListBox)settings.FindName("RelayServers")).SelectedItem = settings.RelayStatuses.Single(status => status.Profile.Url == "http://127.0.0.1:7082");
        ((Button)settings.FindName("SetDefaultRelayButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        settings.Close();
        DeviceConnectionsChecks.Require(model.Connections.Preferences.RelayServers.Count == 2 + RelayServerCatalog.BuiltInServers.Count
            && model.Connections.Preferences.DefaultRelayServerId == profiles[0].Id
            && model.Connections.ReadRelayAdmissionToken(profiles[0]) == "test-ui-legacy-token", "canceling settings discards tokens, added servers and default selection");
        var saveSettings = new SettingsWindow(model);
        saveSettings.ImportRelayCode(RelayConnectionInfo.Encode(profiles[1].Name, profiles[1].Url, "test-ui-token-b"));
        ((Button)saveSettings.FindName("AddRelayButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((ListBox)saveSettings.FindName("RelayServers")).SelectedItem = saveSettings.RelayStatuses[1];
        ((Button)saveSettings.FindName("SetDefaultRelayButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        saveSettings.Loaded += (_, _) => saveSettings.Dispatcher.BeginInvoke(new Action(() =>
            ((Button)saveSettings.FindName("SaveSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
        DeviceConnectionsChecks.Require(saveSettings.ShowDialog() == true
            && model.Connections.Preferences.DefaultRelayServerId == profiles[1].Id
            && model.Connections.Preferences.RelayTokensMigrated
            && model.Connections.ReadSecret("admission").Length == 0
            && model.Connections.ReadRelayAdmissionToken(profiles[0]) == "test-ui-legacy-token"
            && model.Connections.ReadRelayAdmissionToken(profiles[1]) == "test-ui-token-b", "save migrates existing tokens and commits the selected relay's imported token");
        var clearSettings = new SettingsWindow(model);
        clearSettings.ImportRelayCode(RelayConnectionInfo.Encode(profiles[1].Name, profiles[1].Url, ""));
        ((Button)clearSettings.FindName("AddRelayButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        clearSettings.Loaded += (_, _) => clearSettings.Dispatcher.BeginInvoke(new Action(() =>
            ((Button)clearSettings.FindName("SaveSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
        DeviceConnectionsChecks.Require(clearSettings.ShowDialog() == true && model.Connections.ReadRelayAdmissionToken(profiles[1]).Length == 0
            && model.Connections.ReadRelayAdmissionToken(profiles[0]) == "test-ui-legacy-token", "clearing one relay token preserves the other and does not reuse the global key");
        foreach (var profile in profiles) model.Connections.SaveRelayAdmissionToken(profile, string.Empty);
        model.Connections.SaveSecret("admission", string.Empty);
        model.Connections.Peers.Clear();
        var services = new DeviceServicesWindow(model.Connections); services.Show(); await CaptureAsync(services, "services-minimum", 600, 420);
        model.Connections.Peers.Add(new("example", "ABCDEF012345", false, "已连接", "局域网直连", 1024, 2048));
        await CaptureAsync(services, "mappings-minimum", 600, 420); services.Close();
        // Settings and offline-node notifications may refresh the view model; install
        // the synthetic remote catalog immediately before exercising its UI pickers.
        model.Connections.RemoteServices.Clear();
        foreach (var service in model.Connections.LocalServices)
            model.Connections.RemoteServices.Add(new("example", "ABCDEF012345", service));
        await CheckMappingUiAsync(main, center, model.Connections);
        var editor = new SharedServiceEditorWindow(model.Connections.LocalServices.First()); editor.Show(); await CaptureAsync(editor, "service-editor", 480, 440); editor.Close();
        var serialEditor = new SharedServiceEditorWindow(model.Connections.LocalSerialServices.Single(), devices: model.Connections.LocalSerialDevices);
        serialEditor.Show(); await CaptureAsync(serialEditor, "serial-service-editor", 480, 440);
        DeviceConnectionsChecks.Require(!((ComboBox)serialEditor.FindName("ProtocolBox")).IsVisible && ((ComboBox)serialEditor.FindName("PortName")).ItemTemplate is not null
            && ((TextBlock)serialEditor.FindName("PortDescription")).Text.Contains(serialDevice.Description, StringComparison.Ordinal),
            "serial publishing locks its type and shows scanned device metadata");
        serialEditor.Close();
        var udpEditor = new SharedServiceEditorWindow(model.Connections.LocalNetworkServices.Single(s => s.Kind == SharedServiceKind.Udp));
        udpEditor.Show(); await CaptureAsync(udpEditor, "udp-service-editor", 480, 440);
        var protocol = (ComboBox)udpEditor.FindName("ProtocolBox");
        DeviceConnectionsChecks.Require(protocol.IsVisible && protocol.Items.Count == 2 && protocol.SelectedIndex == 1,
            "network service editor offers TCP/UDP and restores the saved UDP selection");
        udpEditor.Close();
        var addEditor = new SharedServiceEditorWindow();
        DeviceConnectionsChecks.Require(((ComboBox)addEditor.FindName("ProtocolBox")).SelectedIndex == 0,
            "the unified network service entry defaults to TCP");
        addEditor.Loaded += (_, _) => addEditor.Dispatcher.BeginInvoke(new Action(() =>
        {
            ((TextBox)addEditor.FindName("ServiceName")).Text = "协议切换测试";
            ((ComboBox)addEditor.FindName("ProtocolBox")).SelectedIndex = 1;
            ((Button)addEditor.FindName("SaveServiceButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }));
        DeviceConnectionsChecks.Require(addEditor.ShowDialog() == true && addEditor.Result?.Kind == SharedServiceKind.Udp,
            "the unified entry saves the selected UDP protocol");
        var editEditor = new SharedServiceEditorWindow(addEditor.Result);
        editEditor.Loaded += (_, _) => editEditor.Dispatcher.BeginInvoke(new Action(() =>
        {
            ((ComboBox)editEditor.FindName("ProtocolBox")).SelectedIndex = 0;
            ((Button)editEditor.FindName("SaveServiceButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }));
        DeviceConnectionsChecks.Require(editEditor.ShowDialog() == true && editEditor.Result?.Kind == SharedServiceKind.Tcp
            && editEditor.Result.Id == addEditor.Result!.Id, "editing switches UDP to TCP while preserving service identity");
        var choice = new CloseChoiceWindow(); choice.Show(); await CaptureAsync(choice, "close-choice", 460, 240); choice.Close();
        var files = new LanFileShareWindow(); files.Show(); await CaptureAsync(files, "files-server-minimum", 700, 500); ((TabControl)files.FindName("Modes")).SelectedIndex = 1; await CaptureAsync(files, "files-client-minimum", 700, 500); await files.ShutdownAsync(); files.Close();
        main.Close();
        Console.WriteLine("PASS connection center/settings/tray/LAN windows screenshots");
    }

    private static async Task CheckMappingUiAsync(MainWindow main, ConnectionCenterWorkspace center, ConnectionCenterViewModel model)
    {
        var controls = (DeviceServicesControl)center.FindName("ServicesControl");
        var serialRules = (ListBox)controls.FindName("SerialMappingRulesList");
        var networkRules = (ListBox)controls.FindName("NetworkMappingRulesList");
        var serial = model.RemoteServices.Single(service => service.Service.Kind == SharedServiceKind.Serial);
        var tcp = model.RemoteServices.Single(service => service.Service.Kind == SharedServiceKind.Tcp);
        var udp = model.RemoteServices.Single(service => service.Service.Kind == SharedServiceKind.Udp);
        var serialRule = new PortMappingRuleViewModel(new(Guid.NewGuid().ToString("N"), serial.DeviceId, serial.Service.Id, serial.Service.Name, 0,
            SharedServiceKind.Serial, "COM101", "CNCB0", serial.TargetEndpoint));
        var networkRule = new PortMappingRuleViewModel(new(Guid.NewGuid().ToString("N"), tcp.DeviceId, tcp.Service.Id, tcp.Service.Name, 22022,
            RemoteEndpoint: tcp.TargetEndpoint));
        model.MappingRules.Add(serialRule); model.MappingRules.Add(networkRule);
        try
        {
            serialRules.SelectedItem = serialRule; networkRules.SelectedItem = networkRule;
            await CaptureAsync(main, "center-separated-mappings-minimum", 1080, 680);
            DeviceConnectionsChecks.Require(controls.FindName("RemoteServicesList") is null && controls.FindName("LocalPort") is null
                && serialRules.Items.Cast<PortMappingRuleViewModel>().All(rule => rule.Rule.Kind == SharedServiceKind.Serial)
                && networkRules.Items.Cast<PortMappingRuleViewModel>().All(rule => rule.Rule.Kind is SharedServiceKind.Tcp or SharedServiceKind.Udp)
                && ReferenceEquals(serialRules.SelectedItem, serialRule) && ReferenceEquals(networkRules.SelectedItem, networkRule)
                && ((Button)controls.FindName("EnableSerialMappingButton")).IsEnabled && ((Button)controls.FindName("EnableNetworkMappingButton")).IsEnabled,
                "serial and network mappings have independent filtered lists and retain their own selected rule");
            CheckInsideWindow(main, controls, (Button)controls.FindName("AddSerialMappingButton"), (Button)controls.FindName("AddNetworkMappingButton"));
            var serialEntry = OpenMappingDialog<RemoteSerialUseWindow>((Button)controls.FindName("AddSerialMappingButton"));
            var portEntry = OpenMappingDialog<RemotePortMappingWindow>((Button)controls.FindName("AddNetworkMappingButton"));
            DeviceConnectionsChecks.Require(((ComboBox)serialEntry.FindName("ServiceBox")).Items.Cast<DeviceRemoteService>().All(service => service.Service.Kind == SharedServiceKind.Serial)
                && ((ComboBox)portEntry.FindName("ServiceBox")).Items.Cast<DeviceRemoteService>().All(service => service.Service.Kind is SharedServiceKind.Tcp or SharedServiceKind.Udp),
                "the two add buttons open only their corresponding resource picker");

            var published = model.RemoteServices.ToArray(); model.RemoteServices.Clear();
            try
            {
                var emptySerial = OpenMappingDialog<RemoteSerialUseWindow>((Button)controls.FindName("AddSerialMappingButton"));
                var emptyNetwork = OpenMappingDialog<RemotePortMappingWindow>((Button)controls.FindName("AddNetworkMappingButton"));
                DeviceConnectionsChecks.Require(((ComboBox)emptySerial.FindName("ServiceBox")).Items.Count == 0 && !((Button)emptySerial.FindName("UseSerialButton")).IsEnabled
                    && ((ComboBox)emptyNetwork.FindName("ServiceBox")).Items.Count == 0 && !((Button)emptyNetwork.FindName("SavePortButton")).IsEnabled
                    && ((TextBlock)emptySerial.FindName("EmptyServicesHint")).Visibility == Visibility.Visible
                    && ((TextBlock)emptyNetwork.FindName("EmptyServicesHint")).Visibility == Visibility.Visible,
                    "both add dialogs remain accessible and explain the empty state when no matching resources are published");
            }
            finally { foreach (var service in published) model.RemoteServices.Add(service); }

            var useSerial = new RemoteSerialUseWindow(serial, []); useSerial.Show();
            await CaptureAsync(useSerial, "remote-serial-app", 620, 640);
            DeviceConnectionsChecks.Require(((ComboBox)useSerial.FindName("UsageMode")).SelectedIndex == 0 && ((Button)useSerial.FindName("UseSerialButton")).IsEnabled,
                "remote serial defaults to application use without requiring a driver");
            ((ComboBox)useSerial.FindName("UsageMode")).SelectedIndex = 1;
            await CaptureAsync(useSerial, "remote-serial-no-driver-minimum", 560, 580);
            DeviceConnectionsChecks.Require(!((Button)useSerial.FindName("UseSerialButton")).IsEnabled,
                "system COM mapping clearly blocks when no driver-backed pair is available");
            CheckInsideWindow(useSerial, (ComboBox)useSerial.FindName("ServiceBox"), (ComboBox)useSerial.FindName("PairBox"), (Button)useSerial.FindName("UseSerialButton"));
            ((ComboBox)useSerial.FindName("UsageMode")).SelectedIndex = 0;
            DeviceConnectionsChecks.Require(((Button)useSerial.FindName("UseSerialButton")).IsEnabled, "application serial remains usable when virtual COM is unavailable");
            useSerial.Close();

            var secondSerial = new DeviceRemoteService("example", "ABCDEF012345", serial.Service with { Id = Guid.NewGuid().ToString("N"), Name = "第二个板卡串口", PortName = "COM12" });
            var choices = new[] { serial, secondSerial, tcp, udp };
            var pairs = new[] { new VirtualSerialPair("COM101", "CNCB0", "CNCB0", string.Empty) };
            var configuredSerial = new RemoteSerialUseWindow(choices, pairs, (service, pair) => model.MapVirtualSerialAsync(service, pair!), () => model.Error);
            configuredSerial.Show(); ((ComboBox)configuredSerial.FindName("ServiceBox")).SelectedItem = secondSerial;
            ((ComboBox)configuredSerial.FindName("UsageMode")).SelectedIndex = 1;
            await CaptureAsync(configuredSerial, "remote-serial-target-minimum", 560, 580);
            DeviceConnectionsChecks.Require(((ComboBox)configuredSerial.FindName("ServiceBox")).Items.Count == 2 && configuredSerial.SelectedService == secondSerial
                && configuredSerial.UseVirtualPort && configuredSerial.SelectedPair?.PublicPort == "COM101"
                && ((TextBlock)configuredSerial.FindName("MappingPreview")).Text.Contains("COM12", StringComparison.Ordinal)
                && ((Button)configuredSerial.FindName("UseSerialButton")).IsEnabled, "serial target picker fixes the explicit remote COM and previews the real driver-backed pair");
            model.SelectedRemoteService = tcp;
            ((Button)configuredSerial.FindName("UseSerialButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await configuredSerial.SubmissionTask;
            DeviceConnectionsChecks.Require(configuredSerial.IsVisible && configuredSerial.SelectedService == secondSerial
                && ((TextBlock)configuredSerial.FindName("ErrorText")).Text == model.Error && model.Error.Contains("已离线", StringComparison.Ordinal),
                "virtual COM submission uses its selected target and retains the dialog with the real failure reason");
            await CaptureAsync(configuredSerial, "remote-serial-failed-minimum", 560, 580); configuredSerial.Close();

            var unknown = new DeviceRemoteService("example", "ABCDEF012345", tcp.Service with { Id = Guid.NewGuid().ToString("N"), Name = "旧版目标", TargetHost = string.Empty, TargetPort = 0 });
            var ports = new RemotePortMappingWindow(choices.Append(unknown), model); ports.Show();
            var localPort = (TextBox)ports.FindName("LocalPort"); var savePort = (Button)ports.FindName("SavePortButton");
            DeviceConnectionsChecks.Require(localPort.Text.Length == 0 && !savePort.IsEnabled && ((ComboBox)ports.FindName("ServiceBox")).Items.Count == 3,
                "port mapping starts with an explicit local port and offers only published network targets");
            foreach (var invalid in new[] { "0", "65536", "1x" })
            { localPort.Text = invalid; DeviceConnectionsChecks.Require(!savePort.IsEnabled && ((TextBlock)ports.FindName("ValidationText")).Text.Length > 0, "invalid mapping ports are rejected inline"); }
            localPort.Text = "22022"; ((ComboBox)ports.FindName("ServiceBox")).SelectedItem = udp;
            await CaptureAsync(ports, "remote-port-default", 620, 550);
            await CaptureAsync(ports, "remote-port-udp-minimum", 540, 510);
            DeviceConnectionsChecks.Require(savePort.IsEnabled && ports.SelectedService == udp && ((TextBlock)ports.FindName("ProtocolLabel")).Text.Contains("UDP", StringComparison.Ordinal)
                && ((TextBlock)ports.FindName("MappingPreview")).Text.Contains("127.0.0.1:22022", StringComparison.Ordinal)
                && ((TextBlock)ports.FindName("MappingPreview")).Text.Contains(udp.TargetEndpoint, StringComparison.Ordinal),
                "network preview shows the fixed selected protocol and both real mapping endpoints");
            CheckInsideWindow(ports, (ComboBox)ports.FindName("ServiceBox"), localPort, savePort, (TextBlock)ports.FindName("MappingPreview"));
            savePort.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await ports.SubmissionTask;
            DeviceConnectionsChecks.Require(ports.IsVisible && ports.SelectedService == udp && localPort.Text == "22022"
                && ((TextBlock)ports.FindName("ErrorText")).Text == model.Error && model.Error.Contains("已离线", StringComparison.Ordinal),
                "a failed network submission preserves the selected UDP target, local port and real model error");
            await CaptureAsync(ports, "remote-port-failed-minimum", 540, 510);
            ((ComboBox)ports.FindName("ServiceBox")).SelectedItem = unknown;
            DeviceConnectionsChecks.Require(((TextBlock)ports.FindName("MappingPreview")).Text.Contains("未提供目标地址", StringComparison.Ordinal)
                && !((TextBlock)ports.FindName("MappingPreview")).Text.Contains(":0", StringComparison.Ordinal), "legacy peers display an unknown target instead of inventing port zero");
            await CaptureAsync(ports, "remote-port-unknown-minimum", 540, 510); ports.Close();
        }
        finally { model.MappingRules.Remove(serialRule); model.MappingRules.Remove(networkRule); }
    }

    private static T OpenMappingDialog<T>(Button button) where T : Window
    {
        T? opened = null; Exception? failure = null;
        button.Dispatcher.BeginInvoke(new Action(() =>
        {
            try { opened = Application.Current.Windows.OfType<T>().LastOrDefault(window => window.IsVisible) ?? throw new InvalidOperationException("mapping dialog did not open"); }
            catch (Exception ex) { failure = ex; }
            finally { opened?.Close(); }
        }));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (failure is not null) throw failure;
        return opened ?? throw new InvalidOperationException("mapping add button did not create a dialog");
    }

    private static void CheckInsideWindow(Window window, params FrameworkElement[] elements)
    {
        foreach (var element in elements)
        {
            var position = element.TranslatePoint(new Point(), window);
            DeviceConnectionsChecks.Require(element.ActualWidth > 0 && element.ActualHeight > 0 && position.X >= 0 && position.Y >= 0
                && position.X + element.ActualWidth <= window.ActualWidth + 1 && position.Y + element.ActualHeight <= window.ActualHeight + 1,
                "mapping controls and actions fit inside the minimum window without clipping");
        }
    }
    private static async Task CaptureAsync(Window window, string name, double width, double height)
    {
        window.Width = width; window.Height = height; window.UpdateLayout(); await Task.Delay(100);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var folder = Path.Combine(Path.GetTempPath(), "EmbedDesk-Design-QA"); Directory.CreateDirectory(folder); var path = Path.Combine(folder, name + ".png");
        using var output = File.Create(path); new PngBitmapEncoder { Frames = { BitmapFrame.Create(bitmap) } }.Save(output); Console.WriteLine("UI SCREENSHOT " + path);
    }
    private static void CheckRelayEditorAlignment(SettingsWindow window)
    {
        var button = (Button)window.FindName("AddRelayButton");
        foreach (var name in new[] { "RelayNameInput", "RelayUrlInput" })
        {
            var input = (TextBox)window.FindName(name);
            DeviceConnectionsChecks.Require(Math.Abs(button.ActualHeight - input.ActualHeight) < 0.1
                && Math.Abs(button.TranslatePoint(new Point(), window).Y - input.TranslatePoint(new Point(), window).Y) < 0.1,
                "relay add button aligns with both input boxes at the current window size");
        }
        var list = (ListBox)window.FindName("RelayServers");
        DeviceConnectionsChecks.Require(list.ActualHeight >= 100, "relay list remains usable at the minimum window size");
    }
    private sealed class RelayCredentialCleanup(ConnectionCenterViewModel model, RelayServerProfile[] profiles) : IDisposable
    {
        public void Dispose()
        {
            foreach (var profile in profiles) model.SaveRelayAdmissionToken(profile, string.Empty);
            model.SaveSecret("admission", string.Empty);
        }
    }
    private static T Find<T>(DependencyObject root) where T : DependencyObject
    { if (root is T result) return result; for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { try { return Find<T>(VisualTreeHelper.GetChild(root, i)); } catch (InvalidOperationException) { } } throw new InvalidOperationException(typeof(T).Name); }
}
