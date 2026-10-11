using System.Text.Json;
using EmbeddedDebugger.Core.Models;

namespace EmbeddedDebugger.Protocols;

/// <summary>Encodes the client-selected serial settings carried by an Open control frame.</summary>
public static class SerialBridgeControlCodec
{
    private sealed record WireSettings(string PortName, int BaudRate, int DataBits, int Parity, int StopBits, int Handshake, bool DtrEnable, bool RtsEnable);

    public static byte[] EncodeSettings(SerialPortSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return JsonSerializer.SerializeToUtf8Bytes(new WireSettings(settings.PortName, settings.BaudRate, settings.DataBits,
            (int)settings.Parity, (int)settings.StopBits, (int)settings.Handshake, settings.DtrEnable, settings.RtsEnable));
    }

    public static bool TryDecodeSettings(ReadOnlySpan<byte> payload, out SerialPortSettings settings)
    {
        settings = new();
        if (payload.Length > 4096) return false;
        try
        {
            var wire = JsonSerializer.Deserialize<WireSettings>(payload);
            if (wire is null || string.IsNullOrWhiteSpace(wire.PortName) || wire.PortName.Length > 128 ||
                wire.BaudRate <= 0 || wire.DataBits is < 5 or > 8 ||
                !Enum.IsDefined((System.IO.Ports.Parity)wire.Parity) ||
                wire.StopBits is < 1 or > 3 ||
                !Enum.IsDefined((System.IO.Ports.Handshake)wire.Handshake)) return false;
            settings = new SerialPortSettings
            {
                PortName = wire.PortName,
                BaudRate = wire.BaudRate,
                DataBits = wire.DataBits,
                Parity = (System.IO.Ports.Parity)wire.Parity,
                StopBits = (System.IO.Ports.StopBits)wire.StopBits,
                Handshake = (System.IO.Ports.Handshake)wire.Handshake,
                DtrEnable = wire.DtrEnable, RtsEnable = wire.RtsEnable
            };
            return true;
        }
        catch (JsonException) { return false; }
        catch (NotSupportedException) { return false; }
    }
}
