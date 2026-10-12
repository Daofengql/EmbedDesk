using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EmbedDesk.App.ViewModels;
using EmbedDesk.Core.Models;

namespace EmbedDesk.App.SmokeTests;

internal static class ConnectionLayoutChecks
{
    public static async Task RunAsync(RemoteDebugWorkspaceViewModel workspace)
    {
        var window = new ConnectionProfileWindow(workspace, new RemoteConnectionProfile
        {
            Name = "测试主机", Host = "localhost", Username = "tester",
            AuthenticationMode = RemoteAuthenticationMode.Password
        }, false);
        window.Show();
        try
        {
            await Task.Delay(100); window.UpdateLayout();
            var passwordHeight = window.ActualHeight;
            window.Editor.AuthenticationMode = RemoteAuthenticationMode.PrivateKey;
            await Task.Delay(100); window.UpdateLayout();
            var scroller = (ScrollViewer)window.FindName("ProfileScroller");
            var hint = (TextBlock)window.FindName("AuthenticationHint");
            var actions = (FrameworkElement)window.FindName("ActionButtons");
            Require(window.ActualHeight > passwordHeight && scroller.ScrollableHeight < 1, "Private-key authentication expands the dialog to fit its extra fields");
            Require(Bottom(hint, window) < actions.TranslatePoint(new Point(), window).Y, "SSH key hint and host-key option fit above the fixed action buttons");
            await FileWorkspaceChecks.CaptureAsync(window, "embeddesk-key-profile-fixed.png");
            window.MaxHeight = 460;
            await Task.Delay(100); window.UpdateLayout();
            Require(scroller.ScrollableHeight > 0 && Bottom(actions, window) < window.ActualHeight, "Short screens scroll only the connection form while keeping actions visible");
            scroller.ScrollToEnd(); await Task.Delay(100);
            Require(Bottom(hint, scroller) <= scroller.ActualHeight + 1, "All private-key fields remain reachable on short screens");
            scroller.ScrollToTop();
            ((TextBox)window.FindName("PortEditor")).Text = "invalid";
            Descendants<Button>(window).Single(b => Equals(b.Content, "保存")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(100); window.UpdateLayout();
            var validation = (TextBlock)window.FindName("ValidationMessage");
            Require(validation.Text.Length > 0 && Bottom(validation, scroller) <= scroller.ActualHeight + 1, "Validation messages are reachable without covering the save/open buttons");
            await FileWorkspaceChecks.CaptureAsync(window, "embeddesk-key-profile-short.png");
            foreach (var protocol in new[] { RemoteProtocol.Rdp, RemoteProtocol.Vnc, RemoteProtocol.Ssh })
            {
                window.Editor.Protocol = protocol; window.UpdateLayout();
                Require(Bottom(actions, window) < window.ActualHeight, $"Switching to {protocol} keeps connection actions visible");
            }
        }
        finally { window.Close(); }
    }

    private static double Bottom(FrameworkElement element, Visual relativeTo) => element.TransformToAncestor(relativeTo).Transform(new Point(0, element.ActualHeight)).Y;
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine($"PASS {message}");
    }
}
