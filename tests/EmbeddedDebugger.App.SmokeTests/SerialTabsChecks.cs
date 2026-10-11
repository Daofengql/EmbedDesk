using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;
using EmbeddedDebugger.App.Controls;
using EmbeddedDebugger.App.ViewModels;
using EmbeddedDebugger.Core.Abstractions;
using EmbeddedDebugger.Core.Models;
using EmbeddedDebugger.Infrastructure.Networking;
using Microsoft.Web.WebView2.Wpf;

namespace EmbeddedDebugger.App.SmokeTests;

internal static class SerialTabsChecks
{
    public static async Task RunAsync()
    {
        var serial = new Ports();
        await using var remote = new RemotePorts();
        await using var vm = new MainWindowViewModel(new TcpUdpTransportService(), serial, new FakeBridge(), remote);
        await vm.InitializeAsync();
        var first = vm.SelectedSerialSession!;
        first.SelectedSerialPort!.BaudRate = 9600;
        first.SerialSendText = "one";
        first.SerialIntervalMs = "50";
        await first.OpenSelectedSerialCommand.ExecuteAsync(null);
        var com3 = serial.Channels.Last();
        var second = vm.AddSerialSession();
        Require(second.SelectedSerialPort?.PortName == "COM7", "New serial tabs prefer a device not already selected");
        second.SerialSendText = "two";
        await second.OpenSelectedSerialCommand.ExecuteAsync(null);
        var com7 = serial.Channels.Last();
        Require(com3.Settings.BaudRate == 9600 && com7.Settings.BaudRate == 115200, "Each serial tab owns its port settings");
        await Task.Run(() => { com3.Receive("device-one\r\n"); com7.Receive("device-two\r\n"); });
        await Until(() => first.Terminal.ReceivedBytes > 0 && second.Terminal.ReceivedBytes > 0);
        Require(Received(first).Contains("device-one") && !Received(first).Contains("device-two") && Received(second).Contains("device-two"), "Concurrent received data stays in its owning tab");
        first.SerialLoopEnabled = true;
        await Until(() => com3.Writes.Count >= 2);
        await second.SendSelectedSerialCommand.ExecuteAsync(null);
        var writes = com3.Writes.Count;
        vm.SelectedSerialSession = first; vm.SelectedSerialSession = second;
        await Until(() => com3.Writes.Count > writes);
        Require(first.SerialLoopEnabled && com3.Writes.All(x => x == "one") && com7.Writes.SequenceEqual(new[] { "two" }), "Switching tabs neither stops nor redirects a background send loop");
        first.StopSerialJobCommand.Execute(null);
        await Until(() => first.RunQueueCommand.CanExecute(null));
        first.SerialSendQueue.Add(new SerialSendQueueItem { Payload = "queue-one" });
        first.SerialSendQueue.Add(new SerialSendQueueItem { Payload = "queue-two" });
        var queue = first.RunQueueCommand.ExecuteAsync(null);
        await second.SendShellInputAsync("shell-two");
        await queue;
        Require(com3.Writes.TakeLast(2).SequenceEqual(new[] { "queue-one", "queue-two" }) && com7.Writes.Last() == "shell-two" && second.SerialSendQueue.Count == 0, "Queue and Shell input remain independent across serial tabs");
        var duplicate = vm.AddSerialSession();
        duplicate.SelectedSerialPort = duplicate.SerialPorts.Single(p => p.PortName == "COM3");
        var channelCount = serial.Channels.Count;
        await duplicate.OpenSelectedSerialCommand.ExecuteAsync(null);
        Require(serial.Channels.Count == channelCount && vm.SelectedSerialSession == first && !duplicate.IsOpen, "Duplicate port opens select the existing tab without creating a channel");
        var firstPort = first.SelectedSerialPort;
        await vm.ScanSerialPortsCommand.ExecuteAsync(null);
        Require(first.SelectedSerialPort == firstPort && first.IsOpen && second.IsOpen && first.Terminal.ReceivedBytes > 0, "Device refresh retains open tabs, settings and buffers");
        vm.SerialPorts.Single(p => p.PortName == "COM3").IsSelected = true;
        await vm.StartBridgeCommand.ExecuteAsync(null);
        Require(vm.IsBridgeRunning && first.IsOpen, "Publishing a mapping does not reopen or take a locally owned COM");
        await vm.StopBridgeCommand.ExecuteAsync(null);

        var window = new MainWindow(vm); window.Show();
        try
        {
            vm.SelectedSerialSession = first;
            await Task.Delay(150); window.UpdateLayout();
            var firstView = View(window, first);
            ((CheckBox)firstView.FindName("ShowTime")).IsChecked = true;
            vm.SelectedSerialSession = second; await Task.Delay(100); window.UpdateLayout();
            var secondView = View(window, second);
            Require(secondView.IsVisible && !firstView.IsVisible && ((CheckBox)secondView.FindName("ShowTime")).IsChecked == false, "Only the selected serial pane is visible; display options stay independent");
            vm.SelectedSerialSession = first;
            var tabRoot = Descendants<SerialTabWorkspace>(window).Single();
            var label = Descendants<Button>(tabRoot).Single(b => b.Name == "TabLabel" && b.DataContext == first);
            var nameEditor = Descendants<TextBox>(tabRoot).Single(b => b.Name == "TitleEditor" && b.DataContext == first);
            first.SerialLoopEnabled = true;
            writes = com3.Writes.Count;
            DoubleClick(label);
            Require(nameEditor.IsVisible && nameEditor.IsKeyboardFocused, "Double-clicking a serial tab edits its title in place");
            nameEditor.Text = "主控日志"; PressKey(nameEditor, Key.Enter);
            await Until(() => com3.Writes.Count > writes);
            Require(first.Title == "主控日志" && first.Detail.Contains("COM3") && second.Title == "COM7" && first.IsOpen && first.SerialLoopEnabled,
                "Renaming a running serial tab preserves its port, background job and other tab names");
            DoubleClick(label); nameEditor.Text = "放弃修改"; PressKey(nameEditor, Key.Escape);
            Require(first.Title == "主控日志" && !nameEditor.IsVisible, "Escape discards a pending serial tab rename");
            DoubleClick(label); nameEditor.Text = " "; PressKey(nameEditor, Key.Enter);
            Require(first.Title == "COM3", "Clearing a custom serial title restores its port name");
            DoubleClick(label); PressKey(nameEditor, Key.Enter);
            Require(first.CustomTitle.Length == 0, "Leaving the default title unchanged preserves automatic port naming");
            DoubleClick(label); nameEditor.Text = "主控日志"; PressKey(nameEditor, Key.Enter);
            first.StopSerialJobCommand.Execute(null);
            await FileWorkspaceChecks.CaptureAsync(window, "embedded-debugger-serial-tabs.png");
            var selector = (ComboBox)firstView.FindName("PortSelector");
            Require(!selector.IsEnabled, "Connected tabs lock the device selector to prevent accidental retargeting");
            var width = selector.ActualWidth;
            window.Width = 1080; window.Height = 680; await Task.Delay(100);
            Require(selector.ActualWidth == width && ((TextBox)firstView.FindName("ReceiveText")).ActualHeight >= 80 && ((TextBox)firstView.FindName("SendEditor")).ActualHeight >= 45, "Small windows retain a fixed-width port selector and usable send/receive panes");
            await FileWorkspaceChecks.CaptureAsync(window, "embedded-debugger-serial-tabs-small.png");

            first.ShellMode = true; await Task.Delay(150);
            var shell = (ShellTerminal)firstView.FindName("Shell");
            var browser = (WebView2)shell.FindName("Browser");
            await UntilAsync(async () => browser.CoreWebView2 is not null && await browser.ExecuteScriptAsync("typeof terminal !== 'undefined' && typeof terminal.write === 'function' && terminal.buffer.active.getLine(0).translateToString(true).includes('device-one')") == "true");
            await browser.ExecuteScriptAsync("terminal.options.fontSize=17; terminal.write('\\x1b[?1049h\\x1b[2J\\x1b[Hserial-tui'); window.serialTabMarker=42;");
            vm.SelectedSerialSession = second;
            com3.Receive("-background"); await Task.Delay(150);
            vm.SerialSessions.Move(vm.SerialSessions.IndexOf(first), vm.SerialSessions.Count - 1);
            vm.SelectedSerialSession = first; await Task.Delay(150);
            Require(ReferenceEquals(View(window, first), firstView) && await browser.ExecuteScriptAsync("window.serialTabMarker===42 && terminal.options.fontSize===17 && terminal.buffer.active.type==='alternate'") == "true", "Switching and reordering serial tabs retain the live Shell instance, font and alternate screen");
            Require((await browser.ExecuteScriptAsync("terminal.buffer.active.getLine(0).translateToString(true)")).Contains("serial-tui-background"), "Hidden serial Shell tabs continue receiving terminal output");
            var mainTabs = Descendants<TabControl>(window).First();
            mainTabs.SelectedIndex = 2; await Task.Delay(80);
            mainTabs.SelectedIndex = 0; await Task.Delay(100);
            Require(await browser.ExecuteScriptAsync("window.serialTabMarker===42 && terminal.buffer.active.type==='alternate'") == "true", "Leaving the serial feature and returning preserves its live Shell");
            first.ShellMode = false;

            // Exercise the actual close button and both outcomes of the modal confirmation.
            var root = Descendants<SerialTabWorkspace>(window).Single();
            var close = Descendants<Button>(root).Single(b => b.DataContext == duplicate && Equals(b.ToolTip, "关闭标签"));
            var sawConfirmation = false;
            using (var dialog = AutoConfirm(false, () => sawConfirmation = true)) close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(sawConfirmation && vm.SerialSessions.Contains(duplicate), "Canceling the close confirmation keeps the serial tab");
            using (var dialog = AutoConfirm(true, () => { })) close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !vm.SerialSessions.Contains(duplicate));

            second.SerialIntervalMs = "50"; second.SerialLoopEnabled = true;
            await Until(() => com7.Writes.Count >= 3);
            await vm.CloseSerialSessionAsync(first);
            writes = com7.Writes.Count;
            await Until(() => com7.Writes.Count > writes);
            Require(com3.Disposed && second.IsOpen && second.SerialLoopEnabled, "Closing one serial tab disposes only its channel and leaves other jobs running");
            await vm.CloseSerialSessionAsync(second);
            Require(com7.Disposed && vm.SerialSessions.Count == 1 && vm.SelectedSerialSession?.Terminal.Entries.Count == 0, "Closing the last serial tab stops jobs and provides a fresh empty tab");

            var pending = vm.SelectedSerialSession!;
            serial.DelayOpen = true;
            var opening = pending.OpenSelectedSerialCommand.ExecuteAsync(null);
            await Until(() => serial.Channels.Last().OpenStarted);
            var pendingChannel = serial.Channels.Last();
            duplicate = vm.AddSerialSession();
            duplicate.SelectedSerialPort = duplicate.SerialPorts.Single(p => p.PortName == pending.SelectedSerialPort!.PortName);
            await duplicate.OpenSelectedSerialCommand.ExecuteAsync(null);
            Require(vm.SelectedSerialSession == pending && !duplicate.IsOpen, "Ports are reserved during connection, before OpenAsync completes");
            await vm.CloseSerialSessionAsync(pending); await opening;
            Require(pendingChannel.Disposed && !pendingChannel.IsOpen, "Closing an opening tab cancels the handshake and releases its channel");
            serial.DelayOpen = false; serial.FailOpen = true;
            await duplicate.OpenSelectedSerialCommand.ExecuteAsync(null);
            Require(duplicate.Status == "连接失败" && serial.Channels.Last().Disposed, "Failed serial opens dispose their channel and keep the tab configurable");
            serial.FailOpen = false;
            await duplicate.OpenSelectedSerialCommand.ExecuteAsync(null);
            Require(duplicate.IsOpen, "A failed connection releases the port reservation for retry");
            serial.Available = false;
            await vm.ScanSerialPortsCommand.ExecuteAsync(null);
            await Until(() => !duplicate.IsBusy && !duplicate.IsOpen);
            Require(duplicate.SelectedSerialPort is { IsAvailable: false } && duplicate.Status == "设备已离线", "Unplugged ports stop their tab without discarding its configuration");
            serial.Available = true;
            await vm.ScanSerialPortsCommand.ExecuteAsync(null);
            await duplicate.OpenSelectedSerialCommand.ExecuteAsync(null);
            Require(duplicate.IsOpen, "Reappearing devices can reconnect in the same tab");

            await remote.ConnectAsync("test", 9100);
            var virtualTab = vm.AddSerialSession();
            virtualTab.SelectedSerialPort = virtualTab.SerialPorts.Single(p => p.IsRemote);
            await virtualTab.OpenSelectedSerialCommand.ExecuteAsync(null);
            await virtualTab.SendShellInputAsync("remote-serial");
            Require(remote.Channel?.Writes.Last() == "remote-serial", "Application virtual serial ports use the same independent tab workflow");
            await remote.DisconnectAsync();
            await Until(() => !virtualTab.IsOpen && !virtualTab.IsBusy);
            Require(duplicate.IsOpen && virtualTab.SelectedSerialPort is { IsAvailable: false }, "Mapping disconnection affects only its virtual serial tabs");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS serial multi-tab regression checks");
    }

    private static void DoubleClick(Button button) => button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Control.PreviewMouseDoubleClickEvent });
    private static void PressKey(TextBox editor, Key key) => editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor), Environment.TickCount, key) { RoutedEvent = UIElement.PreviewKeyDownEvent });
    private static string Received(SerialSessionViewModel session) => string.Concat(session.Terminal.Entries.Where(e => e.IsReceived).Select(e => Encoding.UTF8.GetString(e.Data)));
    private static SerialWorkspace View(Window window, SerialSessionViewModel session) => Descendants<SerialWorkspace>(window).Single(v => v.DataContext == session);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static async Task Until(Func<bool> check) => await UntilAsync(() => Task.FromResult(check()));
    private static async Task UntilAsync(Func<Task<bool>> check)
    {
        var end = DateTime.UtcNow.AddSeconds(15);
        while (!await check()) { if (DateTime.UtcNow > end) throw new TimeoutException("Serial tab check timed out"); await Task.Delay(25); }
    }
    private static IDisposable AutoConfirm(bool result, Action seen)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        timer.Tick += (_, _) =>
        {
            if (Application.Current.Windows.OfType<ConfirmationWindow>().FirstOrDefault() is not { } confirmation) return;
            timer.Stop(); seen(); confirmation.DialogResult = result;
        };
        timer.Start(); return new TimerScope(timer);
    }
    private sealed class TimerScope(DispatcherTimer timer) : IDisposable { public void Dispose() => timer.Stop(); }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine($"PASS {message}");
    }
    private sealed class Ports : ISerialPortService
    {
        public bool Available { get; set; } = true;
        public bool DelayOpen { get; set; }
        public bool FailOpen { get; set; }
        public List<Channel> Channels { get; } = [];
        public Task<IReadOnlyList<SerialPortDescriptor>> ScanAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SerialPortDescriptor>>(Available
            ? [new("COM3", "USB Serial Device", "USB Serial Device", "STMicroelectronics"), new("COM7", "CP210x USB to UART", "CP210x USB to UART Bridge", "Silicon Labs")] : []);
        public ISerialPortChannel CreateChannel(SerialPortSettings settings)
        {
            var channel = new Channel(settings) { DelayOpen = DelayOpen, FailOpen = FailOpen }; Channels.Add(channel); return channel;
        }
    }
    private sealed class Channel(SerialPortSettings settings) : ISerialPortChannel
    {
        public string Id { get; } = Guid.NewGuid().ToString();
        public SerialPortSettings Settings => settings;
        public bool IsOpen { get; private set; }
        public bool Disposed { get; private set; }
        public bool OpenStarted { get; private set; }
        public bool DelayOpen { get; init; }
        public bool FailOpen { get; init; }
        public ConcurrentQueue<string> Writes { get; } = new();
        public event EventHandler<ReadOnlyMemory<byte>>? DataReceived;
        public async Task OpenAsync(CancellationToken cancellationToken = default)
        {
            OpenStarted = true;
            if (DelayOpen) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (FailOpen) throw new IOException("Test open failed");
            cancellationToken.ThrowIfCancellationRequested(); IsOpen = true;
        }
        public Task CloseAsync(CancellationToken cancellationToken = default) { IsOpen = false; return Task.CompletedTask; }
        public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            await Task.Delay(5, cancellationToken);
            if (!IsOpen) throw new InvalidOperationException("Test channel closed");
            Writes.Enqueue(Encoding.UTF8.GetString(data.Span));
        }
        public void Receive(string text) => DataReceived?.Invoke(this, Encoding.UTF8.GetBytes(text));
        public ValueTask DisposeAsync() { Disposed = true; IsOpen = false; return ValueTask.CompletedTask; }
    }
    private sealed class RemotePorts : IRemoteSerialClient
    {
        public bool IsConnected { get; private set; }
        public IReadOnlyList<RemoteSerialPort> Ports => IsConnected ? [new(1, "REMOTE:1", "Mapped COM1")] : [];
        public Channel? Channel { get; private set; }
        public event EventHandler? PortsChanged;
        public event EventHandler? ConnectionChanged;
        public Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
        {
            IsConnected = true; PortsChanged?.Invoke(this, EventArgs.Empty); ConnectionChanged?.Invoke(this, EventArgs.Empty); return Task.CompletedTask;
        }
        public Task DisconnectAsync()
        {
            IsConnected = false; PortsChanged?.Invoke(this, EventArgs.Empty); ConnectionChanged?.Invoke(this, EventArgs.Empty); return Task.CompletedTask;
        }
        public ISerialPortChannel CreateChannel(string portName, SerialPortSettings settings) => Channel = new Channel(settings);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
