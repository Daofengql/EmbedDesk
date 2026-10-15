using EmbedDesk.Core.Models;

namespace EmbedDesk.Core.Abstractions;

/// <summary>Application-wide reservations. Reserving never opens a device.</summary>
public interface ISerialPortSharing
{
    event EventHandler? ReservationsChanged;
    void SetSharedPorts(IEnumerable<string> portNames);
    bool IsShared(string portName);
    bool IsInUse(string portName);
    ISerialPortChannel CreateSharedChannel(SerialPortSettings settings);
}
