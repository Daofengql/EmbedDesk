using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EmbedDesk.App.Controls;
using EmbedDesk.App.ViewModels;
using Microsoft.Web.WebView2.Wpf;

namespace EmbedDesk.App.SmokeTests;

internal static class RemoteTabsChecks
{
    internal static async Task RunEmptyStateAsync(MainWindowViewModel vm)
    {
        var manager = vm.RemoteDebug!;
        foreach (var shell in manager.LocalShells.ToArray()) await manager.CloseLocalShellAsync(shell);
        var workspace = new RemoteDebugWorkspace { DataContext = manager };
        var window = new Window { Title = "远程调试空状态", Width = 1080, Height = 700, Content = workspace };
        window.Show();
        try
        {
            workspace.UpdateLayout();
            Require(((Button)workspace.FindName("EmptyNewConnectionButton")).IsVisible,
                "Empty remote workspace provides a centered new connection button");
            await FileWorkspaceChecks.CaptureAsync(window, "embeddesk-remote-empty-state.png");
        }
        finally { window.Close(); }
    }

    internal static async Task RunAsync(MainWindowViewModel vm, FakeRemoteDebugService service)
    {
        using var log = new StreamWriter(Path.Combine(Path.GetTempPath(), "embeddesk-remote-tabs-check.txt")) { AutoFlush = true };
        var previousOutput = Console.Out;
        Console.SetOut(log);
        var manager = vm.RemoteDebug!;
        var window = new MainWindow(vm) { Left = 10, Top = 10 };
        window.Show();
        ((TabControl)window.FindName("MainWorkspaceTabs")).SelectedIndex = 1;
        window.UpdateLayout();
        var workspace = Descendants<RemoteDebugWorkspace>(window).Single();
        try
        {
            var first = manager.LocalShell!;
            var firstBrowser = await BrowserAsync(workspace, first);
            var firstCore = firstBrowser.CoreWebView2;
            Require(CloseButton(workspace, first).IsVisible, "Default local Shell has a visible close button");
            ((Button)workspace.FindName("NewLocalShellButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var second = manager.SelectedLocalShell!;
            var secondBrowser = await BrowserAsync(workspace, second);
            Require(first != second && first.IsRunning && second.IsRunning, "Plus opens an independent local process and tab");
            await InputAsync(secondBrowser, "$global:embedDeskTabValue = 'SECOND'; Write-Output ('TAB-' + 'SECOND-READY')\r");
            await Until(() => Received(second).Contains("TAB-SECOND-READY"));
            manager.SelectTab(first);
            await InputAsync(firstBrowser, "$global:embedDeskTabValue = 'FIRST'; Write-Output ('TAB-' + 'FIRST-READY')\r");
            await Until(() => Received(first).Contains("TAB-FIRST-READY"));
            Require(!Received(first).Contains("TAB-SECOND-READY") && !Received(second).Contains("TAB-FIRST-READY"), "WebView input and process output stay within their own Shell");
            await second.SendAsync("Write-Output ('BACKGROUND-' + $global:embedDeskTabValue)\r");
            await Until(() => Received(second).Contains("BACKGROUND-SECOND"));
            manager.MoveTab(second, first, false);
            window.UpdateLayout();
            Require(ReferenceEquals(firstCore, firstBrowser.CoreWebView2) && first.IsRunning, "Reordering headers preserves the existing local terminal");

            var menu = OpenMenu(workspace, second);
            Require(manager.ActiveTab == first, "Opening a background tab menu leaves the active session intact");
            await Task.Delay(100);
            CaptureMenu(menu);
            ClickMenu(menu, "rename");
            await Until(() => Editor(workspace, second).IsVisible);
            var editor = Editor(workspace, second);
            editor.Text = "构建终端";
            Press(editor, Key.Enter);
            Require(second.TabTitle == "构建终端" && first.TabTitle == "本地 Shell", "Context rename affects only the clicked tab");
            menu = OpenMenu(workspace, second); ClickMenu(menu, "rename");
            await Until(() => Editor(workspace, second).IsVisible);
            editor.Text = "不保存"; Press(editor, Key.Escape);
            Require(second.TabTitle == "构建终端", "Escape cancels an inline rename");
            menu = OpenMenu(workspace, second); ClickMenu(menu, "duplicate");
            await Until(() => manager.LocalShells.Count == 3);
            var third = manager.SelectedLocalShell!;
            var thirdBrowser = await BrowserAsync(workspace, third);
            Require(third != first && third != second, "Duplicate Shell creates a new independent session");
            await thirdBrowser.ExecuteScriptAsync("window.chrome.webview.postMessage(JSON.stringify({type:'context',action:'fullscreen'}))");
            await Task.Delay(200);
            // Exercise the same terminal event used by its fullscreen button.
            manager.IsLocalShellFullscreen = true;
            await thirdBrowser.ExecuteScriptAsync("document.getElementById('exit-fullscreen').click()");
            await Until(() => !manager.IsTerminalFullscreen);
            Require(!third.IsFullscreen, "Local terminal can exit fullscreen without toggling back in");
            await thirdBrowser.ExecuteScriptAsync("window.resetTerminal(); terminal.write('Independent local Shell ready.\\r\\n> ')");
            await FileWorkspaceChecks.CaptureAsync(window, "embeddesk-remote-tabs-default.png");
            window.Width = window.MinWidth; window.Height = window.MinHeight;
            await FileWorkspaceChecks.CaptureAsync(window, "embeddesk-remote-tabs-small.png");
            Require(((Button)workspace.FindName("NewLocalShellButton")).IsVisible, "New Shell remains reachable at minimum window size");
            var confirmed = false;
            using (AutoConfirm(false, () => confirmed = true)) CloseButton(workspace, second).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(confirmed && second.IsRunning && manager.LocalShells.Contains(second), "Canceling close keeps the Shell process and tab");
            using (AutoConfirm(true, () => { })) { menu = OpenMenu(workspace, second); ClickMenu(menu, "close"); }
            await Until(() => !manager.LocalShells.Contains(second));
            await second.SendAsync("Write-Output 'must-not-restart'\r");
            Require(second.IsDisposed && !second.IsRunning && manager.ActiveTab == third && first.IsRunning, "Closing a background Shell releases it, prevents restart and preserves the active tab");

            await manager.OpenProfileInNewTabAsync(manager.Profiles.First());
            var session = manager.SelectedSession!;
            manager.SetTabTitle(session, "远程日志");
            menu = OpenMenu(workspace, session); ClickMenu(menu, "duplicate");
            await Until(() => manager.Sessions.Count == 2 && manager.SelectedSession!.IsWorkspaceReady);
            var duplicate = manager.SelectedSession!;
            Require(session != duplicate && !ReferenceEquals(session.Profile, duplicate.Profile) && session.IsConnected
                && session.Profile.Name == manager.Profiles.First().Name, "Duplicate remote tab connects independently without changing saved profiles");
            service.Files["/tab-check.txt"] = Encoding.UTF8.GetBytes("saved\n");
            await session.RefreshDirectoryCommand.ExecuteAsync(null);
            await session.OpenEntryAsync(session.DirectoryEntries.Single(entry => entry.Name == "tab-check.txt"));
            var document = manager.SelectedDocument!;
            document.Text += "unsaved";
            menu = OpenMenu(workspace, document);
            Require(!menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "duplicate")).IsEnabled, "File preview does not offer a second conflicting editor");
            ClickMenu(menu, "rename");
            await Until(() => Editor(workspace, document).IsVisible);
            editor = Editor(workspace, document);
            Require(editor.Text == document.Name, "Rename editor excludes the unsaved marker");
            editor.Text = "配置草稿"; Press(editor, Key.Enter);
            Require(document.TabTitle == "配置草稿 •" && document.RemotePath == "/tab-check.txt", "File tab rename preserves its remote path and unsaved marker");
            var warned = false;
            using (AutoConfirm(false, () => warned = Descendants<TextBlock>(Application.Current.Windows.OfType<ConfirmationWindow>().Single()).Any(t => t.Text.Contains("未保存"))))
                CloseButton(workspace, session).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(warned && session.IsConnected && manager.Documents.Contains(document), "Closing a parent session warns about unsaved file tabs");
            manager.CloseDocument(document);
            await manager.CloseTabAsync(session);
            Require(duplicate.IsConnected && manager.ActiveTab == duplicate, "Closing one remote session keeps its duplicate connected");
            await manager.CloseTabAsync(duplicate);
            await manager.CloseLocalShellAsync(first);
            await manager.CloseLocalShellAsync(third);
            Require(manager.Tabs.Count == 0 && manager.ActiveTab is null && !manager.IsLocalShellSelected, "The last Shell can close into an empty workspace");
            workspace.UpdateLayout();
            Require(((Button)workspace.FindName("EmptyNewConnectionButton")).IsVisible, "Empty remote workspace provides a centered new connection button");
            await FileWorkspaceChecks.CaptureAsync(window, "embeddesk-remote-tabs-empty.png");
            ((Button)workspace.FindName("NewLocalShellButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(manager.LocalShells.Count == 1 && manager.IsLocalShellSelected && manager.LocalShell!.IsRunning, "Plus reopens a Shell after closing every tab");
            Console.WriteLine("PASS remote tab menus and independent local Shell lifecycle");
        }
        finally { window.Close(); Console.SetOut(previousOutput); }
    }

    private static async Task<WebView2> BrowserAsync(RemoteDebugWorkspace workspace, LocalShellViewModel shell)
    {
        workspace.UpdateLayout();
        var terminal = Descendants<ShellTerminal>(workspace).Single(t => t.DataContext == shell);
        var browser = (WebView2)terminal.FindName("Browser");
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (browser.CoreWebView2 is null || await browser.ExecuteScriptAsync("typeof terminal !== 'undefined'") != "true")
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Local terminal initialization timed out");
            await Task.Delay(50);
        }
        return browser;
    }
    private static Task InputAsync(WebView2 browser, string text) => browser.ExecuteScriptAsync($"window.chrome.webview.postMessage({JsonSerializer.Serialize(text)})");
    private static string Received(LocalShellViewModel shell) => string.Concat(shell.Terminal.Entries.Select(entry => Encoding.UTF8.GetString(entry.Data)));
    private static Border Header(RemoteDebugWorkspace workspace, object tab) => Descendants<Border>((DependencyObject)((ItemsControl)workspace.FindName("TabStrip")).ItemContainerGenerator.ContainerFromItem(tab)).Single(b => b.ContextMenu is not null);
    private static Button CloseButton(RemoteDebugWorkspace workspace, object tab) { workspace.UpdateLayout(); return Descendants<Button>(Header(workspace, tab)).Single(b => Equals(b.Tag, "CloseTab")); }
    private static TextBox Editor(RemoteDebugWorkspace workspace, object tab) => Descendants<TextBox>(Header(workspace, tab)).Single();
    private static ContextMenu OpenMenu(RemoteDebugWorkspace workspace, object tab)
    {
        workspace.UpdateLayout();
        var header = Header(workspace, tab);
        header.BringIntoView();
        header.ContextMenu.PlacementTarget = header;
        header.ContextMenu.IsOpen = true;
        return header.ContextMenu;
    }
    private static void ClickMenu(ContextMenu menu, string action)
    {
        menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, action)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.IsOpen = false;
    }
    private static void Press(TextBox editor, Key key) => editor.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor), Environment.TickCount, key) { RoutedEvent = UIElement.PreviewKeyDownEvent });
    private static IDisposable AutoConfirm(bool result, Action seen)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            if (Application.Current.Windows.OfType<ConfirmationWindow>().FirstOrDefault() is not { } dialog) return;
            timer.Stop(); seen(); dialog.DialogResult = result;
        };
        timer.Start(); return new TimerScope(timer);
    }
    private sealed class TimerScope(DispatcherTimer timer) : IDisposable { public void Dispose() => timer.Stop(); }
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("Remote tab check timed out"); await Task.Delay(30); }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
    private static void CaptureMenu(ContextMenu menu)
    {
        menu.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth), (int)Math.Ceiling(menu.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(menu);
        using var stream = File.Create(Path.Combine(Path.GetTempPath(), "embeddesk-remote-tabs-menu.png"));
        new PngBitmapEncoder { Frames = { BitmapFrame.Create(bitmap) } }.Save(stream);
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }
}
