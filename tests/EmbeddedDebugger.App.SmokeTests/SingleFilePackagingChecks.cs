using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EmbeddedDebugger.App.Controls;
using EmbeddedDebugger.App.ViewModels;
using Microsoft.Web.WebView2.Wpf;

namespace EmbeddedDebugger.App.SmokeTests;

internal static class SingleFilePackagingChecks
{
    internal static async Task RunAsync(MainWindowViewModel viewModel)
    {
        if (Directory.GetFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories).Length != 1)
            throw new InvalidOperationException("Packaging verification requires an isolated directory containing only the EXE.");
        await viewModel.InitializeAsync();
        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            Descendants<TabControl>(window).First().SelectedIndex = 1;
            window.UpdateLayout();
            var terminal = Descendants<ShellTerminal>(window).First();
            var browser = (WebView2)terminal.FindName("Browser");
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (browser.CoreWebView2 is null || await browser.ExecuteScriptAsync("typeof terminal !== 'undefined' && terminal.cols > 0 && terminal.rows > 0") != "true")
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("The single-file terminal failed to initialize.");
                await Task.Delay(100);
            }
            if (await browser.ExecuteScriptAsync("getComputedStyle(document.querySelector('.xterm-screen')).position === 'relative'") != "true")
                throw new InvalidOperationException("The embedded xterm stylesheet failed to load.");
            viewModel.RemoteDebug!.IsLocalShellFullscreen = true;
            await Task.Delay(300);
            var surface = (Grid)window.FindName("ApplicationSurface");
            if (Math.Abs(terminal.ActualWidth - surface.ActualWidth) > 1 || Math.Abs(terminal.ActualHeight - surface.ActualHeight) > 1)
                throw new InvalidOperationException("The packaged terminal does not fill the fullscreen surface.");
            await TerminalRegressionChecks.CheckVisibleRowsAsync(browser);
            if (!viewModel.RemoteDebug.LocalShell.IsRunning) throw new InvalidOperationException("The packaged local Shell did not start.");
            viewModel.RemoteDebug.LocalShell.Terminal.Add(DateTimeOffset.Now, true,
                System.Text.Encoding.UTF8.GetBytes("\r\n\x1b[32mSingle EXE terminal assets loaded successfully.\x1b[0m\r\n"));
            deadline = DateTime.UtcNow.AddSeconds(10);
            while (await browser.ExecuteScriptAsync("Array.from({length:terminal.buffer.active.length},(_,i)=>terminal.buffer.active.getLine(i).translateToString(true)).join('\\n').includes('Single EXE terminal assets loaded successfully.')") != "true")
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("The packaged terminal did not render its output.");
                await Task.Delay(100);
            }
            await FileWorkspaceChecks.CaptureAsync(window, "embedded-debugger-single-exe.png");
            viewModel.RemoteDebug.IsLocalShellFullscreen = false;
            Console.WriteLine("PASS Single EXE loads WPF, WebView2 native dependencies, embedded HTML/JS/CSS, local Shell and fullscreen without adjacent files");
        }
        finally { window.Close(); }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
}
