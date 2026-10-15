using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EmbedDesk.App.Controls;
using EmbedDesk.App.ViewModels;
using EmbedDesk.Core.Models;
using EmbedDesk.Infrastructure.Diagnostics;

namespace EmbedDesk.App.SmokeTests;

internal static class DebugToolsChecks
{
    internal static async Task RunAsync(MainWindowViewModel vm)
    {
        await CheckFilesAsync();
        await CheckConnectionLifecycleAsync();
        await CheckCameraAsync();
        await CheckPidAsync();
        var main = new MainWindow(vm); main.Show();
        try
        {
            ((TabControl)main.FindName("MainWorkspaceTabs")).SelectedIndex = 5;
            await FileWorkspaceChecks.CaptureAsync(main, "embeddesk-debug-tools-gallery.png");
            main.Width = main.MinWidth; main.Height = main.MinHeight;
            await FileWorkspaceChecks.CaptureAsync(main, "embeddesk-debug-tools-gallery-small.png");
            var gallery = Find<EmbeddedToolsWorkspace>(main).Single();
            foreach (var tag in new[] { "Camera", "Pid", "BinaryEditor" })
            {
                Find<Button>(gallery).Single(b => Equals(b.Tag, tag)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var tool = main.OwnedWindows.Cast<Window>().Single(w => w.IsVisible);
                Require(tool.Title.Contains(tag == "Camera" ? "摄像头" : tag == "Pid" ? "PID" : "二进制", StringComparison.Ordinal), "Gallery opens " + tag);
                tool.Close(); await Until(() => !tool.IsVisible);
            }
        }
        finally { main.Close(); }
    }

    private static async Task CheckFilesAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "EmbedDesk-DebugTools-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "sample.bin"); var copy = Path.Combine(folder, "edited.bin");
        var bytes = new byte[2 * 1024 * 1024]; Array.Fill(bytes, (byte)0x41); bytes[65535] = 0x12; bytes[65536] = 0x34;
        await File.WriteAllBytesAsync(path, bytes);
        try
        {
            await using (var doc = new BinaryFileDocument(path))
            {
                Require(await doc.FindAsync([0x12, 0x34], 0) == 65535, "Binary search crosses chunk boundaries");
                await doc.ReplaceAsync(65535, [0xFE, 0xED]);
                Require(await doc.FindAsync([0xFE, 0xED], 0) == 65535, "Binary search sees unsaved patches");
                Require((await File.ReadAllBytesAsync(path))[65535] == 0x12, "Edits leave the original unchanged");
                doc.Undo(); Require(doc.ModifiedBytes == 0, "Undo removes patches that restore original bytes"); doc.Redo(); Require(doc.ModifiedBytes == 2, "Redo restores the complete edit");
                using var cancel = new CancellationTokenSource(); cancel.Cancel();
                try { await doc.SaveAsync(copy, cancel.Token); throw new InvalidOperationException("Expected save cancellation"); } catch (OperationCanceledException) { }
                Require(!File.Exists(copy) && Directory.GetFiles(folder, "*.tmp").Length == 0, "Canceled save leaves no target or partial temporary file");
                await doc.SaveAsync(copy); Require(doc.ModifiedBytes == 0 && (await doc.ReadAsync(65535, 2)).SequenceEqual(new byte[] { 0xFE, 0xED }), "Save-as publishes patched bytes and rebinds the document");
                await doc.ReplaceAsync(0, [0x99]); await doc.SaveAsync(copy); Require((await doc.ReadAsync(0, 1))[0] == 0x99, "In-place save can replace and reopen the source");
                try { await doc.ReplaceAsync(doc.Length, [1]); throw new InvalidOperationException("Expected range rejection"); } catch (ArgumentException) { }
            }
            var window = new BinaryEditorWindow(); window.Show();
            try
            {
                await window.LoadFileAsync(path);
                var view = (HexEditorView)window.FindName("HexView"); Require(view.Bytes.Length == view.VisibleRows * 16, "Binary editor loads only the visible rows");
                ((TextBox)window.FindName("GotoBox")).Text = "FFFF"; Click(window, "定位"); await Until(() => ((Panel)window.FindName("Toolbar")).IsEnabled);
                Require(view.Selection == 65535, "Binary UI locates an exact byte offset");
                await FileWorkspaceChecks.CaptureAsync(window, "embeddesk-binary-editor.png");
                window.Width = window.MinWidth; window.Height = window.MinHeight;
                await FileWorkspaceChecks.CaptureAsync(window, "embeddesk-binary-editor-small.png");
                view.RaiseEvent(new TextCompositionEventArgs(InputManager.Current.PrimaryKeyboardDevice, new TextComposition(InputManager.Current, view, "CA")) { RoutedEvent = TextCompositionManager.TextInputEvent });
                Require(window.Title.StartsWith("* ", StringComparison.Ordinal), "Binary UI marks unsaved changes");
                Require(view.Bytes[(int)(65535 - view.Offset)] == 0xCA && view.Selection == 65536, "Two HEX keystrokes edit one byte and advance the caret");
                Click(window, "撤销"); await Until(() => ((Panel)window.FindName("Toolbar")).IsEnabled);
                Click(window, "撤销"); await Until(() => ((Panel)window.FindName("Toolbar")).IsEnabled);
                Require(!window.Title.StartsWith("* ", StringComparison.Ordinal), "Binary UI undo returns to clean state");
            }
            finally { await window.ShutdownAsync(); window.Close(); }
        }
        finally { File.Delete(path); File.Delete(copy); Directory.Delete(folder); }
    }

    private static async Task CheckConnectionLifecycleAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var connection = new DebugToolConnection(new FakeSerialService());
        using (var cancel = new CancellationTokenSource())
        {
            var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var run = connection.RunAsync(new(DebugToolTransport.TcpServer, "127.0.0.1", port), _ => Task.CompletedTask, _ => listening.TrySetResult(), cancel.Token);
            await listening.Task; cancel.Cancel(); try { await run; } catch (OperationCanceledException) { }
            listener.Start(); listener.Stop(); Require(true, "Canceling a waiting TCP listener releases its port");
        }
        var count = 0; var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var cancel = new CancellationTokenSource())
        {
            var run = connection.RunAsync(new(DebugToolTransport.Udp, "127.0.0.1", port), _ => { Interlocked.Increment(ref count); received.TrySetResult(); return Task.CompletedTask; }, _ => { }, cancel.Token);
            using var first = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); using var second = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            await first.SendAsync(new byte[] { 1 }, new IPEndPoint(IPAddress.Loopback, port)); await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await second.SendAsync(new byte[] { 2 }, new IPEndPoint(IPAddress.Loopback, port)); await Task.Delay(50);
            await connection.SendAsync(new byte[] { 3 }, cancel.Token); var reply = await first.ReceiveAsync(cancel.Token);
            Require(count == 1 && reply.Buffer[0] == 3, "UDP locks to one sender and returns replies to that peer");
            cancel.Cancel(); try { await run; } catch (OperationCanceledException) { }
        }
        using (var cancel = new CancellationTokenSource())
        {
            var serial = new FakeSerialService(); var transport = new DebugToolConnection(serial); var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var run = transport.RunAsync(new(DebugToolTransport.Serial, "", 0, new SerialPortSettings { PortName = "COM3" }), _ => { delivered.TrySetResult(); return Task.CompletedTask; }, _ => { }, cancel.Token);
            await transport.SendAsync(new byte[] { 7 }, cancel.Token); await delivered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Require(serial.LastSettings is { DtrEnable: false, RtsEnable: false, Handshake: System.IO.Ports.Handshake.None }, "Serial tools keep flow control and control lines off");
            cancel.Cancel(); try { await run; } catch (OperationCanceledException) { }
        }
    }

    private static async Task CheckCameraAsync()
    {
        var camera = new CameraDebugWindow(new FakeSerialService()); camera.Show(); var connection = (DebugToolConnectionPanel)camera.FindName("Connection");
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        Configure(connection, (IPEndPoint)listener.LocalEndpoint);
        using var peer = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5)); var stream = peer.GetStream();
        try
        {
            // Real TCP stream: split header, coalesced frames, both device addresses, all supported image formats.
            var pixels = new byte[80 * 60 * 3];
            for (var y = 0; y < 60; y++) for (var x = 0; x < 80; x++) { pixels[(y * 80 + x) * 3] = (byte)(x * 3); pixels[(y * 80 + x) * 3 + 1] = (byte)(y * 4); pixels[(y * 80 + x) * 3 + 2] = 120; }
            var bitmap = BitmapSource.Create(80, 60, 96, 96, PixelFormats.Rgb24, null, pixels, 240); bitmap.Freeze();
            var badCrc = DebugToolPacketReader.Encode(true, 0, 1, [5, 80, 0, 60, 0]); badCrc[^1] ^= 0x5A;
            await stream.WriteAsync(badCrc);
            Require((await PacketAsync(stream, true)).Payload.SequenceEqual(new byte[] { 1 }), "Camera rejects a corrupt frame and sends a failure acknowledgement");
            foreach (byte kind in new byte[] { 1, 2, 3, 4, 5 })
            {
                byte[] image;
                if (kind <= 3)
                {
                    BitmapEncoder encoder = kind == 1 ? new JpegBitmapEncoder() : kind == 2 ? new BmpBitmapEncoder() : new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var memory = new MemoryStream(); encoder.Save(memory); image = memory.ToArray();
                }
                else if (kind == 4) { image = new byte[80 * 60 * 2]; for (var i = 0; i < image.Length; i += 2) { image[i] = 0; image[i + 1] = 0xF8; } }
                else image = pixels;
                var format = DebugToolPacketReader.Encode(true, 0, 1, [kind, 80, 0, 60, 0]);
                await stream.WriteAsync(format.AsMemory(0, 3)); await stream.WriteAsync(format.AsMemory(3)); Require((await PacketAsync(stream, true)).Payload.SequenceEqual(new byte[] { 0 }), "Camera accepts format " + kind);
                await stream.WriteAsync(DebugToolPacketReader.Encode(true, 0, 2, image)); Require((await PacketAsync(stream, true)).Payload[0] == 0, "Camera decodes image format " + kind);
                await Until(() => ((Image)camera.FindName("Preview0")).Source is BitmapSource);
            }
            await stream.WriteAsync(DebugToolPacketReader.Encode(true, 1, 1, [5, 80, 0, 60, 0])); await PacketAsync(stream, true);
            await stream.WriteAsync(DebugToolPacketReader.Encode(true, 1, 2, pixels)); await PacketAsync(stream, true);
            ((ComboBox)camera.FindName("DeviceCountBox")).SelectedIndex = 1;
            await Until(() => ((Image)camera.FindName("Preview1")).Source is BitmapSource);
            await FileWorkspaceChecks.CaptureAsync(camera, "embeddesk-camera-debug.png"); camera.Width = camera.MinWidth; camera.Height = camera.MinHeight;
            await FileWorkspaceChecks.CaptureAsync(camera, "embeddesk-camera-debug-small.png");
            // Register read correlates the returned sequence; write waits for device confirmation.
            Click(camera, "读取"); var read = await PacketAsync(stream, true); Require(read.Command == 0x11 && read.Payload.Length == 3, "Camera emits documented register read framing");
            await stream.WriteAsync(DebugToolPacketReader.Encode(true, 0, 0x11, [0, read.Payload[0], read.Payload[1], 0xAB]));
            await Until(() => ((TextBlock)camera.FindName("RegisterStatus")).Text.Contains("读取成功", StringComparison.Ordinal));
            Click(camera, "写入"); var write = await PacketAsync(stream, true); Require(write.Command == 0x10 && write.Payload.Length == 2, "Camera emits documented register write framing");
            await stream.WriteAsync(DebugToolPacketReader.Encode(true, 0, 0, [0])); await Until(() => ((TextBlock)camera.FindName("RegisterStatus")).Text.Contains("写入成功", StringComparison.Ordinal));
            await connection.StopAsync();
            ((ComboBox)camera.FindName("ModeBox")).SelectedIndex = 1;
            Configure(connection, (IPEndPoint)listener.LocalEndpoint); using var jpegPeer = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var jpegEncoder = new JpegBitmapEncoder(); jpegEncoder.Frames.Add(BitmapFrame.Create(bitmap)); using var jpegBytes = new MemoryStream(); jpegEncoder.Save(jpegBytes);
            await jpegPeer.GetStream().WriteAsync(jpegBytes.ToArray()); await Until(() => ((Image)camera.FindName("Preview0")).Source is BitmapSource);
            Require(true, "Camera accepts JPEG bytes without any wrapper");
        }
        finally { await connection.StopAsync(); camera.Close(); }
    }

    private static async Task CheckPidAsync()
    {
        var window = new PidDebugWindow(new FakeSerialService()); window.Show(); var connection = (DebugToolConnectionPanel)window.FindName("Connection");
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); Configure(connection, (IPEndPoint)listener.LocalEndpoint);
        using var peer = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5)); var stream = peer.GetStream();
        try
        {
            var payload = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(payload, 100);
            for (byte channel = 1; channel <= 5; channel++) await stream.WriteAsync(DebugToolPacketReader.Encode(false, channel, 1, payload));
            await stream.WriteAsync(DebugToolPacketReader.Encode(false, 1, 4, []));
            for (var sample = 0; sample < 2100; sample++)
                for (byte channel = 1; channel <= 5; channel++)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(payload, (int)(100 + 20 * Math.Sin(sample * .03 + channel)));
                    await stream.WriteAsync(DebugToolPacketReader.Encode(false, channel, 2, payload));
                }
            var plot = (PidPlot)window.FindName("Plot");
            await Until(() => plot.Series.Count == 5 && plot.Series.All(s => s.Samples.Length == 2000));
            Require(plot.Series.All(s => s.Samples.All(p => p.Target == 100)), "PID retains bounded histories with the matching targets");
            foreach (var (label, command) in new[] { ("发送 PID", 0x10), ("发送目标值", 0x11), ("发送周期", 0x15), ("启动", 0x12), ("停止", 0x13), ("复位", 0x14) })
            {
                Click(window, label); var packet = await PacketAsync(stream, false); Require(packet.Command == command && packet.Address == 1, "PID emits " + label);
            }
            ((ComboBox)window.FindName("TimeWindowBox")).SelectedIndex = 4;
            for (var sample = 0; sample < 100; sample++)
            {
                for (byte channel = 1; channel <= 5; channel++)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(payload, (int)(100 + 20 * Math.Sin(sample * .13 + channel)));
                    await stream.WriteAsync(DebugToolPacketReader.Encode(false, channel, 2, payload));
                }
                await Task.Delay(10);
            }
            await Until(() => ((TextBlock)window.FindName("MetricsText")).Text.StartsWith("累计采样 " + 11000.ToString("N0", CultureInfo.CurrentCulture), StringComparison.Ordinal));
            ((CheckBox)window.FindName("PauseBox")).IsChecked = true;
            var raw = plot.Series.Select(s => s.Samples.ToArray()).ToArray();
            var mode = (ComboBox)window.FindName("FilterBox");
            var average = (TextBox)window.FindName("SmoothingWindowBox");
            var alpha = (TextBox)window.FindName("SmoothingAlphaBox");
            var export = (Button)window.FindName("ExportButton");
            mode.SelectedIndex = 1; average.Text = "0";
            Require(!export.IsEnabled && ((TextBlock)window.FindName("FilterHint")).Text.Contains("无效", StringComparison.Ordinal), "PID rejects a zero smoothing window");
            average.Text = "5";
            Require(export.IsEnabled && plot.SmoothingMode == SignalSmoothingMode.MovingAverage && plot.SmoothingWindow == 5, "PID exposes moving average parameters");
            mode.SelectedIndex = 2; alpha.Text = "NaN";
            Require(!export.IsEnabled, "PID rejects a nonfinite smoothing coefficient");
            alpha.Text = "0.25";
            Require(export.IsEnabled && plot.SmoothingMode == SignalSmoothingMode.Exponential && plot.SmoothingAlpha == .25, "PID exposes an independent exponential coefficient");
            for (var channel = 0; channel < raw.Length; channel++) Require(raw[channel].SequenceEqual(plot.Series[channel].Samples), "PID filters leave raw channel history unchanged");
            var fractions = PidPlot.ApplySmoothing([new(0, 100, 0), new(.01, 100, 1)], SignalSmoothingMode.MovingAverage, 2);
            Require(fractions[1].Actual == .5 && fractions[1].Target == 100, "PID preserves fractional smoothing without filtering targets");
            var csv = Path.Combine(Path.GetTempPath(), "embeddesk-pid-" + Guid.NewGuid().ToString("N") + ".csv");
            try
            {
                await window.ExportCsvAsync(csv, true);
                var lines = await File.ReadAllLinesAsync(csv);
                Require(lines[0] == "seconds,channel,target,actual,filtered_actual" && lines.Length == 10001, "PID exports a bounded snapshot with both raw and filtered columns");
                var rows = lines.Skip(1).Select(line => line.Split(',')).Where(parts => parts[1] == "1").ToArray();
                var expected = PidPlot.ApplySmoothing(raw[0], SignalSmoothingMode.Exponential, 5, .25);
                Require(rows.Select(row => double.Parse(row[3], CultureInfo.InvariantCulture)).SequenceEqual(raw[0].Select(s => s.Actual)) && rows.Select(row => double.Parse(row[4], CultureInfo.InvariantCulture)).SequenceEqual(expected.Select(s => s.Actual)), "PID CSV filters each channel independently and preserves original values");
                await window.ExportCsvAsync(csv, false);
                Require((await File.ReadAllLinesAsync(csv))[0] == "seconds,channel,target,actual", "PID can export only the raw columns");
                var original = await File.ReadAllBytesAsync(csv);
                using var cancelExport = new CancellationTokenSource(); cancelExport.Cancel();
                await window.ExportCsvAsync(csv, true, cancelExport.Token);
                Require(original.SequenceEqual(await File.ReadAllBytesAsync(csv)), "Canceling PID export leaves an existing destination intact");
            }
            finally { File.Delete(csv); }
            ((ComboBox)window.FindName("TimeWindowBox")).SelectedIndex = 0;
            Require(plot.WindowSeconds == .05, "PID can inspect a 50 ms time window");
            plot.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = UIElement.MouseWheelEvent });
            Require(plot.WindowSeconds == .05, "PID wheel zoom respects the 50 ms lower bound");
            ((ComboBox)window.FindName("TimeWindowBox")).SelectedIndex = 4;
            mode.SelectedIndex = 1;
            ((CheckBox)window.FindName("ShowPointsBox")).IsChecked = true;
            await FileWorkspaceChecks.CaptureAsync(window, "embeddesk-pid-debug.png"); window.Width = window.MinWidth; window.Height = window.MinHeight;
            await FileWorkspaceChecks.CaptureAsync(window, "embeddesk-pid-debug-small.png");
            mode.SelectedIndex = 2;
            await FileWorkspaceChecks.CaptureAsync(window, "embeddesk-pid-debug-ema-small.png");
            mode.SelectedIndex = 1;
            Require(plot.InspectAt(new(plot.ActualWidth / 2, plot.ActualHeight / 2)).Contains("原始", StringComparison.Ordinal), "PID cursor inspection reports raw and displayed values");
            Require(Find<ScrollViewer>(window).Where(v => v.IsVisible).All(v => v.ScrollableHeight == 0), "PID parameters and action buttons fit at minimum size without scrolling");
            ((CheckBox)window.FindName("PauseBox")).IsChecked = true;
            await stream.WriteAsync(DebugToolPacketReader.Encode(false, 1, 2, new byte[] { 42, 0, 0, 0 }));
            await Until(() => ((TextBlock)window.FindName("ChannelStatus")).Text.Contains("42", StringComparison.Ordinal));
            Require(true, "Pausing a PID plot keeps receiving actual values");
        }
        finally { await connection.StopAsync(); window.Close(); }
    }

    private static void Configure(DebugToolConnectionPanel connection, IPEndPoint endpoint)
    {
        ((ComboBox)connection.FindName("TransportBox")).SelectedIndex = 1;
        ((TextBox)connection.FindName("NetworkPortBox")).Text = endpoint.Port.ToString(CultureInfo.InvariantCulture);
        ((Button)connection.FindName("ConnectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static async Task<DebugToolPacket> PacketAsync(NetworkStream stream, bool camera)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var head = new byte[10]; await stream.ReadExactlyAsync(head, cancel.Token); var length = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(5));
        Require(length is >= 11 and < 8388609, "Outgoing packet length is bounded");
        var bytes = new byte[length]; head.CopyTo(bytes, 0); await stream.ReadExactlyAsync(bytes.AsMemory(10), cancel.Token);
        DebugToolPacket? packet = null; new DebugToolPacketReader(camera).Feed(bytes, parsed => packet = parsed);
        return packet ?? throw new InvalidOperationException("Outgoing packet checksum invalid");
    }
    private static void Click(Window window, string label) => Find<Button>(window).Single(b => Equals(b.Content, label)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Find<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static async Task Until(Func<bool> condition)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(25, cancel.Token);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); Console.WriteLine("PASS " + message); }
}
