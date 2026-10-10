using System.IO;
using System.Windows;

namespace EmbeddedDebugger.App;

internal static class Program
{
    [STAThread]
    public static int Main()
    {
        try
        {
            var app = new App();
            app.InitializeComponent();
            return app.Run();
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
            return 1;
        }
    }

    internal static void ReportFailure(Exception exception)
    {
        var detail = exception.Message;
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EmbeddedDebugger");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "startup-error.log");
            File.AppendAllText(path, $"{DateTimeOffset.Now:O}{Environment.NewLine}{exception}{Environment.NewLine}");
            detail += $"{Environment.NewLine}{Environment.NewLine}日志: {path}";
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
        {
        }
        MessageBox.Show(detail, "Embedded Debugger 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
