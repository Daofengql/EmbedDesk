using System.Windows;
using EmbedDesk.App.ViewModels;
using EmbedDesk.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EmbedDesk.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    public static ServiceProvider CreateServices(LocalShellStartupOptions? startup = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(startup ?? new LocalShellStartupOptions([]));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddEmbedDeskInfrastructure();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            LocalShellStartupOptions startup;
            string? argumentError = null;
            try { startup = new LocalShellStartupOptions(e.Args); }
            catch (Exception ex) when (ex is ArgumentException or System.IO.IOException or UnauthorizedAccessException)
            {
                startup = new LocalShellStartupOptions([]);
                argumentError = ex.Message;
            }
            _services = CreateServices(startup);
            MainWindow = _services.GetRequiredService<MainWindow>();
            if (startup.Directories.Count > 0) ((MainWindow)MainWindow).ShowLocalShellWorkspace();
            MainWindow.Show();
            if (argumentError is not null)
                MessageBox.Show(MainWindow, argumentError, "打开本地 Shell", MessageBoxButton.OK, MessageBoxImage.Information);
            await _services.GetRequiredService<MainWindowViewModel>().InitializeAsync();
        }
        catch (Exception exception)
        {
            Program.ReportFailure(exception);
            await StopAsync();
            Shutdown(1);
        }
    }

    public async Task StopAsync()
    {
        var services = _services;
        _services = null;
        if (services is not null) await services.DisposeAsync();
    }
}
