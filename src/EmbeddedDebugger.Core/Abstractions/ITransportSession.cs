using EmbeddedDebugger.Core.Models;

namespace EmbeddedDebugger.Core.Abstractions;

public interface ITransportSession : IAsyncDisposable
{
    string Id { get; }
    TransportOptions Options { get; }
    TransportSessionState State { get; }
    string? BoundLocalEndpoint => null;
    event EventHandler<TransportMessage>? MessageReceived;
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
    /// <summary>Sends to one remote endpoint when the transport supports peer targeting.</summary>
    Task SendToAsync(ReadOnlyMemory<byte> data, string remoteEndpoint, CancellationToken cancellationToken = default)
        => SendAsync(data, cancellationToken);
}

public interface ITransportService
{
    ITransportSession CreateSession(TransportOptions options);
}
