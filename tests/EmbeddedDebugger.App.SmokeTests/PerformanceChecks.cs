using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using EmbeddedDebugger.App.Controls;
using EmbeddedDebugger.App.ViewModels;

namespace EmbeddedDebugger.App.SmokeTests;

internal static class PerformanceChecks
{
    public static async Task RunAsync(MainWindowViewModel vm)
    {
        var cryptoStart = Stopwatch.GetTimestamp();
        var crypto = System.Reflection.Assembly.Load("BouncyCastle.Cryptography");
        Console.WriteLine($"METRIC crypto first load: {Stopwatch.GetElapsedTime(cryptoStart).TotalMilliseconds:0.00} ms; {crypto.GetName().Version}");
        var bytes = Enumerable.Range(0, 16 * 1024).Select(i => (byte)i).ToArray();
        var entry = new TrafficEntryViewModel(DateTimeOffset.Now, true, bytes);
        _ = entry.DisplayHex;
        Measure("HEX 16 KiB x 100", () => { for (var i = 0; i < 100; i++) _ = entry.DisplayHex; });
        var buffer = new TerminalBufferViewModel();
        var payload = new byte[64 * 1024];
        Measure("History 64 KiB x 1000", () => { for (var i = 0; i < 1000; i++) buffer.Add(DateTimeOffset.Now, true, payload); });
        Console.WriteLine($"METRIC retained history: {buffer.Entries.Sum(e => (long)e.Data.Length):N0} bytes");
        await vm.InitializeAsync();
        var window = new MainWindow(vm) { Width = 1200, Height = 800 }; window.Show();
        try
        {
            await Task.Delay(150);
            var serial = Find<SerialWorkspace>(window)!;
            var text = (TextBox)serial.FindName("ReceiveText");
            var packet = Encoding.UTF8.GetBytes(new string('x', 200) + "\r\n");
            var terminal = vm.SelectedSerialSession!.Terminal;
            var changes = 0;
            text.TextChanged += (_, _) => changes++;
            var burstStart = Stopwatch.GetTimestamp();
            Measure("Serial burst 300 x 202 B", () => { for (var i = 0; i < 300; i++) terminal.Add(DateTimeOffset.Now, true, packet); });
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (text.Text.Count(c => c == 'x') != 60000) { if (DateTime.UtcNow > deadline) throw new TimeoutException("Serial burst did not finish rendering."); await Task.Delay(10); }
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            if (!text.Text.Contains(new string('x', 200), StringComparison.Ordinal)) throw new InvalidOperationException("Serial text did not render.");
            Console.WriteLine($"METRIC serial burst through UI idle: {Stopwatch.GetElapsedTime(burstStart).TotalMilliseconds:0.00} ms");
            Console.WriteLine($"METRIC serial TextChanged events: {changes}");
            await CheckReceiveRenderingAsync(vm, serial, terminal, text);
        }
        finally { window.Close(); }
    }

    private static async Task CheckReceiveRenderingAsync(MainWindowViewModel vm, SerialWorkspace serial, TerminalBufferViewModel buffer, TextBox text)
    {
        if (buffer.ReceivedBytes != 60600) throw new InvalidOperationException("Batched rendering changed byte counters.");
        ((CheckBox)serial.FindName("ShowTime")).IsChecked = false;
        ((CheckBox)serial.FindName("ShowSent")).IsChecked = false;
        buffer.Clear();
        foreach (var value in Encoding.UTF8.GetBytes("拆包中文\r\n")) buffer.Add(DateTimeOffset.Now, true, new[] { value });
        await Task.Delay(120);
        if (text.Text != "拆包中文\r\n") throw new InvalidOperationException("Incremental receive decoding lost split UTF-8 characters.");
        buffer.Clear(); await Task.Delay(100);
        if (text.Text.Length != 0 || buffer.ReceivedBytes != 0) throw new InvalidOperationException("Clear did not reset batched output.");
        vm.SelectedSerialSession!.ShellMode = true; await Task.Delay(50);
        var changes = 0; text.TextChanged += (_, _) => changes++;
        buffer.Add(DateTimeOffset.Now, true, Encoding.UTF8.GetBytes("hidden-view-output"));
        await Task.Delay(100);
        if (changes != 0) throw new InvalidOperationException("Hidden receive pane still renders traffic.");
        vm.SelectedSerialSession!.ShellMode = false; await Task.Delay(150);
        if (text.Text != "hidden-view-output") throw new InvalidOperationException("Restored receive pane did not replay current history.");
        var large = new TerminalBufferViewModel();
        for (var i = 0; i < 100; i++) large.Add(DateTimeOffset.Now, true, new byte[65536]);
        if (large.Entries.Sum(e => e.Data.Length) > TerminalBufferViewModel.MaxHistoryBytes || large.ReceivedBytes != 6553600)
            throw new InvalidOperationException("Byte-bounded history changed total traffic counters.");
        Console.WriteLine("PASS Batched receive handles split UTF-8, clear, hidden panes and bounded history without changing byte counters");
    }

    private static void Measure(string name, Action action)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp(); action();
        Console.WriteLine($"METRIC {name}: {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.00} ms; {GC.GetAllocatedBytesForCurrentThread() - allocated:N0} allocated bytes");
    }
    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) return match;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            if (Find<T>(System.Windows.Media.VisualTreeHelper.GetChild(root, i)) is { } child) return child;
        return null;
    }
}
