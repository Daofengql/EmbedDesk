using EmbeddedDebugger.Core.Models;

namespace EmbeddedDebugger.Core.Abstractions;

public interface ISerialPortChannel : IAsyncDisposable
{
    string Id { get; }
    SerialPortSettings Settings { get; }
    bool IsOpen { get; }
    event EventHandler<ReadOnlyMemory<byte>>? DataReceived;
    Task OpenAsync(CancellationToken cancellationToken = default);
    Task CloseAsync(CancellationToken cancellationToken = default);
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
}

public interface ISerialPortService
{
    Task<IReadOnlyList<SerialPortDescriptor>> ScanAsync(CancellationToken cancellationToken = default);
    ISerialPortChannel CreateChannel(SerialPortSettings settings);
}

public interface IVirtualSerialMapper
{
    Task<IReadOnlyList<string>> CreatePairAsync(string displayName, CancellationToken cancellationToken = default);
    Task RemovePairAsync(IReadOnlyList<string> portNames, CancellationToken cancellationToken = default);
}

public interface ISerialBridgeService
{
    Task<string> StartAsync(SerialBridgeProfile profile, CancellationToken cancellationToken = default);
    Task StopAsync(string bridgeId, CancellationToken cancellationToken = default);
}
