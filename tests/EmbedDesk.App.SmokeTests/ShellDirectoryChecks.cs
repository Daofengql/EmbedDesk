using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using EmbedDesk.App.Controls;
using EmbedDesk.App.ViewModels;
using Microsoft.Web.WebView2.Wpf;

namespace EmbedDesk.App.SmokeTests;

internal static class ShellDirectoryChecks
{
    internal static async Task RunAsync(MainWindowViewModel vm)
    {
        using var log = new StreamWriter(Path.Combine(Path.GetTempPath(), "embeddesk-directory-check.txt")) { AutoFlush = true };
        var previousOutput = Console.Out;
        Console.SetOut(log);
        try { await RunCoreAsync(vm); }
        catch (Exception ex) { Console.WriteLine(ex); throw; }
        finally { Console.SetOut(previousOutput); }
    }

    private static async Task RunCoreAsync(MainWindowViewModel vm)
    {
        using var temporary = new TemporarySettingsDirectory();
        var firstPath = Directory.CreateDirectory(Path.Combine(temporary.DirectoryPath, "目录 A & '$[x]")).FullName;
        var secondPath = Directory.CreateDirectory(Path.Combine(temporary.DirectoryPath, "目录 B")).FullName;
        var originalDirectory = Environment.CurrentDirectory;
        var options = new LocalShellStartupOptions([firstPath, firstPath + Path.DirectorySeparatorChar, secondPath]);
        Require(options.Directories.Count == 2, "Directory arguments are normalized and deduplicated");
        await using (var startup = new RemoteDebugWorkspaceViewModel(new FakeRemoteDebugService(), temporary.DirectoryPath, null, options))
        {
            Require(startup.LocalShells.Count == 2 && startup.LocalShells[0].TabTitle == "目录 A & '$[x]",
                "Startup directories replace the default Shell and use directory names for tabs");
            var startupWorkspace = new RemoteDebugWorkspace { DataContext = startup };
            var startupWindow = new Window { Content = startupWorkspace, Width = 1080, Height = 700 };
            startupWindow.Show();
            try
            {
                startup.SelectTab(startup.LocalShells[0]);
                await BrowserAsync(startupWorkspace, startup.LocalShells[0]);
                Require(startup.LocalShells[0].InitialDirectory == firstPath && startup.LocalShells[0].IsRunning,
                    "Startup Shell exposes the requested directory");
                startup.SelectTab(startup.LocalShells[1]);
                await BrowserAsync(startupWorkspace, startup.LocalShells[1]);
                Require(startup.LocalShells[1].InitialDirectory == secondPath && startup.LocalShells[1].IsRunning,
                    "Second startup Shell exposes its requested directory");
            }
            finally { startupWindow.Close(); }
        }
        var manager = vm.RemoteDebug!;
        var originalShell = manager.LocalShell!;
        var window = new MainWindow(vm);
        window.ShowLocalShellWorkspace();
        window.Show();
        window.UpdateLayout();
        var workspace = (RemoteDebugWorkspace)window.FindName("RemoteArea");
        try
        {
            Require(((TabControl)window.FindName("MainWorkspaceTabs")).SelectedIndex == 1 && workspace.CanAcceptDirectoryDrop,
                "Directory startup selects the local Shell workspace");
            var browser = await BrowserAsync(workspace, originalShell);
            var count = manager.LocalShells.Count;
            PostDrop(new WindowInteropHelper(window).Handle, [firstPath]);
            await Until(() => manager.LocalShells.Count == count + 1);
            var fromWindow = manager.SelectedLocalShell!;
            await CheckDirectoryAsync(fromWindow, firstPath);
            Require(originalShell.IsRunning, "Dropping on the window opens a new tab without replacing the running Shell");
            browser = await BrowserAsync(workspace, fromWindow);
            PostDrop(browser.Handle, [secondPath]);
            await Until(() => manager.LocalShells.Count == count + 2);
            var fromNativeTerminal = manager.SelectedLocalShell!;
            await CheckDirectoryAsync(fromNativeTerminal, secondPath);
            browser = await BrowserAsync(workspace, fromNativeTerminal);
            foreach (var type in new[] { "dragEnter", "dragOver", "drop" })
                await browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchDragEvent", JsonSerializer.Serialize(new
                {
                    type, x = 30, y = 30,
                    data = new { items = Array.Empty<object>(), files = new[] { firstPath }, dragOperationsMask = 1 }
                }));
            await Until(() => manager.LocalShells.Count == count + 3);
            var fromBrowser = manager.SelectedLocalShell!;
            await CheckDirectoryAsync(fromBrowser, firstPath);
            Require(originalShell.IsRunning && fromWindow.IsRunning && fromNativeTerminal.IsRunning,
                "Native and WebView drops preserve existing sessions");
            browser = await BrowserAsync(workspace, fromBrowser);
            var file = Path.Combine(temporary.DirectoryPath, "not-a-directory.txt");
            File.WriteAllText(file, "test");
            workspace.OpenDroppedDirectories([file]);
            workspace.OpenDroppedDirectories([firstPath, Path.Combine(temporary.DirectoryPath, "missing")]);
            Require(manager.LocalShells.Count == count + 3 && manager.Status.Contains("失败"),
                "Files and mixed invalid batches are rejected without opening partial sessions");
            ((TabControl)window.FindName("MainWorkspaceTabs")).SelectedIndex = 0;
            window.UpdateLayout();
            PostDrop(new WindowInteropHelper(window).Handle, [secondPath]);
            await Task.Delay(150);
            Require(manager.LocalShells.Count == count + 3 && !workspace.CanAcceptDirectoryDrop,
                "Other workspaces do not accept local Shell directory drops");
            window.ShowLocalShellWorkspace();
            await manager.DuplicateTabAsync(fromBrowser);
            await CheckDirectoryAsync(manager.SelectedLocalShell!, firstPath);
            Require(Environment.CurrentDirectory == originalDirectory, "Directory shells never change the application's process-wide working directory");
            browser = await BrowserAsync(workspace, manager.SelectedLocalShell!);
            await Task.Delay(200);
            await Task.Delay(200);
            await FileWorkspaceChecks.CaptureAsync(window, "embeddesk-directory-shell-default.png");
            window.Width = window.MinWidth; window.Height = window.MinHeight;
            await FileWorkspaceChecks.CaptureAsync(window, "embeddesk-directory-shell-minimum.png");
            Require(!Received(manager.SelectedLocalShell!).Contains("初始化失败"), "Native drop targets initialize successfully");
        }
        finally { window.Close(); }
    }

    private static async Task CheckDirectoryAsync(LocalShellViewModel shell, string expected)
    {
        await Task.Delay(100);
        Require(shell.InitialDirectory == expected && shell.IsRunning, "A real ConPTY PowerShell starts in the exact requested directory");
    }

    private static async Task<WebView2> BrowserAsync(RemoteDebugWorkspace workspace, LocalShellViewModel shell)
    {
        workspace.UpdateLayout();
        var terminal = Descendants<ShellTerminal>(workspace).Single(control => control.DataContext == shell);
        var browser = (WebView2)terminal.FindName("Browser");
        await Until(() => browser.CoreWebView2 is not null);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (await browser.ExecuteScriptAsync("typeof terminal !== 'undefined' && window.directoryDropEnabled === true") != "true")
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Directory drop terminal initialization timed out");
            await Task.Delay(50);
        }
        return browser;
    }

    private static void PostDrop(IntPtr target, string[] paths)
    {
        var text = Encoding.Unicode.GetBytes(string.Join('\0', paths) + "\0\0");
        var memory = GlobalAlloc(0x42, (nuint)(20 + text.Length));
        if (memory == IntPtr.Zero) throw new InvalidOperationException("HDROP allocation failed");
        var pointer = GlobalLock(memory);
        if (pointer == IntPtr.Zero) { GlobalFree(memory); throw new InvalidOperationException("HDROP lock failed"); }
        Marshal.WriteInt32(pointer, 0, 20); // DROPFILES.pFiles
        Marshal.WriteInt32(pointer, 16, 1); // DROPFILES.fWide
        Marshal.Copy(text, 0, pointer + 20, text.Length);
        GlobalUnlock(memory);
        if (!PostMessage(target, 0x0233, memory, IntPtr.Zero))
        { GlobalFree(memory); throw new InvalidOperationException("HDROP delivery failed"); }
        // The receiving DragFinish owns and releases memory after successful delivery.
    }

    private static string Received(LocalShellViewModel shell) => string.Concat(shell.Terminal.Entries.Select(entry => Encoding.UTF8.GetString(entry.Data)));
    private static async Task Until(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!predicate()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("Directory Shell check timed out"); await Task.Delay(50); }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
