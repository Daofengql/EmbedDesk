using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using EmbedDesk.App.Controls;
using EmbedDesk.App.ViewModels;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace EmbedDesk.App.SmokeTests;

internal static class TerminalRegressionChecks
{
    public static async Task RunAsync()
    {
        var control = new ShellTerminal { Buffer = new TerminalBufferViewModel() };
        var window = new Window { Title = "Terminal regression checks", Content = control, Width = 800, Height = 500 };
        var input = new List<string>();
        TerminalSize? reportedSize = null;
        control.InputReceived += (_, text) => input.Add(text);
        control.ResizeReceived += (_, size) => reportedSize = size;
        window.Show();
        try
        {
            // Output arrives before WebView navigation and while its replay is yielding.
            control.IsOutputVisible = false;
            for (var i = 0; i < 80; i++) control.Buffer.Add(DateTimeOffset.Now, true, System.Text.Encoding.UTF8.GetBytes($"MOTD-{i:000}\r\n"));
            var arrivals = Task.Run(async () =>
            {
                for (var i = 80; i < 160; i++)
                {
                    var line = i;
                    await control.Dispatcher.InvokeAsync(() => control.Buffer.Add(DateTimeOffset.Now, true, System.Text.Encoding.UTF8.GetBytes($"MOTD-{line:000}\r\n")));
                    await Task.Delay(5);
                }
            });
            var browser = (WebView2)control.FindName("Browser");
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (browser.CoreWebView2 is not null && await browser.ExecuteScriptAsync("typeof terminal !== 'undefined' && terminal.rows > 0") == "true") break;
                await Task.Delay(100);
            }
            Require(browser.CoreWebView2 is not null, "Terminal WebView starts");
            await arrivals;
            control.IsOutputVisible = true;
            await Task.Delay(200);
            var actual = JsonSerializer.Deserialize<string>(await browser.ExecuteScriptAsync("Array.from({length:terminal.buffer.active.length},(_,i)=>terminal.buffer.active.getLine(i).translateToString(true)).filter(x=>x.startsWith('MOTD-')).join('\\n')"));
            Require(actual == string.Join('\n', Enumerable.Range(0, 160).Select(i => $"MOTD-{i:000}")), "Hidden initialization and live replay preserve every MOTD line exactly once");
            control.Buffer.Clear(); await Task.Delay(100);
            await browser.ExecuteScriptAsync("window.batchWrites=0; window.originalWrite=window.writeTerminal; window.writeTerminal=data=>{window.batchWrites++;window.originalWrite(data);}");
            for (var i = 0; i < 800; i++) control.Buffer.Add(DateTimeOffset.Now, true, Encoding.UTF8.GetBytes($"BATCH-{i:000}\r\n"));
            await Task.Delay(300);
            var batched = JsonSerializer.Deserialize<string>(await browser.ExecuteScriptAsync("Array.from({length:terminal.buffer.active.length},(_,i)=>terminal.buffer.active.getLine(i).translateToString(true)).filter(x=>x.startsWith('BATCH-')).join('\\n')"));
            Require(batched == string.Join('\n', Enumerable.Range(0, 800).Select(i => $"BATCH-{i:000}")), "Batching preserves the exact order of 800 terminal writes");
            var writes = int.Parse(await browser.ExecuteScriptAsync("window.batchWrites"), System.Globalization.CultureInfo.InvariantCulture);
            Require(writes < 10, $"800 packets use {writes} WebView script calls instead of 800");
            control.Buffer.Add(DateTimeOffset.Now, true, Encoding.UTF8.GetBytes("stale-before-reset"));
            control.Buffer.Clear();
            control.Buffer.Add(DateTimeOffset.Now, true, Encoding.UTF8.GetBytes("fresh-after-reset"));
            await Task.Delay(150);
            var afterReset = await browser.ExecuteScriptAsync("Array.from({length:terminal.buffer.active.length},(_,i)=>terminal.buffer.active.getLine(i).translateToString(true)).join('\\n')");
            Require(afterReset.Contains("fresh-after-reset") && !afterReset.Contains("stale-before-reset"), "Clear invalidates queued terminal output before replay");
            await browser.ExecuteScriptAsync("window.writeTerminal=window.originalWrite");
            control.Buffer.Clear();
            await Task.Delay(100);
            await browser.ExecuteScriptAsync("terminal.focus()");
            await KeyAsync(browser, "Backspace", 8);
            await KeyAsync(browser, "Delete", 46);
            await KeyAsync(browser, "h", 72, modifiers: 2);
            await Task.Delay(100);
            Require(input.SequenceEqual(new[] { "\x7f", "\x1b[3~", "\x08" }), "Backspace sends DEL once; Delete and Ctrl+H keep their own sequences");

            foreach (var size in new[] { (800d, 500d), (560d, 333d), (1000d, 697d) })
            {
                window.Width = size.Item1;
                window.Height = size.Item2;
                await Task.Delay(150);
                await CheckVisibleRowsAsync(browser);
            }
            var oldRows = reportedSize?.Rows;
            await KeyAsync(browser, "+", 187, modifiers: 2);
            await Task.Delay(150);
            await CheckVisibleRowsAsync(browser);
            using (var grid = JsonDocument.Parse(await browser.ExecuteScriptAsync("({cols:terminal.cols, rows:terminal.rows})")))
                Require(reportedSize is not null && reportedSize.Columns == grid.RootElement.GetProperty("cols").GetInt32() && reportedSize.Rows == grid.RootElement.GetProperty("rows").GetInt32() && reportedSize.Rows != oldRows,
                    "Font zoom reports the new terminal size without resizing the window");

            using var beforeZoom = JsonDocument.Parse(await browser.ExecuteScriptAsync("({font:terminal.options.fontSize,cols:terminal.cols,rows:terminal.rows})"));
            await NativeZoomAsync(window, browser, 0xBD);
            using (var afterZoom = JsonDocument.Parse(await browser.ExecuteScriptAsync("({font:terminal.options.fontSize,cols:terminal.cols,rows:terminal.rows})")))
            {
                Require(browser.ZoomFactor == 1 && afterZoom.RootElement.GetProperty("font").GetInt32() < beforeZoom.RootElement.GetProperty("font").GetInt32()
                    && afterZoom.RootElement.GetProperty("cols").GetInt32() > beforeZoom.RootElement.GetProperty("cols").GetInt32()
                    && afterZoom.RootElement.GetProperty("rows").GetInt32() > beforeZoom.RootElement.GetProperty("rows").GetInt32(),
                    "Physical Ctrl+- enlarges the terminal grid and never zooms the embedded browser");
            }
            await CheckVisibleRowsAsync(browser);

            await browser.ExecuteScriptAsync("terminal.reset(); terminal.write('\\x1b['+terminal.rows+';1HLast visible row: abc\\b \\b'); terminal.focus()");
            await Task.Delay(100);
            Require((await browser.ExecuteScriptAsync("terminal.buffer.active.getLine(terminal.rows-1).translateToString(true)")).Contains("Last visible row: ab"), "Server BS/space/BS erase output renders on the last row");
            var screenshot = Path.Combine(Path.GetTempPath(), "embeddesk-terminal-regression.png");
            await using var stream = File.Create(screenshot);
            await browser.CoreWebView2!.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            Console.WriteLine($"TERMINAL SCREENSHOT {screenshot}");
            await CheckLocalTtyAsync(window, control, browser);
        }
        finally { window.Close(); }
    }

    private static async Task CheckLocalTtyAsync(Window window, ShellTerminal control, WebView2 browser)
    {
        await using var shell = new LocalShellViewModel();
        control.DataContext = shell;
        control.Buffer = shell.Terminal;
        async void Send(object? sender, string text) => await shell.SendAsync(text);
        control.InputReceived += Send;
        try
        {
            await shell.StartAsync();
            Require(shell.IsRunning, "Local ConPTY starts for real TTY resize checks");
            await NativeZoomAsync(window, browser, 0xBD);
            using var grid = JsonDocument.Parse(await browser.ExecuteScriptAsync("({cols:terminal.cols,rows:terminal.rows})"));
            var columns = grid.RootElement.GetProperty("cols").GetInt32();
            var rows = grid.RootElement.GetProperty("rows").GetInt32();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            Match? size;
            do
            {
                await Task.Delay(40);
                var text = Encoding.UTF8.GetString(shell.Terminal.Entries.Where(entry => entry.IsReceived).SelectMany(entry => entry.Data).ToArray());
                // ConPTY emits this VT window-size notification after native resizing.
                size = Regex.Matches(text, @"\x1b\[8;(\d+);(\d+)t").LastOrDefault();
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"ConPTY did not acknowledge {columns}x{rows}; last notification: {size?.Value}");
            } while (size is null || int.Parse(size.Groups[1].Value) != rows || int.Parse(size.Groups[2].Value) != columns);
            Require(browser.ZoomFactor == 1, $"Local ConPTY acknowledges {columns} columns x {rows} rows after shrinking the font");
        }
        finally { control.InputReceived -= Send; control.DataContext = null; control.Buffer = null; }
    }

    internal static async Task KeyAsync(WebView2 browser, string key, int virtualKey, int modifiers = 0)
    {
        foreach (var type in new[] { "keyDown", "keyUp" })
            await browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new { type, key, windowsVirtualKeyCode = virtualKey, modifiers }));
    }

    internal static async Task NativeZoomAsync(Window window, WebView2 browser, byte key)
    {
        await FocusTerminalAsync(window, browser);
        var oldFont = int.Parse(await browser.ExecuteScriptAsync("terminal.options.fontSize"));
        var expectedFont = key == 0xBD ? Math.Max(9, oldFont - 1) : key == 0xBB ? Math.Min(24, oldFont + 1) : 14;
        await Task.Run(async () =>
        {
            keybd_event(0x11, (byte)MapVirtualKey(0x11, 0), 0, UIntPtr.Zero);
            try
            {
                keybd_event(key, (byte)MapVirtualKey(key, 0), 0, UIntPtr.Zero);
                await Task.Delay(50);
            }
            finally
            {
                keybd_event(key, (byte)MapVirtualKey(key, 0), 2, UIntPtr.Zero);
                keybd_event(0x11, (byte)MapVirtualKey(0x11, 0), 2, UIntPtr.Zero);
            }
        });
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (await browser.ExecuteScriptAsync($"terminal.options.fontSize === {expectedFont} && terminal.cols === fit.proposeDimensions().cols && terminal.rows === fit.proposeDimensions().rows") != "true")
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Native font shortcut did not resize the terminal; expected font=" + expectedFont
                + "; actual=" + await browser.ExecuteScriptAsync("({font:terminal.options.fontSize,cols:terminal.cols,rows:terminal.rows,fit:fit.proposeDimensions(),focus:document.hasFocus()})"));
            await Task.Delay(40);
        }
        await Task.Delay(150);
    }

    internal static async Task NativeFullscreenAsync(Window window, WebView2 browser)
    {
        await FocusTerminalAsync(window, browser);
        var previous = await browser.ExecuteScriptAsync("document.getElementById('exit-fullscreen').hidden");
        keybd_event(0x7A, (byte)MapVirtualKey(0x7A, 0), 0, UIntPtr.Zero);
        try { await Task.Delay(50); }
        finally { keybd_event(0x7A, (byte)MapVirtualKey(0x7A, 0), 2, UIntPtr.Zero); }
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (await browser.ExecuteScriptAsync("document.getElementById('exit-fullscreen').hidden") == previous)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Native F11 did not change the terminal fullscreen state");
            await Task.Delay(40);
        }
    }

    private static async Task FocusTerminalAsync(Window window, WebView2 browser)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        do
        {
            FocusTestWindow(window);
            browser.Focus();
            await browser.ExecuteScriptAsync("terminal.focus()");
            await Task.Delay(100);
            // WPF logical focus can outlive the native WebView's input focus
            // after window chrome changes. Click the guarded test viewport as a user would.
            if (GetForegroundWindow() == new WindowInteropHelper(window).Handle)
            {
                GetCursorPos(out var previous);
                var point = browser.PointToScreen(new Point(40, 40));
                SetCursorPos((int)point.X, (int)point.Y);
                mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
                mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
                SetCursorPos(previous.X, previous.Y);
                await Task.Delay(100);
            }
            if (GetForegroundWindow() == new WindowInteropHelper(window).Handle
                && await browser.ExecuteScriptAsync("document.hasFocus() && document.activeElement.classList.contains('xterm-helper-textarea')") == "true") return;
        } while (DateTime.UtcNow < deadline);
        throw new TimeoutException("Test terminal could not gain native and DOM input focus");
    }

    private static void FocusTestWindow(Window window)
    {
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var currentThread = GetCurrentThreadId();
        var attached = foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            window.Activate();
            SetForegroundWindow(new WindowInteropHelper(window).Handle);
            System.Windows.Input.Keyboard.ClearFocus();
            window.Focus();
        }
        finally { if (attached) AttachThreadInput(currentThread, foregroundThread, false); }
    }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool enable);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extraInfo);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extraInfo);

    internal static async Task CheckVisibleRowsAsync(WebView2 browser)
    {
        using var result = JsonDocument.Parse(await browser.ExecuteScriptAsync("(() => { const s=document.querySelector('.xterm-screen').getBoundingClientRect(),h=document.getElementById('terminal').getBoundingClientRect();return {bottom:s.bottom,hostBottom:h.bottom,right:s.right,hostRight:h.right,height:innerHeight,rows:terminal.rows}; })()"));
        var bounds = result.RootElement;
        Require(bounds.GetProperty("bottom").GetDouble() <= bounds.GetProperty("hostBottom").GetDouble() + 0.5
            && bounds.GetProperty("hostBottom").GetDouble() <= bounds.GetProperty("height").GetDouble() - 17
            && bounds.GetProperty("right").GetDouble() <= bounds.GetProperty("hostRight").GetDouble() + 0.5,
            $"All {bounds.GetProperty("rows").GetInt32()} terminal rows and columns fit inside the visible area");
    }

    private static void Require(bool value, string description)
    {
        if (!value) throw new InvalidOperationException(description);
        Console.WriteLine($"PASS {description}");
    }
}
