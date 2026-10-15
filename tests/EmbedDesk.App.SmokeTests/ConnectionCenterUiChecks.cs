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
        await model.InitializeAsync();
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
        // Only the screenshot uses synthetic values; session state remains the real view model.
        identity.SetCurrentValue(TextBlock.TextProperty, "ABCDEF012345");
        password.SetCurrentValue(TextBlock.TextProperty, "DEMO12345678");
        await CaptureAsync(main, "center-default", 1340, 940);
        await CaptureAsync(main, "center-minimum", 1080, 680);
        var mappingCard = (Border)center.FindName("MappingControlCard");
        DeviceConnectionsChecks.Require(identity.FontSize >= 30 && mappingCard.ActualHeight >= 200,
            "large identity and bottom mapping controls fit the minimum main window");
        model.Connections.Peers.Clear();
        await CaptureAsync(main, "center-local-mapping-minimum", 1080, 680);
        model.Connections.Peers.Add(new("example", "ABCDEF012345", false, "已连接", "局域网直连", 1024, 2048));
        await CaptureAsync(main, "center-mappings-minimum", 1080, 680);
        identity.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
        password.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
        var session = model.SelectedSerialSession!; session.SelectedSerialPort = session.SerialPorts.Single(p => p.PortName == "COM7"); await session.OpenSelectedSerialCommand.ExecuteAsync(null);
        DeviceConnectionsChecks.Require(session.IsOpen, "local serial opens outside reservations");
        main.HideToTray(); DeviceConnectionsChecks.Require(!main.IsVisible && session.IsOpen, "tray hide keeps live serial");
        main.RestoreFromTray(); DeviceConnectionsChecks.Require(main.IsVisible && session.IsOpen && model.SelectedSerialSession == session, "tray restore preserves existing session"); await session.CloseAsync();
        using (var store = new AppSettingsStore(Path.Combine(Path.GetTempPath(), "EmbedDesk-Schema-Test", Guid.NewGuid().ToString("N"))))
        { store.Update(s => { s.ConnectionPreferences = s.ConnectionPreferences with { CloseAction = MainWindowCloseAction.Tray }; }); store.SaveNow(); using var reload = new AppSettingsStore(Path.GetDirectoryName(store.FilePath)); DeviceConnectionsChecks.Require(reload.Snapshot().ConnectionPreferences.CloseAction == MainWindowCloseAction.Tray, "close preference persists"); Directory.Delete(Path.GetDirectoryName(store.FilePath)!, true); }
        var profiles = new[] { new RelayServerProfile { Name = "本机中继", Url = "http://127.0.0.1:7080" }, new RelayServerProfile { Name = "备用中继示例", Url = "http://127.0.0.1:1" } };
        model.AppSettings.Update(s => s.ConnectionPreferences = s.ConnectionPreferences with { RelayServers = profiles.ToList(), DefaultRelayServerId = profiles[0].Id });
        var settings = new SettingsWindow(model); settings.Show(); await settings.ProbeTask;
        var tabs = Find<TabControl>(settings);
        await CaptureAsync(settings, "settings-relays-default", 820, 680);
        for (var index = 0; index < tabs.Items.Count; index++) { tabs.SelectedIndex = index; await CaptureAsync(settings, "settings-" + index, 680, 570); }
        DeviceConnectionsChecks.Require(settings.FindName("AutoConnect") is null && settings.RelayStatuses.Count == 2
            && settings.RelayStatuses.Any(s => s.IsDefault), "relay settings list replaces the retired auto-connect control and marks the default server");
        ((TextBox)settings.FindName("RelayNameInput")).Text = "取消时不保存";
        ((TextBox)settings.FindName("RelayUrlInput")).Text = "http://127.0.0.1:7082";
        ((Button)settings.FindName("AddRelayButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((ListBox)settings.FindName("RelayServers")).SelectedItem = settings.RelayStatuses[2];
        ((Button)settings.FindName("SetDefaultRelayButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        settings.Close();
        DeviceConnectionsChecks.Require(model.Connections.Preferences.RelayServers.Count == 2
            && model.Connections.Preferences.DefaultRelayServerId == profiles[0].Id, "canceling settings discards added servers and default selection");
        var saveSettings = new SettingsWindow(model);
        ((ListBox)saveSettings.FindName("RelayServers")).SelectedItem = saveSettings.RelayStatuses[1];
        ((Button)saveSettings.FindName("SetDefaultRelayButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        saveSettings.Loaded += (_, _) => saveSettings.Dispatcher.BeginInvoke(new Action(() =>
            ((Button)saveSettings.FindName("SaveSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
        DeviceConnectionsChecks.Require(saveSettings.ShowDialog() == true
            && model.Connections.Preferences.DefaultRelayServerId == profiles[1].Id, "save commits the selected default relay");
        model.Connections.Peers.Clear();
        var services = new DeviceServicesWindow(model.Connections); services.Show(); await CaptureAsync(services, "services-minimum", 600, 420);
        model.Connections.Peers.Add(new("example", "ABCDEF012345", false, "已连接", "局域网直连", 1024, 2048));
        await CaptureAsync(services, "mappings-minimum", 600, 420); services.Close();
        model.Connections.SelectedRemoteService = model.Connections.RemoteServices.Single(s => s.Service.Kind == SharedServiceKind.Serial);
        await CaptureAsync(main, "center-remote-serial-minimum", 1080, 680);
        var useSerial = new RemoteSerialUseWindow(model.Connections.SelectedRemoteService, []); useSerial.Show();
        await CaptureAsync(useSerial, "remote-serial-app", 600, 540);
        DeviceConnectionsChecks.Require(((ComboBox)useSerial.FindName("UsageMode")).SelectedIndex == 0 && ((Button)useSerial.FindName("UseSerialButton")).IsEnabled,
            "remote serial defaults to application use without requiring a driver");
        ((ComboBox)useSerial.FindName("UsageMode")).SelectedIndex = 1;
        await CaptureAsync(useSerial, "remote-serial-no-driver", 600, 540);
        DeviceConnectionsChecks.Require(!((Button)useSerial.FindName("UseSerialButton")).IsEnabled,
            "system COM mapping clearly blocks when no driver-backed pair is available");
        ((ComboBox)useSerial.FindName("UsageMode")).SelectedIndex = 0;
        DeviceConnectionsChecks.Require(((Button)useSerial.FindName("UseSerialButton")).IsEnabled, "application serial remains usable when virtual COM is unavailable");
        useSerial.Close();
        var configuredSerial = new RemoteSerialUseWindow(model.Connections.SelectedRemoteService, [new VirtualSerialPair("COM101", "CNCB0", "CNCB0", string.Empty)]);
        configuredSerial.Show(); ((ComboBox)configuredSerial.FindName("UsageMode")).SelectedIndex = 1;
        await CaptureAsync(configuredSerial, "remote-serial-virtual", 600, 540);
        DeviceConnectionsChecks.Require(configuredSerial.UseVirtualPort && configuredSerial.SelectedPair?.PublicPort == "COM101"
            && ((Button)configuredSerial.FindName("UseSerialButton")).IsEnabled, "configured pair enables optional virtual COM mapping");
        configuredSerial.Close();
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
    private static async Task CaptureAsync(Window window, string name, double width, double height)
    {
        window.Width = width; window.Height = height; window.UpdateLayout(); await Task.Delay(100);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var folder = Path.Combine(Path.GetTempPath(), "EmbedDesk-Design-QA"); Directory.CreateDirectory(folder); var path = Path.Combine(folder, name + ".png");
        using var output = File.Create(path); new PngBitmapEncoder { Frames = { BitmapFrame.Create(bitmap) } }.Save(output); Console.WriteLine("UI SCREENSHOT " + path);
    }
    private static T Find<T>(DependencyObject root) where T : DependencyObject
    { if (root is T result) return result; for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { try { return Find<T>(VisualTreeHelper.GetChild(root, i)); } catch (InvalidOperationException) { } } throw new InvalidOperationException(typeof(T).Name); }
}
