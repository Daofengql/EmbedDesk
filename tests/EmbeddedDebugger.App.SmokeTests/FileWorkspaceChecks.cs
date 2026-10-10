using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EmbeddedDebugger.App.Controls;
using EmbeddedDebugger.App.ViewModels;
using EmbeddedDebugger.Core.Models;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace EmbeddedDebugger.App.SmokeTests;

internal static class FileWorkspaceChecks
{
    public static async Task RunAsync(Window window, RemoteDebugWorkspace managerControl, RemoteDebugWorkspaceViewModel manager, RemoteWorkspace control, FakeRemoteDebugService service)
    {
        var session = manager.SelectedSession!;
        var column = (ColumnDefinition)managerControl.FindName("SessionColumn");
        var splitter = Descendants<GridSplitter>(managerControl).First(item => Equals(item.ToolTip, "拖动调整会话栏宽度"));
        Drag(splitter, -45, 0);
        window.UpdateLayout();
        Require(column.ActualWidth < 236 && column.ActualWidth >= 160, "Session sidebar can be resized with its splitter");
        var savedWidth = column.ActualWidth;
        manager.ToggleSessionPaneCommand.Execute(null); window.UpdateLayout();
        Require(column.ActualWidth == 0 && ((ColumnDefinition)managerControl.FindName("SessionRailColumn")).ActualWidth == 20, "Collapsed session sidebar keeps only its narrow edge toggle rail");
        manager.ToggleSessionPaneCommand.Execute(null); window.UpdateLayout();
        Require(Math.Abs(column.ActualWidth - savedWidth) < 1, "Restoring the sidebar preserves its dragged width");
        Drag(splitter, 500, 0); window.UpdateLayout();
        Require(column.ActualWidth <= 236, "Session sidebar cannot grow beyond its original width");

        var localDirectory = Path.Combine(Path.GetTempPath(), "embedded-debugger-file-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(localDirectory);
        await File.WriteAllTextAsync(Path.Combine(localDirectory, "local-config.json"), "{\"enabled\": true}");
        try
        {
            var encoded = new UTF8Encoding(true);
            service.Files["/config.txt"] = encoded.GetPreamble().Concat(encoded.GetBytes("first line\r\n第二行\r\n")).ToArray();
            service.Files["/binary.dat"] = [0, 1, 2, 3];
            service.Files["/preview.png"] = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");
            service.Files["/logs/status.log"] = Encoding.UTF8.GetBytes("ready\n");
            await session.RefreshDirectoryCommand.ExecuteAsync(null);
            await session.RefreshLocalDirectoryAsync(localDirectory);
            var header = (Border)control.FindName("DirectoryDragHandle");
            await Task.Delay(150); window.UpdateLayout();
            var files = (Border)control.FindName("DirectoryPane");
            var shell = (Border)control.FindName("ShellPane");
            var local = (Grid)control.FindName("LocalPane");
            Require(Grid.GetRow(files) == 2 && Grid.GetRow(shell) == 0, "Files are fixed below a full-width terminal by default");
            Require(local.Visibility == Visibility.Visible, "Wide bottom dock shows local files on the left");
            var fileSplitter = (GridSplitter)control.FindName("DirectorySplitter");
            var oldHeight = files.ActualHeight;
            Drag(fileSplitter, 0, -25); window.UpdateLayout();
            Require(files.ActualHeight > oldHeight, "Bottom file panel height can be dragged");
            var browser = Descendants<WebView2>(control).Single();
            for (var attempt = 0; attempt < 50; attempt++)
            {
                if (browser.CoreWebView2 is not null && await browser.ExecuteScriptAsync("typeof terminal !== 'undefined'") == "true") break;
                await Task.Delay(100);
            }
            Require(await browser.ExecuteScriptAsync("typeof terminal !== 'undefined'") == "true", "Terminal stays initialized after docking");
            session.Terminal.Add(DateTimeOffset.Now, true, Encoding.UTF8.GetBytes("\x1b[32mConnected to regression host\x1b[0m\r\nuser@debian:~$ ls\r\nconfig.txt  logs  preview.png\r\nuser@debian:~$ "));
            await Task.Delay(150);
            Require((await browser.ExecuteScriptAsync("terminal.buffer.active.getLine(0).translateToString(true)")).Contains("Connected"), "Terminal output still renders in the bottom-docked layout");
            await CaptureAsync(window, "embedded-debugger-files-bottom.png");
            session.ToggleDirectoryPaneCommand.Execute(null); window.UpdateLayout();
            var restore = (Button)control.FindName("DirectoryRestore");
            Require(Grid.GetRow(restore) == 1 && restore.ActualHeight == 22, "Bottom file panel keeps a restore rail after collapse");
            restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
            Require(session.IsDirectoryPaneExpanded && Equals(restore.ToolTip, "收起目录"), "Center toggle reopens the directory and remains available to collapse it");
            Require(!Descendants<Button>(header).Any(), "File header has no separate detach or collapse buttons");
            var previousPath = session.Path;
            var selectedFile = session.DirectoryEntries.First();
            session.SelectedEntry = selectedFile;
            ((MenuItem)header.ContextMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Task.Delay(100); window.UpdateLayout();
            var floating = Application.Current.Windows.OfType<FilePaneWindow>().Single();
            Require(ReferenceEquals(floating.Content, files) && files.DataContext == session, "Detaching moves the existing file panel into an independent window");
            Require(session.Path == previousPath && session.SelectedEntry == selectedFile && local.IsVisible, "Detached file panel preserves paths, selection and both file lists");
            floating.Width = 580; floating.UpdateLayout();
            Require(local.Visibility == Visibility.Collapsed, "Narrow detached windows show only the remote file list");
            floating.Width = 940; floating.UpdateLayout();
            Require(local.Visibility == Visibility.Visible, "Widening the detached window restores local files");
            await CaptureAsync(floating, "embedded-debugger-files-window.png");
            session.IsTerminalFullscreen = true; await Task.Delay(150);
            Require(!floating.IsVisible, "Detached files are hidden while the terminal occupies the full screen");
            session.IsTerminalFullscreen = false; await Task.Delay(150);
            Require(floating.IsVisible && ReferenceEquals(floating.Content, files) && session.Path == previousPath,
                "Exiting fullscreen restores the existing detached file window and its location");
            var layout = (Grid)control.FindName("DockLayout");
            var outside = layout.PointToScreen(new Point(-80, -80));
            floating.CompleteMove(outside); await Task.Delay(50);
            Require(floating.IsVisible, "Dropping outside the bottom docking zone leaves the file window independent");
            var dockPoint = layout.PointToScreen(new Point(layout.ActualWidth / 2, layout.ActualHeight - 35));
            floating.UpdateMove(dockPoint);
            Require(((Popup)control.FindName("DockHint")).IsOpen, "Dragging over the main window bottom shows a merge hint");
            floating.CompleteMove(dockPoint); await Task.Delay(100); window.UpdateLayout();
            Require(!Application.Current.Windows.OfType<FilePaneWindow>().Any() && layout.Children.Contains(files) && session.SelectedEntry == selectedFile, "Releasing over the bottom merges the same panel without losing selection");
            control.DetachFilePane(); await Task.Delay(50);
            Application.Current.Windows.OfType<FilePaneWindow>().Single().Close(); window.UpdateLayout();
            Require(layout.Children.Contains(files) && files.IsVisible, "Closing a detached window returns files to the bottom");
            var remoteGrid = (DataGrid)control.FindName("RemoteFiles");
            Require(remoteGrid.ContextMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "上传文件…")), "File operations are available in the context menu");
            Require(!Descendants<Button>(control).Any(button => Equals(button.ToolTip, "上传文件")), "File panel has no separate operation button rows");

            await session.OpenEntryAsync(new RemoteDirectoryEntryViewModel(new("logs", "/logs", true, 0, null, "")));
            Require(session.DirectoryEntries[0].Name == "..", "Remote directories provide a parent entry");
            await session.OpenEntryAsync(session.DirectoryEntries[0]);
            Require(session.Path == "/", "Double-clicking the parent entry navigates back");

            var textEntry = session.DirectoryEntries.Single(item => item.Name == "config.txt");
            await session.OpenEntryAsync(textEntry); window.UpdateLayout();
            var document = manager.SelectedDocument!;
            Require(document is { IsText: true } && document.Text.Contains("第二行") && manager.IsDocumentSelected && !manager.IsRemoteSessionSelected, "Small remote text opens in a new top-level editor tab");
            document.Text += "changed\r\n";
            manager.MoveTab(document, manager.LocalShell, false); window.UpdateLayout();
            Require(manager.Tabs[0] == document && manager.ActiveTab == document && document.IsDirty, "File tabs can move before the local shell without losing edits or selection");
            manager.MoveTab(session, document, false); window.UpdateLayout();
            Require(manager.Tabs[0] == session && manager.Tabs[1] == document && session.IsConnected, "SSH tabs can be reordered together with file tabs without reconnecting");
            manager.SelectSessionCommand.Execute(session);
            window.UpdateLayout();
            Require(Descendants<RemoteWorkspace>(managerControl).Single(item => item.DataContext == session) == control, "Selecting or reordering tabs preserves the terminal control");
            await session.OpenEntryAsync(textEntry);
            Require(manager.SelectedDocument == document && document.IsDirty && service.PreviewDownloads.Count(path => path == "/config.txt") == 1, "Reopening a file reuses its tab and keeps unsaved edits");
            await document.SaveCommand.ExecuteAsync(null);
            Require(!document.IsDirty && service.Files["/config.txt"].SequenceEqual(encoded.GetPreamble().Concat(encoded.GetBytes(document.Text))), "Saving writes back to the same remote path and preserves UTF-8 BOM and line endings");
            document.Text += "unsaved";
            service.FailUpload = true;
            await document.SaveCommand.ExecuteAsync(null);
            Require(document.IsDirty && document.Status.StartsWith("保存失败"), "Failed saves retain unsaved text");
            service.FailUpload = false;
            await CaptureAsync(window, "embedded-debugger-file-editor.png");
            var cache = document.CachePath;
            manager.CloseDocument(document);
            Require(!File.Exists(cache) && manager.SelectedSession == session && !manager.IsDocumentSelected, "Closing a document removes its cache and returns to its session");
            Require(!manager.Tabs.Contains(document), "Closing a reordered file tab removes its header");

            var count = service.PreviewDownloads.Count;
            await session.OpenEntryAsync(new RemoteDirectoryEntryViewModel(new("large.txt", "/large.txt", false, session.PreviewSizeLimitBytes + 1, null, "")));
            Require(service.PreviewDownloads.Count == count && session.FileStatus.Contains("预览上限"), "Oversized files are rejected before downloading");
            await session.OpenEntryAsync(session.DirectoryEntries.Single(item => item.Name == "binary.dat"));
            Require(manager.Documents.Count == 0 && session.FileStatus.StartsWith("无法预览"), "Binary files are not opened as editable text");
            await session.OpenEntryAsync(session.DirectoryEntries.Single(item => item.Name == "preview.png"));
            Require(manager.SelectedDocument is { IsImage: true, IsText: false }, "Small images open in a read-only preview tab");
            manager.CloseDocument(manager.SelectedDocument!);
            await NativeDragChecks.RunAsync(window, managerControl, manager, control);
            await manager.OpenProfileCommand.ExecuteAsync(null);
            var closingSession = manager.SelectedSession!;
            service.PreviewDelayMs = 150;
            var pendingPreview = closingSession.OpenEntryAsync(closingSession.DirectoryEntries.Single(item => item.Name == "config.txt"));
            await manager.CloseSessionCommand.ExecuteAsync(closingSession);
            await pendingPreview;
            service.PreviewDelayMs = 0;
            Require(!manager.Documents.Any(document => document.Owner == closingSession), "Closing a session cancels pending previews without leaving orphan file tabs");
            manager.SelectSessionCommand.Execute(session);
        }
        finally
        {
            service.Files.Clear();
            File.Delete(Path.Combine(localDirectory, "local-config.json"));
            Directory.Delete(localDirectory);
        }
    }

    private static void Drag(GridSplitter splitter, double horizontal, double vertical)
    {
        splitter.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        splitter.RaiseEvent(new DragDeltaEventArgs(horizontal, vertical) { RoutedEvent = Thumb.DragDeltaEvent });
        splitter.RaiseEvent(new DragCompletedEventArgs(horizontal, vertical, false) { RoutedEvent = Thumb.DragCompletedEvent });
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
    internal static async Task CaptureAsync(Window window, string name)
    {
        window.UpdateLayout(); await Task.Delay(150);
        var width = (int)window.ActualWidth; var height = (int)window.ActualHeight;
        var surface = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        surface.Render(window);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(surface, new Rect(0, 0, width, height));
            foreach (var browser in Descendants<WebView2>(window).Where(browser => browser.IsVisible && browser.CoreWebView2 is not null))
            {
                using var stream = new MemoryStream();
                await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
                stream.Position = 0;
                var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var origin = browser.TransformToAncestor(window).Transform(new Point());
                drawing.DrawImage(bitmap, new Rect(origin, browser.RenderSize));
            }
        }
        var output = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); output.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(output));
        var path = Path.Combine(Path.GetTempPath(), name); using var file = File.Create(path); encoder.Save(file);
        Console.WriteLine($"UI SCREENSHOT {path}");
    }
    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Console.WriteLine($"PASS {description}");
    }
}
