using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace EmbeddedDebugger.App;

internal static class WindowTheme
{
    private const int UseImmersiveDarkMode = 20;
    private const int LegacyImmersiveDarkMode = 19;
    private const int BorderColor = 34;
    private const int CaptionColor = 35;
    private const int TextColor = 36;

    public static void ApplyDarkTitleBar(Window window)
    {
        window.SourceInitialized += (_, _) => Apply(window);
    }

    private static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var enabled = 1;
        _ = DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, sizeof(int));
        _ = DwmSetWindowAttribute(handle, LegacyImmersiveDarkMode, ref enabled, sizeof(int));

        var caption = unchecked((int)0x0027221D); // COLORREF for #1D2227
        var text = unchecked((int)0x00F0EDE8); // COLORREF for #E8EDF0
        var border = unchecked((int)0x00443C34); // COLORREF for #343C44
        _ = DwmSetWindowAttribute(handle, CaptionColor, ref caption, sizeof(int));
        _ = DwmSetWindowAttribute(handle, TextColor, ref text, sizeof(int));
        _ = DwmSetWindowAttribute(handle, BorderColor, ref border, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);
}
