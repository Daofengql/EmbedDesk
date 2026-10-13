using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EmbedDesk.App.Controls;
using EmbedDesk.App.ViewModels;
using EmbedDesk.Core.Models;

namespace EmbedDesk.App.SmokeTests;

internal static class S3Checks
{
    public static async Task RunAsync(MainWindow window, RemoteDebugWorkspace control, RemoteDebugWorkspaceViewModel workspace, string settingsDirectory)
    {
        await S3ObjectStorageChecks.RunAsync();
        await using var fixture = new LocalS3Fixture();
        var profile = new RemoteConnectionProfile
        {
            Name = "S3 smoke profile",
            Protocol = RemoteProtocol.S3,
            Host = fixture.Endpoint,
            Port = 443,
            S3Region = "us-east-1",
            S3Bucket = "sample-bucket"
        };
        var editor = new ConnectionProfileWindow(workspace, profile, false);
        try
        {
            editor.Show();
            await Task.Delay(100);
            editor.UpdateLayout();
            Require(editor.IsS3 && editor.Editor.Endpoint.EndsWith("us-east-1", StringComparison.Ordinal), "S3 connection editor exposes the service endpoint and region without a terminal port");
            Require(editor.FindName("S3SecretEditor") is PasswordBox && ((FrameworkElement)editor.FindName("PortEditor")!).Visibility == Visibility.Collapsed,
                "S3 credentials have a password field and the remote-terminal port field is hidden");
            await FileWorkspaceChecks.CaptureAsync(editor, "embeddesk-s3-profile.png");
        }
        finally { editor.Close(); }

        profile.S3AccessKeyId = "smoke-access-key";
        profile.S3SecretAccessKey = "smoke-secret-key";
        workspace.Profiles.Add(profile);
        workspace.SaveProfilesCommand.Execute(null);
        var savedProfiles = File.ReadAllText(Path.Combine(settingsDirectory, "remote-profiles.json"));
        Require(!savedProfiles.Contains("smoke-access-key", StringComparison.Ordinal) && !savedProfiles.Contains("smoke-secret-key", StringComparison.Ordinal),
            "S3 access and secret keys are excluded from persisted connection JSON");

        await workspace.OpenProfileInNewTabAsync(profile);
        window.UpdateLayout();
        await Task.Delay(100);
        window.UpdateLayout();
        Require(workspace.SelectedS3 is not null && workspace.Tabs.Contains(workspace.SelectedS3)
            && workspace.Sessions.Count == 0 && workspace.SelectedSession is null && !workspace.IsLocalShellSelected,
            "Opening an S3 profile creates a remote-workspace tab without creating a terminal session");
        await FileWorkspaceChecks.CaptureAsync(window, "embeddesk-s3-main.png");
        var standaloneBrowser = new S3Workspace { DataContext = workspace.SelectedS3 };
        var browserWindow = new Window { Owner = window, Width = 900, Height = 600, Content = standaloneBrowser };
        browserWindow.Show();
        await Task.Delay(100);
        browserWindow.UpdateLayout();
        var objectGrid = (DataGrid)standaloneBrowser.FindName("ObjectGrid");
        Require(objectGrid.Columns.Count == 4,
            "S3 tab displays object-browser controls");
        Require(workspace.SelectedS3!.Status.Contains("sample-bucket", StringComparison.Ordinal), "S3 browser lists the configured bucket through a local S3-compatible endpoint");
        await FileWorkspaceChecks.CaptureAsync(browserWindow, "embeddesk-s3-browser.png");
        browserWindow.Close();
        await workspace.CloseS3BrowserAsync(workspace.SelectedS3!);
        workspace.Profiles.Remove(profile);
        workspace.SaveProfilesCommand.Execute(null);
    }

    private sealed class LocalS3Fixture : IAsyncDisposable
    {
        private const string EmptyList = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Name>sample-bucket</Name><Prefix></Prefix><KeyCount>0</KeyCount><MaxKeys>500</MaxKeys><IsTruncated>false</IsTruncated></ListBucketResult>";
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _acceptLoop;
        public LocalS3Fixture() { _listener.Start(); Endpoint = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}"; _acceptLoop = AcceptAsync(); }
        public string Endpoint { get; }
        private async Task AcceptAsync()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cancellation.Token); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                _ = RespondAsync(client);
            }
        }
        private static async Task RespondAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                string? line;
                do { line = await reader.ReadLineAsync(); } while (!string.IsNullOrEmpty(line));
                var body = Encoding.UTF8.GetBytes(EmptyList);
                var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/xml\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header);
                await stream.WriteAsync(body);
            }
        }
        public async ValueTask DisposeAsync()
        {
            _cancellation.Cancel(); _listener.Stop();
            try { await _acceptLoop; } catch (SocketException) { }
            _cancellation.Dispose();
        }
    }

    private static T Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T found) return found;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (Find<T>(VisualTreeHelper.GetChild(root, index)) is { } child) return child;
        throw new InvalidOperationException($"Could not find {typeof(T).Name} in the visual tree.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine($"PASS {message}");
    }
}
