using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;

namespace EmbedDesk.App;

public partial class AboutWindow : Window
{
    private const string RepositoryUrl = "https://github.com/Daofengql/EmbedDesk";

    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"版本 {GetVersionLabel()}";
        WindowTheme.ApplyDarkTitleBar(this);
    }

    private static string GetVersionLabel()
    {
        var version = typeof(AboutWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (string.IsNullOrWhiteSpace(version)) return "v0.0.2";
        var metadataSeparator = version.IndexOf('+');
        if (metadataSeparator >= 0) version = version[..metadataSeparator];
        return version.StartsWith('v') ? version : $"v{version}";
    }

    private void OpenRepository_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(RepositoryUrl) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, $"无法打开默认浏览器，请手动访问：\n{RepositoryUrl}", "打开开源仓库", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
