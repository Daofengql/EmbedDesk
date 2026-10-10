using EmbeddedDebugger.Core.Models;

namespace EmbeddedDebugger.Core.Abstractions;

public sealed record RemoteSerialPort(ushort ChannelId, string PortName, string DisplayName);

public interface IRemoteSerialClient : IAsyncDisposable
{
    bool IsConnected { get; }
    event EventHandler? PortsChanged;
    event EventHandler? ConnectionChanged;
    IReadOnlyList<RemoteSerialPort> Ports { get; }
    Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default);
    Task DisconnectAsync();
    ISerialPortChannel CreateChannel(string portName, SerialPortSettings settings);
}
