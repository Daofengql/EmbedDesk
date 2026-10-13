using System.ComponentModel;
using System.Runtime.InteropServices;

namespace EmbedDesk.App;

/// <summary>Shell file drops also work from an unelevated Explorer into our elevated window.</summary>
internal sealed class NativeDirectoryDropTarget : IDisposable
{
    private const uint WmDropFiles = 0x0233, WmCopyGlobalData = 0x0049, WmNcDestroy = 0x0082;
    private readonly HashSet<IntPtr> _windows = [];
    private readonly SubclassProcedure _procedure;
    private readonly Action<string[]> _drop;
    private readonly Action<string> _error;
    private bool _enabled, _disposed;

    internal NativeDirectoryDropTarget(Action<string[]> drop, Action<string> error)
    {
        _drop = drop;
        _error = error;
        _procedure = WindowProcedure;
    }

    internal void Attach(IntPtr window, bool includeChildren = false)
    {
        if (_disposed || window == IntPtr.Zero) return;
        AttachWindow(window);
        if (includeChildren)
            EnumChildWindows(window, (child, _) => { AttachWindow(child); return true; }, IntPtr.Zero);
    }

    private void AttachWindow(IntPtr window)
    {
        // WebView2 can have out-of-process child windows. Never subclass another thread/process.
        if (_windows.Contains(window) || GetWindowThreadProcessId(window, out _) != GetCurrentThreadId()) return;
        if (!SetWindowSubclass(window, _procedure, 1, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        _windows.Add(window);
        // Only open the two messages needed by the Shell's HDROP transport, not general IPC.
        if (!ChangeWindowMessageFilterEx(window, WmDropFiles, 1, IntPtr.Zero)
            || !ChangeWindowMessageFilterEx(window, WmCopyGlobalData, 1, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        DragAcceptFiles(window, _enabled);
    }

    internal bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            foreach (var window in _windows) DragAcceptFiles(window, value);
        }
    }

    private IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data)
    {
        if (message == WmNcDestroy)
        {
            _windows.Remove(window);
            RemoveWindowSubclass(window, _procedure, 1);
        }
        if (message != WmDropFiles) return DefSubclassProc(window, message, wParam, lParam);
        try
        {
            if (!_enabled || _disposed) return IntPtr.Zero;
            var count = DragQueryFile(wParam, uint.MaxValue, null, 0);
            if (count is 0 or > LocalShellStartupOptions.MaximumDirectories)
                throw new ArgumentException("一次请拖入 1–16 个目录。");
            var paths = new string[count];
            for (uint i = 0; i < count; i++)
            {
                var length = DragQueryFile(wParam, i, null, 0);
                if (length is 0 or > 32767) throw new ArgumentException("拖入的目录路径无效或过长。");
                var path = new char[length + 1];
                if (DragQueryFile(wParam, i, path, (uint)path.Length) != length)
                    throw new ArgumentException("无法读取拖入的目录路径。");
                paths[i] = new string(path, 0, (int)length);
            }
            _drop(paths);
        }
        catch (Exception ex) { _error(ex.Message); }
        finally { DragFinish(wParam); }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var window in _windows)
        {
            DragAcceptFiles(window, false);
            ChangeWindowMessageFilterEx(window, WmDropFiles, 0, IntPtr.Zero);
            ChangeWindowMessageFilterEx(window, WmCopyGlobalData, 0, IntPtr.Zero);
            RemoveWindowSubclass(window, _procedure, 1);
        }
        _windows.Clear();
    }

    private delegate IntPtr SubclassProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data);
    private delegate bool EnumWindowProcedure(IntPtr window, IntPtr data);
    [DllImport("comctl32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr window, SubclassProcedure procedure, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProcedure procedure, nuint id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr window, uint message, uint action, IntPtr changeInfo);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowProcedure callback, IntPtr data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("shell32.dll")] private static extern void DragAcceptFiles(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool accept);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFile(IntPtr drop, uint index, [Out] char[]? path, uint length);
    [DllImport("shell32.dll")] private static extern void DragFinish(IntPtr drop);
}
