using System.IO;
using System.Reflection;
using System.Collections;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EmbedDesk.App.Controls;
using EmbedDesk.Infrastructure.Networking;

namespace EmbedDesk.App.SmokeTests;

internal static class LanFileClipboardChecks
{
    public static async Task RunAsync(string shared)
    {
        var original = Clipboard.GetDataObject(); var host = new Window { Width = 300, Height = 180, ShowInTaskbar = false };
        var tools = new NetworkToolsWorkspace(); host.Content = tools; host.Show();
        var window = new LanFileShareWindow { Owner = host };
        try
        {
            window.Show(); var copy = (Button)window.FindName("CopyConnectionButton");
            DeviceConnectionsChecks.Require(!copy.IsEnabled && ((TextBox)window.FindName("ServerCode")).IsReadOnly, "server credentials are selectable and sharing requires an active server");
            ((TextBox)window.FindName("Root")).Text = shared;
            Click(window, "StartServerButton"); await Operation(window);
            Click(window, "CopyConnectionButton"); var info = LanFileConnectionInfo.Parse(Clipboard.GetText());
            DeviceConnectionsChecks.Require(copy.IsEnabled && info.AccessCode == ((TextBox)window.FindName("ServerCode")).Text, "copy button exports the active server's current access code");
            ((TabControl)window.FindName("Modes")).SelectedIndex = 1;
            Click(window, "ImportConnectionButton");
            var address = (ComboBox)window.FindName("Server"); var code = (PasswordBox)window.FindName("Code");
            DeviceConnectionsChecks.Require(address.Text == info.Addresses[0] && code.Password == info.AccessCode && address.Items.Count == info.Addresses.Count, "clipboard import restores all candidate addresses and the access code");
            await Operation(window);
            DeviceConnectionsChecks.Require(((DataGrid)window.FindName("Entries")).Items.Count > 0 && ((Button)window.FindName("ConnectButton")).Background is not null,
                "clipboard import automatically browses the server and leaves an explicit connect action available");
            var oldItems = ((DataGrid)window.FindName("Entries")).ItemsSource; var oldAddress = address.Text; var oldCode = code.Password;
            Clipboard.SetText("malformed connection information"); Click(window, "ImportConnectionButton");
            DeviceConnectionsChecks.Require(address.Text == oldAddress && code.Password == oldCode && ReferenceEquals(((DataGrid)window.FindName("Entries")).ItemsSource, oldItems)
                && ((TextBlock)window.FindName("ErrorText")).Text.Length > 0, "invalid clipboard information leaves the current connection and listing intact");
            Click(window, "DiscoverButton");
            DeviceConnectionsChecks.Require(!((Button)window.FindName("ImportConnectionButton")).IsEnabled && !address.IsEnabled && !code.IsEnabled, "active operations prevent importing a different transfer target");
            await Operation(window);
            await CheckQueueAndBackgroundAsync(window, tools, shared, info.AccessCode);
            // Only synthetic values are rendered in screenshots; live addresses and access codes never reach artifacts.
            ((TabControl)window.FindName("Modes")).SelectedIndex = 0;
            ((TextBox)window.FindName("Root")).Text = "共享目录";
            ((TextBox)window.FindName("ServerStatus")).Text = "http://127.0.0.1:9100\nhttp://127.0.0.2:9100";
            ((TextBox)window.FindName("ServerCode")).Text = "012345ABCDEF";
            await CaptureAsync(window, "files-clipboard-server-default", 820, 620);
            await CaptureAsync(window, "files-clipboard-server-minimum", 700, 500);
            ((TabControl)window.FindName("Modes")).SelectedIndex = 1;
            Clipboard.SetText(LanFileConnectionInfo.Encode(["http://127.0.0.1:9100", "http://127.0.0.2:9100"], "012345ABCDEF"));
            Click(window, "ImportConnectionButton");
            await CaptureAsync(window, "files-clipboard-client-default", 820, 620);
            await CaptureAsync(window, "files-clipboard-client-minimum", 700, 500);
            DeviceConnectionsChecks.Require(((Button)window.FindName("ImportConnectionButton")).ActualWidth >= ((Button)window.FindName("ImportConnectionButton")).DesiredSize.Width && ((DataGrid)window.FindName("Entries")).ActualHeight > 60,
                "clipboard import controls and file listing fit the minimum window");
            await CloseChoiceAsync(window, "StopButton");
            DeviceConnectionsChecks.Require(!window.IsVisible && !window.HasActiveWork && !window.IsBackgroundService, "stop and close waits for file service cleanup");
            Console.WriteLine("PASS LAN clipboard copy/import UI and default/minimum screenshots");
        }
        finally
        {
            await window.ShutdownAsync(); window.Close(); host.Close();
            if (original is null) Clipboard.Clear(); else Clipboard.SetDataObject(original, true);
        }
    }
    private static async Task CheckQueueAndBackgroundAsync(LanFileShareWindow window, NetworkToolsWorkspace tools, string shared, string accessCode)
    {
        var service = (LanFileService)typeof(LanFileShareWindow).GetField("_service", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var endpoint = $"http://127.0.0.1:{service.Port}"; var address = (ComboBox)window.FindName("Server"); var code = (PasswordBox)window.FindName("Code");
        address.Text = endpoint; code.Password = accessCode;
        await Browse(window, "");
        DeviceConnectionsChecks.Require(!((Button)window.FindName("ParentButton")).IsEnabled, "root navigation disables going above the shared directory");
        var entries = (DataGrid)window.FindName("Entries"); entries.SelectedItem = entries.Items.Cast<object>().Single(e => Value<string>(e, "Name") == "sub");
        typeof(LanFileShareWindow).GetMethod("Entries_DoubleClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [entries, null]); await Operation(window);
        DeviceConnectionsChecks.Require(((TextBlock)window.FindName("CurrentPath")).Text == "/sub/" && ((Button)window.FindName("ParentButton")).IsEnabled, "double-click enters a shared child directory");
        Directory.CreateDirectory(Path.Combine(shared, "sub", "nested")); await Browse(window, "sub/nested/");
        Click(window, "ParentButton"); await Operation(window);
        DeviceConnectionsChecks.Require(((TextBlock)window.FindName("CurrentPath")).Text == "/sub/", "parent navigation remains inside the shared tree");
        Click(window, "ParentButton"); await Operation(window);
        DeviceConnectionsChecks.Require(((TextBlock)window.FindName("CurrentPath")).Text == "/" && !((Button)window.FindName("ParentButton")).IsEnabled, "parent navigation stops at the shared root");
        var sources = new List<string>();
        for (var index = 0; index < 4; index++)
        {
            var source = Path.Combine(Path.GetDirectoryName(shared)!, $"queue-{index}.bin");
            await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(2 * LanFileService.TransferChunkSize + 17)); sources.Add(source);
        }
        var queuedBeforeInvalid = ((IEnumerable)typeof(LanFileShareWindow).GetField("_transferItems", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Cast<object>().Count();
        window.QueueUpload(sources[0], "C:/outside.bin", true);
        DeviceConnectionsChecks.Require(((IEnumerable)typeof(LanFileShareWindow).GetField("_transferItems", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Cast<object>().Count() == queuedBeforeInvalid,
            "client rejects an absolute remote target while allowing local source files from any directory");
        foreach (var source in sources) window.QueueUpload(source, "sub/" + Path.GetFileName(source), true);
        var tasks = ((IEnumerable)typeof(LanFileShareWindow).GetField("_transferItems", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Cast<object>().ToArray();
        DeviceConnectionsChecks.Require(tasks.Count(t => Value<string>(t, "Status") == "上传中") == 3 && Value<string>(tasks[3], "Status") == "等待中", "upload queue caps active files at three");
        ActionTask(window, tasks[0]); await Work(tasks[0]);
        DeviceConnectionsChecks.Require(Value<string>(tasks[0], "Status") == "已暂停", "pause awaits in-flight work and retains a resumable queue item");
        ((TextBlock)window.FindName("ErrorText")).Text = "";
        address.Text = "http://127.0.0.1:9100"; code.Password = "012345ABCDEF";
        ActionTask(window, tasks[0]);
        Click(window, "TransferButton");
        await CaptureAsync(window, "files-transfer-panel-default", 820, 620);
        await CaptureAsync(window, "files-transfer-panel-minimum", 700, 500);
        await Task.WhenAll(tasks.Select(Work));
        foreach (var source in sources)
            DeviceConnectionsChecks.Require((await File.ReadAllBytesAsync(source)).SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(shared, "sub", Path.GetFileName(source)))), "queued transfers retain their original target when the displayed address changes");
        DeviceConnectionsChecks.Require(tasks.All(t => Value<string>(t, "Status") == "已完成"), "paused upload resumes and all queued files complete");
        Click(window, "TransferButton");
        ((TextBlock)window.FindName("ErrorText")).Text = "";
        address.Text = endpoint; code.Password = accessCode;
        code.Password = "invalid"; window.QueueUpload(sources[0], "retry-queue.bin", true);
        var retry = ((IEnumerable)typeof(LanFileShareWindow).GetField("_transferItems", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Cast<object>().Last();
        await Work(retry); DeviceConnectionsChecks.Require(Value<string>(retry, "Status") == "失败", "authentication failure remains visible in the transfer panel");
        code.Password = accessCode; ActionTask(window, retry); await Work(retry);
        DeviceConnectionsChecks.Require(Value<string>(retry, "Status") == "已完成", "failed transfer can retry with corrected credentials");
        await CloseChoiceAsync(window, "KeepButton", capture: true);
        DeviceConnectionsChecks.Require(window.IsBackgroundService && !window.IsVisible && window.HasActiveWork, "closing with background retention hides the window without stopping its service");
        await using var client = new LanFileService();
        DeviceConnectionsChecks.Require((await client.ListRemoteAsync(endpoint, accessCode, "sub/")).Count >= 4, "hidden file service remains reachable");
        typeof(NetworkToolsWorkspace).GetMethod("OpenTool_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tools, [new Button { Tag = "LanFiles" }, new RoutedEventArgs()]);
        DeviceConnectionsChecks.Require(window.IsVisible && !window.IsBackgroundService && window.Owner.OwnedWindows.OfType<LanFileShareWindow>().Single() == window, "opening the tool restores the same retained service instance");
        Console.WriteLine("PASS root navigation, parallel queue, pause/resume, target snapshots and background restore");
    }
    private static Task Browse(LanFileShareWindow window, string folder) => (Task)typeof(LanFileShareWindow).GetMethod("BrowseAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [folder])!;
    private static T Value<T>(object target, string name) => (T)target.GetType().GetProperty(name)!.GetValue(target)!;
    private static Task Work(object task) => (Task)task.GetType().GetField("Work", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(task)!;
    private static void ActionTask(LanFileShareWindow window, object task) => typeof(LanFileShareWindow).GetMethod("TransferAction_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [new Button { Tag = task }, new RoutedEventArgs()]);
    private static async Task CloseChoiceAsync(LanFileShareWindow window, string button, bool capture = false)
    {
        var completed = new TaskCompletionSource(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        timer.Tick += async (_, _) =>
        {
            var dialog = window.OwnedWindows.OfType<LanFileCloseChoiceWindow>().SingleOrDefault(); if (dialog is null) return;
            timer.Stop();
            try { if (capture) await CaptureAsync(dialog, "files-background-close-choice", 520, 230); Click(dialog, button); completed.SetResult(); }
            catch (Exception ex) { dialog.Close(); completed.SetException(ex); }
        };
        timer.Start(); window.Close(); await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (button == "StopButton") { await window.ShutdownAsync(); await Task.Delay(100); }
    }
    private static Task Operation(LanFileShareWindow window) => (Task)typeof(LanFileShareWindow).GetField("_operation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    private static void Click(Window window, string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task CaptureAsync(Window window, string name, double width, double height)
    {
        window.Width = width; window.Height = height; window.UpdateLayout(); await Task.Delay(120);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var directory = Path.Combine(Path.GetTempPath(), "EmbedDesk-Design-QA"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".png");
        using var output = File.Create(path); new PngBitmapEncoder { Frames = { BitmapFrame.Create(bitmap) } }.Save(output);
        Console.WriteLine("UI SCREENSHOT " + path);
    }
}
