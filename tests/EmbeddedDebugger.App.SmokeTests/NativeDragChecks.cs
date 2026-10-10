using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using EmbeddedDebugger.App.Controls;
using EmbeddedDebugger.App.ViewModels;

namespace EmbeddedDebugger.App.SmokeTests;

// Opt-in interactive smoke checks drive only the test application's own windows.
internal static class NativeDragChecks
{
    public static async Task RunAsync(Window window, RemoteDebugWorkspace managerControl, RemoteDebugWorkspaceViewModel manager, RemoteWorkspace workspace)
    {
        if (Environment.GetEnvironmentVariable("EDBG_DRAG_SMOKE") != "1") return;
        GetCursorPos(out var originalCursor);
        try
        {
            manager.SelectTab(workspace.DataContext);
            window.Activate(); window.UpdateLayout();
            SetForegroundWindow(new WindowInteropHelper(window).Handle);
            await Task.Delay(200);
            var header = (Border)workspace.FindName("DirectoryDragHandle");
            var layout = (Grid)workspace.FindName("DockLayout");
            var start = header.PointToScreen(new Point(header.ActualWidth / 2, 16));
            var end = layout.PointToScreen(new Point(200, 90));
            await DragAsync(start, end);
            var floating = Application.Current.Windows.OfType<FilePaneWindow>().SingleOrDefault();
            Require(floating is not null, "Native mouse drag detaches the file panel");
            var nativeHeader = (Border)workspace.FindName("DirectoryDragHandle");
            var target = layout.PointToScreen(new Point(layout.ActualWidth / 2, layout.ActualHeight - 40));
            await DragAsync(nativeHeader.PointToScreen(new Point(nativeHeader.ActualWidth / 2, 16)), target);
            Require(!Application.Current.Windows.OfType<FilePaneWindow>().Any(), "Native mouse drag merges the file window back at the bottom");

            window.Activate(); window.UpdateLayout();
            var strip = (ItemsControl)managerControl.FindName("TabStrip");
            var tab = manager.Tabs.Last();
            var first = manager.Tabs.First();
            var source = (FrameworkElement)strip.ItemContainerGenerator.ContainerFromItem(tab);
            var destination = (FrameworkElement)strip.ItemContainerGenerator.ContainerFromItem(first);
            await DragAsync(source.PointToScreen(new Point(30, 22)), destination.PointToScreen(new Point(8, 22)));
            Require(ReferenceEquals(manager.Tabs[0], tab), "Native mouse drag changes the top-level tab order");
        }
        finally { mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); SetCursorPos(originalCursor.X, originalCursor.Y); }
    }

    private static async Task DragAsync(Point start, Point end)
    {
        await Task.Run(async () =>
        {
            SetCursorPos((int)start.X, (int)start.Y);
            await Task.Delay(120);
            mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
            try
            {
                await Task.Delay(100);
                for (var i = 1; i <= 24; i++)
                {
                    SetCursorPos((int)(start.X + (end.X - start.X) * i / 24), (int)(start.Y + (end.Y - start.Y) * i / 24));
                    await Task.Delay(30);
                }
                await Task.Delay(120);
            }
            finally { mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); }
        });
        await Task.Delay(250);
    }
    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Console.WriteLine($"PASS {description}");
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
}
