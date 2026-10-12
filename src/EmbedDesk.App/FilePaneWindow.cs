using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace EmbedDesk.App;

/// <summary>Reports native title-bar drags as well as drags initiated from the file header.</summary>
public sealed class FilePaneWindow : Window
{
    private HwndSource? _source;
    private bool _moving;
    public event EventHandler? MoveStarted;
    public event EventHandler<Point>? MoveUpdated;
    public event EventHandler<Point>? MoveCompleted;
    public event EventHandler? MoveCancelled;

    public void UpdateMove(Point screenPosition) => MoveUpdated?.Invoke(this, screenPosition);
    public void CompleteMove(Point screenPosition) => MoveCompleted?.Invoke(this, screenPosition);

    public FilePaneWindow()
    {
        Title = "文件传输";
        Width = 940; Height = 460; MinWidth = 480; MinHeight = 240;
        ShowInTaskbar = false;
        Icon = new BitmapImage(new Uri("pack://application:,,,/EmbedDesk.App;component/Assets/embeddesk.ico"));
        WindowTheme.ApplyDarkTitleBar(this);
        SourceInitialized += (_, _) =>
        {
            _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _source?.AddHook(WindowMessage);
        };
        Closed += (_, _) => { _source?.RemoveHook(WindowMessage); _source = null; };
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case 0x0231: // WM_ENTERSIZEMOVE
                _moving = false;
                break;
            case 0x0216: // WM_MOVING (exclude resizing)
                if (!_moving) MoveStarted?.Invoke(this, EventArgs.Empty);
                _moving = true;
                if (GetCursorPos(out var cursor)) UpdateMove(new Point(cursor.X, cursor.Y));
                break;
            case 0x0232: // WM_EXITSIZEMOVE
                if (_moving && (GetAsyncKeyState(0x1B) & 0x8000) == 0 && GetCursorPos(out var end)) CompleteMove(new Point(end.X, end.Y));
                else MoveCancelled?.Invoke(this, EventArgs.Empty);
                _moving = false;
                break;
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
