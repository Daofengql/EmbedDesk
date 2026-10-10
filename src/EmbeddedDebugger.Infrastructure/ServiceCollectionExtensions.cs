using EmbeddedDebugger.Core.Abstractions;
using EmbeddedDebugger.Infrastructure.Bridging;
using EmbeddedDebugger.Infrastructure.Diagnostics;
using EmbeddedDebugger.Infrastructure.Networking;
using EmbeddedDebugger.Infrastructure.Serial;
using EmbeddedDebugger.Infrastructure.Remote;
using Microsoft.Extensions.DependencyInjection;

namespace EmbeddedDebugger.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddEmbeddedDebuggerInfrastructure(this IServiceCollection services)
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
