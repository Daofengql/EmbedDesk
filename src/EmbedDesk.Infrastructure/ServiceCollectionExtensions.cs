using EmbedDesk.Core.Abstractions;
using EmbedDesk.Infrastructure.Bridging;
using EmbedDesk.Infrastructure.Diagnostics;
using EmbedDesk.Infrastructure.Networking;
using EmbedDesk.Infrastructure.Serial;
using EmbedDesk.Infrastructure.Remote;
using Microsoft.Extensions.DependencyInjection;

namespace EmbedDesk.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddEmbedDeskInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<ITransportService, TcpUdpTransportService>();
        services.AddSingleton<ISerialPortService, SerialPortService>();
        services.AddSingleton<IVirtualSerialMapper, ExternalVirtualSerialMapper>();
        services.AddSingleton<IHttpDiagnosticsParser, HttpDiagnosticsParser>();
        services.AddSingleton<ISerialBridgeService, SerialBridgeService>();
        services.AddSingleton<IRemoteSerialClient, RemoteSerialClient>();
        services.AddSingleton<IRemoteDebugService, RemoteDebugService>();
        return services;
    }
}
