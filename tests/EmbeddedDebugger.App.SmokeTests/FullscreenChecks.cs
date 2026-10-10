using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EmbeddedDebugger.App.Controls;
using EmbeddedDebugger.App.ViewModels;
using Microsoft.Web.WebView2.Wpf;

namespace EmbeddedDebugger.App.SmokeTests;

internal static class FullscreenChecks
{
    internal static async Task RunAsync(MainWindow window, RemoteDebugWorkspace managerControl,
        RemoteDebugWorkspaceViewModel manager, RemoteWorkspace remoteControl, FakeRemoteDebugService service)
    {
        var session = manager.SelectedSession!;
        var terminal = (ShellTerminal)remoteControl.FindName("TerminalControl");
        var browser = (WebView2)terminal.FindName("Browser");
        await WaitForBrowserAsync(browser);
        var originalCore = browser.CoreWebView2;
        var savedBounds = new Rect(window.Left, window.Top, window.Width, window.Height);
        var sidebarWidth = manager.SessionPaneWidth;
        var filesExpanded = session.IsDirectoryPaneExpanded;
        var edgeToggle = (Button)managerControl.FindName("SessionPaneToggle");
        var surface = (Grid)window.FindName("ApplicationSurface");
        session.Terminal.Add(DateTimeOffset.Now, true, Encoding.UTF8.GetBytes("FULLSCREEN-CONTENT-PRESERVED\r\n"));
        await Task.Delay(150);
        var originalSize = service.LastResize!.Value;

        foreach (var expanded in new[] { false, true })
        {
            manager.IsSessionPaneExpanded = expanded;
            session.IsDirectoryPaneExpanded = expanded;
            window.UpdateLayout();
            var fullscreenButton = Descendants<Button>(remoteControl).Single(button => Equals(button.ToolTip, "终端全屏"));
            fullscreenButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await SettleAsync(window);
            Require(manager.IsTerminalFullscreen && terminal.IsFullscreen && window.WindowStyle == WindowStyle.None
                && window.WindowState == WindowState.Maximized, "SSH toolbar enters full-screen mode on the existing window");
            Require(!edgeToggle.IsVisible && !((Button)remoteControl.FindName("DirectoryRestore")).IsVisible,
                "Fullscreen removes both sidebar rails");
            RequireFillsSurface(terminal, surface);
            await RequireRemoteSizeAsync(browser, service);
            Require(service.LastResize!.Value.Columns > originalSize.Columns && service.LastResize.Value.Rows > originalSize.Rows,
                "Full-screen dimensions reach the SSH PTY");
            Require(await browser.ExecuteScriptAsync("document.getElementById('exit-fullscreen').hidden") == "false",
                "The terminal exposes an in-page exit button in fullscreen");
            if (expanded) await FileWorkspaceChecks.CaptureAsync(window, "embedded-debugger-ssh-fullscreen.png");
            await browser.ExecuteScriptAsync("document.getElementById('exit-fullscreen').click()");
            await SettleAsync(window);
            Require(!manager.IsTerminalFullscreen && window.WindowStyle == WindowStyle.SingleBorderWindow
                && window.WindowState == WindowState.Normal, "The in-terminal exit button restores the normal window");
            Require(new Rect(window.Left, window.Top, window.Width, window.Height) == savedBounds,
                "Exiting fullscreen restores the window's original position and dimensions");
            Require(manager.IsSessionPaneExpanded == expanded && session.IsDirectoryPaneExpanded == expanded,
                "Fullscreen preserves both collapsed and expanded pane states");
            Require(!expanded || manager.SessionPaneWidth == sidebarWidth, "Fullscreen preserves sidebar width");
            await RequireRemoteSizeAsync(browser, service);
            Require(ReferenceEquals(browser.CoreWebView2, originalCore) && session.IsConnected
                && (await browser.ExecuteScriptAsync("Array.from({length:terminal.buffer.active.length},(_,i)=>terminal.buffer.active.getLine(i).translateToString(true)).join('\\n')")).Contains("FULLSCREEN-CONTENT-PRESERVED"),
                "Fullscreen keeps the existing SSH terminal, connection and output");
        }

        await browser.ExecuteScriptAsync("terminal.focus()");
        var transitions = new List<bool>();
        System.ComponentModel.PropertyChangedEventHandler observe = (_, e) => { if (e.PropertyName == nameof(manager.IsTerminalFullscreen)) transitions.Add(manager.IsTerminalFullscreen); };
        manager.PropertyChanged += observe;
        await TerminalRegressionChecks.NativeFullscreenAsync(window, browser);
        await SettleAsync(window);
        manager.PropertyChanged -= observe;
        Require(manager.IsTerminalFullscreen, "Physical F11 enters SSH fullscreen; transitions=" + string.Join(',', transitions));
        await TerminalRegressionChecks.NativeFullscreenAsync(window, browser);
        await SettleAsync(window);
        Require(!manager.IsTerminalFullscreen, "Physical F11 exits SSH fullscreen without a second browser toggle");

        manager.SelectLocalShellCommand.Execute(null);
        await SettleAsync(window);
        var localTerminal = Descendants<ShellTerminal>(managerControl).Single(item => item.DataContext == manager.LocalShell);
        var localBrowser = (WebView2)localTerminal.FindName("Browser");
        await WaitForBrowserAsync(localBrowser);
        var localCore = localBrowser.CoreWebView2;
        window.WindowState = WindowState.Maximized;
        await SettleAsync(window);
        await localBrowser.ExecuteScriptAsync("terminal.focus()");
        await TerminalRegressionChecks.KeyAsync(localBrowser, "F11", 122);
        await SettleAsync(window);
        Require(manager.IsTerminalFullscreen && manager.LocalShell.IsFullscreen, "F11 enters local Shell fullscreen");
        RequireFillsSurface(localTerminal, surface);
        await TerminalRegressionChecks.CheckVisibleRowsAsync(localBrowser);
        await FileWorkspaceChecks.CaptureAsync(window, "embedded-debugger-local-fullscreen.png");
        await TerminalRegressionChecks.KeyAsync(localBrowser, "F11", 122);
        await SettleAsync(window);
        Require(!manager.IsTerminalFullscreen && !manager.LocalShell.IsFullscreen && window.WindowState == WindowState.Maximized
            && window.WindowStyle == WindowStyle.SingleBorderWindow && ReferenceEquals(localBrowser.CoreWebView2, localCore),
            "Exiting local fullscreen restores an originally maximized window without replacing its terminal");
        window.WindowState = WindowState.Normal;
        manager.SelectSessionCommand.Execute(session);
        manager.IsSessionPaneExpanded = true;
        session.IsDirectoryPaneExpanded = filesExpanded;
        await SettleAsync(window);
        var rail = (ColumnDefinition)managerControl.FindName("SessionRailColumn");
        Require(rail.ActualWidth == 20 && edgeToggle.ActualHeight == 72 && edgeToggle.VerticalAlignment == VerticalAlignment.Center,
            "The session toggle sits in the middle of the sidebar edge");
        await FileWorkspaceChecks.CaptureAsync(window, "embedded-debugger-session-edge.png");
        session.IsTerminalFullscreen = true;
        await session.DisconnectCommand.ExecuteAsync(null);
        await SettleAsync(window);
        Require(!manager.IsTerminalFullscreen && !session.IsTerminalFullscreen && window.WindowState == WindowState.Normal,
            "Disconnecting SSH exits fullscreen so connection details remain accessible");
        await session.ConnectCommand.ExecuteAsync(null);
        session.IsTerminalFullscreen = true;
        manager.SelectLocalShellCommand.Execute(null);
        await SettleAsync(window);
        Require(!session.IsTerminalFullscreen && !manager.IsTerminalFullscreen, "Changing the active terminal clears the previous session's fullscreen state");
        manager.IsLocalShellFullscreen = true;
        manager.SelectSessionCommand.Execute(session);
        await SettleAsync(window);
        Require(!manager.IsTerminalFullscreen && !manager.LocalShell.IsFullscreen, "Returning to SSH clears local fullscreen state consistently");
        session.Terminal.Clear();
        await Task.Delay(100);
    }

    private static void RequireFillsSurface(FrameworkElement terminal, FrameworkElement surface)
    {
        var origin = terminal.TranslatePoint(new Point(), surface);
        Require(Math.Abs(origin.X) < 1 && Math.Abs(origin.Y) < 1
            && Math.Abs(terminal.ActualWidth - surface.ActualWidth) < 1
            && Math.Abs(terminal.ActualHeight - surface.ActualHeight) < 1,
            $"Terminal fills the entire application surface ({terminal.ActualWidth:0} × {terminal.ActualHeight:0})");
    }

    private static async Task RequireRemoteSizeAsync(WebView2 browser, FakeRemoteDebugService service)
    {
        var size = JsonSerializer.Deserialize<int[]>(await browser.ExecuteScriptAsync("[terminal.cols,terminal.rows]"))!;
        Require(service.LastResize == (size[0], size[1]), "SSH PTY matches the visible terminal grid after fullscreen changes");
        await TerminalRegressionChecks.CheckVisibleRowsAsync(browser);
    }

    private static async Task WaitForBrowserAsync(WebView2 browser)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (browser.CoreWebView2 is not null && await browser.ExecuteScriptAsync("typeof terminal !== 'undefined' && terminal.rows > 0") == "true") return;
            await Task.Delay(100);
        }
        throw new InvalidOperationException("Fullscreen test terminal did not initialize");
    }

    private static async Task SettleAsync(Window window) { window.UpdateLayout(); await Task.Delay(300); }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Console.WriteLine($"PASS {description}");
    }
}
