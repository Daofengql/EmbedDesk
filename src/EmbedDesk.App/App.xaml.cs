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

    public static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
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
            _services = CreateServices();
            MainWindow = _services.GetRequiredService<MainWindow>();
            MainWindow.Show();
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
