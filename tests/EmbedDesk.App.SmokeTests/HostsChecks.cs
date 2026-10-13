using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EmbedDesk.App.Controls;
using EmbedDesk.App.ViewModels;
using EmbedDesk.Core.Models;
using EmbedDesk.Infrastructure.Networking;

namespace EmbedDesk.App.SmokeTests;

internal static class HostsChecks
{
    internal static Task RunAsync()
    {
        using var temporary = new TemporarySettingsDirectory();
        var path = Path.Combine(temporary.DirectoryPath, "hosts");
        var backupDir = Path.Combine(temporary.DirectoryPath, "backups");
        var original = Encoding.UTF8.GetBytes("# external\n127.0.0.1 localhost\n");
        File.WriteAllBytes(path, original);
        var service = new HostsFileService(path);
        var snapshot = service.Read();
        var rules = new[]
        {
            new HostsMapping { Domain = "例子.测试", Address = "127.0.0.2", Comment = "smoke" },
            new HostsMapping { Domain = "例子.测试", Address = "::1" },
            new HostsMapping { Domain = "disabled.test", Address = "127.0.0.3", Enabled = false }
        };
        var content = HostsFileService.CreateManagedContent(snapshot, rules);
        var backup = service.Apply(snapshot, content, backupDir);
        Require(File.ReadAllBytes(backup).SequenceEqual(original), "Hosts application creates a complete backup");
        var text = File.ReadAllText(path);
        Require(text.Contains("127.0.0.2\txn--fsqu00a.xn--0zwm56d") && text.Contains("::1\txn--fsqu00a.xn--0zwm56d")
            && !text.Contains("disabled.test"), "Hosts writes enabled exact IPv4/IPv6 and IDN rules");
        RequireThrows<IOException>(() => service.Apply(snapshot, content, backupDir), "Stale Hosts snapshots cannot overwrite newer edits");
        var second = service.Read();
        var removed = HostsFileService.CreateManagedContent(second, []);
        service.Apply(second, removed, backupDir);
        Require(File.ReadAllBytes(path).SequenceEqual(original), "Removing the managed block preserves external Hosts bytes");
        RequireThrows<InvalidOperationException>(() => HostsFileService.CreateManagedContent(service.Read(),
            [new HostsMapping { Domain = "localhost", Address = "127.0.0.2" }]), "External Hosts conflicts are rejected");
        CheckSettings(temporary.DirectoryPath);
        return Task.CompletedTask;
    }

    private static void CheckSettings(string directory)
    {
        var legacy = Encoding.UTF8.GetBytes("""
            {"SchemaVersion":1,"ListenAddress":"127.0.0.1","ListenPort":5353,"UpstreamAddress":"127.0.0.1",
             "Profiles":[{"Name":"旧方案","Rules":[
                {"Domain":"example.test","Address":"127.0.0.2","Enabled":true},
                {"Domain":"subdomain.test","Address":"::1","MatchSubdomains":true,"Enabled":true}]}]}
            """);
        var path = Path.Combine(directory, "hosts-manager.json");
        File.WriteAllBytes(path, legacy);
        var store = new HostsManagerSettingsStore(directory);
        var migrated = store.Load();
        Require(migrated.SchemaVersion == 2 && store.DisabledLegacySubdomains && migrated.Profiles[0].Rules[0].Enabled
            && !migrated.Profiles[0].Rules[1].Enabled && migrated.Profiles[0].Rules[1].Domain == "subdomain.test",
            "Legacy exact rules survive; subdomain matching rules are retained and disabled");
        store.Save(migrated);
        var serialized = File.ReadAllText(path);
        Require(!serialized.Contains("ListenAddress") && !serialized.Contains("UpstreamAddress") && !serialized.Contains("MatchSubdomains")
            && File.ReadAllBytes(path + ".bak").SequenceEqual(legacy), "Migrated settings drop proxy fields and retain a legacy backup");
        var staleStore = new HostsManagerSettingsStore(directory);
        _ = staleStore.Load();
        store.Save(migrated with { Profiles = [migrated.Profiles[0] with { Name = "更新方案" }] });
        RequireThrows<IOException>(() => staleStore.Save(migrated), "A stale settings window cannot overwrite another window's changes");
        RequireThrows<InvalidDataException>(() => HostsManagerSettingsStore.Parse(Encoding.UTF8.GetBytes("{\"Profiles\":[null]}")),
            "Invalid imported profiles are rejected");
    }

    internal static async Task RunWindowAsync()
    {
        using var temporary = new TemporarySettingsDirectory();
        var path = Path.Combine(temporary.DirectoryPath, "hosts");
        var original = "# isolated test hosts\n127.0.0.1 localhost\n";
        File.WriteAllText(path, original);
        var store = new HostsManagerSettingsStore(temporary.DirectoryPath);
        _ = store.Load();
        store.Save(new HostsManagerSettings
        {
            Profiles = [new HostsProfile { Name = "本地开发", Rules = [
                new HostsMapping { Domain = "example.test", Address = "127.0.0.2", Comment = "IPv4 开发服务" },
                new HostsMapping { Domain = "example.test", Address = "::1", Comment = "IPv6 开发服务" }] }]
        });
        var window = new HostsManagerWindow(temporary.DirectoryPath, path);
        try
        {
            window.Show();
            await FileWorkspaceChecks.CaptureAsync(window, "embeddesk-hosts-default.png");
            var grid = (DataGrid)window.FindName("RulesGrid");
            Require(grid.Columns.Count == 4 && !Descendants<Button>(window).Any(button =>
                button.Content is string label && (label.Contains("代理") || label.Contains("管理员"))),
                "Hosts UI contains only exact mapping controls without proxy or re-elevation actions");
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            await FileWorkspaceChecks.CaptureAsync(window, "embeddesk-hosts-minimum.png");
            var apply = (Button)window.FindName("ApplyHostsButton");
            var bottom = apply.TransformToAncestor(window).Transform(new Point(0, apply.ActualHeight));
            Require(grid.ActualHeight >= grid.MinHeight && apply.IsVisible && bottom.Y < window.ActualHeight - 20,
                "Rule grid and Hosts actions fit at the minimum window size");
            var vm = (HostsManagerViewModel)window.DataContext;
            vm.SelectedProfile!.Rules[0].Comment = "修改后的备注";
            ((Button)window.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(new HostsManagerSettingsStore(temporary.DirectoryPath).Load().Profiles[0].Rules[0].Comment == "修改后的备注",
                "Saving from the Hosts window persists edited rules");
        }
        finally { window.Close(); }
        var reopened = new HostsManagerWindow(temporary.DirectoryPath, path);
        try
        {
            reopened.Show();
            Require(((HostsManagerViewModel)reopened.DataContext).SelectedProfile!.Rules[0].Comment == "修改后的备注",
                "Reopening restores the saved Hosts scheme");
        }
        finally { reopened.Close(); }
        Require(File.ReadAllText(path) == original, "Saving and reopening drafts never applies Hosts implicitly");

        var gallery = new Window { Title = "网络工具", Width = 1080, Height = 880, Content = new NetworkToolsWorkspace() };
        try
        {
            gallery.Show();
            await FileWorkspaceChecks.CaptureAsync(gallery, "embeddesk-network-tools-spacing.png");
            var cards = Descendants<Button>(gallery).Where(button => button.Tag is string).ToArray();
            var first = cards.Single(button => (string)button.Tag == "Cidr");
            var next = cards.Single(button => (string)button.Tag == "LocalInfo");
            var gap = next.TransformToAncestor(gallery).Transform(new Point()).Y
                - first.TransformToAncestor(gallery).Transform(new Point(0, first.ActualHeight)).Y;
            Require(gap >= 8, "Network tool card rows have at least 8 DIPs of separation");
        }
        finally { gallery.Close(); }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) yield return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(parent, index))) yield return child;
    }

    private static void RequireThrows<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { Console.WriteLine("PASS " + message); return; }
        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }
}
