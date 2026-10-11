using System.Buffers.Binary;
using System.IO;
using EmbeddedDebugger.App;
using EmbeddedDebugger.Core.Abstractions;
using EmbeddedDebugger.Core.Models;
using EmbeddedDebugger.Infrastructure.Firmware;

namespace EmbeddedDebugger.App.SmokeTests;

internal static class StcChecks
{
    internal static async Task RunAsync()
    {
        var runFrame = Stc89BootloaderClient.EncodePacket([0x82]);
        Require(runFrame.SequenceEqual(new byte[] { 0x46, 0xB9, 0x6A, 0, 6, 0x82, 0xF2, 0x16 }), "STC89 command framing matches wire vector");
        Require(Stc89BootloaderClient.DecodePacket([0x46, 0xB9, 0x68, 0, 6, 0x80, 0xEE, 0x16]).SequenceEqual(new byte[] { 0x80 }), "STC89 response checksum vector");
        Reject<InvalidDataException>(() => Stc89BootloaderClient.DecodePacket([0x46, 0xB9, 0x68, 0, 6, 0x80, 0xEF, 0x16]), "Corrupt response checksum rejected");
        var status = Status();
        var info = Stc89BootloaderClient.ParseStatus(status, 2400);
        Require(info.Device.CodeBytes == 8192 && Math.Abs(info.ClockHz - 11059200) < 1 && !info.SixClockMode, "STC89 status ID, capacity and oscillator detection");
        var baud = Stc89BootloaderClient.CalculateBaud(info, 19200);
        Require(baud.Reload == 0xFFEE && baud.Checksum == 0x24 && baud.WaitStates == 0x81, "STC89 baud negotiation timing");
        var unknown = (byte[])status.Clone(); unknown[20] = 0xF3;
        Reject<NotSupportedException>(() => Stc89BootloaderClient.ParseStatus(unknown, 2400), "Unknown MCU refused before writing");
        Reject<ArgumentException>(() => Stc89BootloaderClient.PrepareFirmware([new(8190, [1, 2, 3])], info.Device), "Oversize/nonzero reset-vector image rejected");
        Reject<ArgumentException>(() => Stc89BootloaderClient.PrepareFirmware([new(0, [1, 2]), new(1, [3])], info.Device), "Overlapping STC image rejected");
        var prepared = Stc89BootloaderClient.PrepareFirmware([new(0, [2, 3]), new(128, [4])], info.Device);
        Require(prepared.Length == 256 && prepared[2] == 0xFF && prepared[128] == 4 && prepared[^1] == 0xFF, "Sparse firmware padding leaves erased bytes");
        var serial = new ScriptedStcChannel();
        await using (var client = new Stc89BootloaderClient(serial))
        {
            var device = await client.ConnectAsync();
            Require(device.Device.Id == 0xF002 && serial.BaudChanges.SequenceEqual(new[] { 19200, 2400, 19200 }), "STC89 full handshake and dynamic baud sequence");
            var sent = serial.Commands.Count;
            try { await client.ProgramAsync([new(0, new byte[8193])]); throw new Exception("Invalid image was accepted"); }
            catch (ArgumentException) { }
            Require(serial.Commands.Count == sent, "No erase or write sent for invalid image");
            await client.ProgramAsync([new(0, [2, 3]), new(128, [4])]);
            Require(serial.Flash.AsSpan(0, 256).SequenceEqual(prepared) && serial.RestoredOptions == status[19], "STC89 erase, block program and preserved options");
            Require(serial.EraseBlocks == 32, "STC89 erase limited to 8 KiB code area");
            serial.CorruptProgramAck = true;
            try { await client.ProgramAsync([new(0, [1])]); throw new Exception("Corrupt program acknowledgement was accepted"); }
            catch (IOException) { }
            Require(serial.Commands.Last() == 0, "Bad device checksum stops before option programming or execution");
        }
        var idle = new ScriptedStcChannel { IgnoreInput = true };
        await using (var client = new Stc89BootloaderClient(idle))
        {
            using var timeout = new CancellationTokenSource(60);
            try { await client.ConnectAsync(token: timeout.Token); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) { }
            Require(idle.Commands.Count == 0, "STC89 pending power-on wait is cancellable");
        }
        var window = new StcIspToolWindow(new FakeSerialService()); window.Show(); await Task.Delay(40); window.Close();
        Console.WriteLine("PASS STC89 wire protocol, guards, acknowledgement validation, cancellation and tool initialization");
    }
    private static byte[] Status()
    {
        var data = new byte[23];
        for (var i = 0; i < 8; i++) BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(1 + i * 2), 2688);
        data[17] = 0x62; data[18] = (byte)'A'; data[19] = 1; data[20] = 0xF0; data[21] = 2;
        return data;
    }
    private static void Reject<T>(Action action, string description) where T : Exception
    { try { action(); } catch (T) { Console.WriteLine("PASS " + description); return; } throw new InvalidOperationException("FAIL " + description); }
    private static void Require(bool condition, string description)
    { if (!condition) throw new InvalidOperationException("FAIL " + description); Console.WriteLine("PASS " + description); }

    private sealed class ScriptedStcChannel : ISerialPortChannel, ISerialPortControl
    {
        public string Id => "stc-fixture";
        public SerialPortSettings Settings { get; private set; } = new() { PortName = "TEST", BaudRate = 2400 };
        public bool IsOpen => true;
        public event EventHandler<ReadOnlyMemory<byte>>? DataReceived;
        public List<int> BaudChanges { get; } = [];
        public List<byte> Commands { get; } = [];
        public byte[] Flash { get; } = new byte[8192];
        public byte RestoredOptions { get; private set; }
        public byte EraseBlocks { get; private set; }
        public bool CorruptProgramAck { get; set; }
        public bool IgnoreInput { get; set; }
        public Task OpenAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task SetControlLinesAsync(bool dtrEnable, bool rtsEnable, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetBaudRateAsync(int baudRate, CancellationToken cancellationToken = default)
        { BaudChanges.Add(baudRate); Settings = new() { PortName = "TEST", BaudRate = baudRate }; return Task.CompletedTask; }
        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); if (IgnoreInput) return Task.CompletedTask;
            if (data.Length == 1 && data.Span[0] == 0x7F) { Respond(Status()); return Task.CompletedTask; }
            var bytes = data.ToArray();
            if (bytes[0] != 0x46 || bytes[1] != 0xB9 || bytes[2] != 0x6A || bytes[^1] != 0x16 || BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(3)) != bytes.Length - 2 || (byte)bytes[2..^2].Sum(v => v) != bytes[^2]) throw new InvalidDataException("Invalid host frame");
            var payload = bytes[5..^2]; Commands.Add(payload[0]);
            switch (payload[0])
            {
                case 0x8F: case 0x8E: Respond([payload[0]]); break;
                case 0x80: if (payload.Length != 7 || payload[5] != 0xF0 || payload[6] != 2) throw new InvalidDataException(); Respond([0x80]); break;
                case 0x84: EraseBlocks = payload[1]; Array.Fill(Flash, (byte)0xFF); Respond([0x80]); break;
                case 0:
                    var address = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(3));
                    if (BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(5)) != 128 || payload.Length != 135) throw new InvalidDataException();
                    payload.AsSpan(7).CopyTo(Flash.AsSpan(address));
                    Respond([0x80, (byte)(payload[7..].Sum(v => v) + (CorruptProgramAck ? 1 : 0))]); break;
                case 0x8D: RestoredOptions = payload[1]; Respond([0x8D]); break;
                case 0x82: break;
                default: throw new InvalidDataException("Unexpected command");
            }
            return Task.CompletedTask;
        }
        private void Respond(byte[] payload)
        {
            // Independent device fixture encoder, deliberately fragmented to exercise streaming parsing.
            var packet = new byte[payload.Length + 7]; packet[0] = 0x46; packet[1] = 0xB9; packet[2] = 0x68;
            packet[3] = (byte)((payload.Length + 5) >> 8); packet[4] = (byte)(payload.Length + 5);
            payload.CopyTo(packet, 5); packet[^2] = (byte)packet[2..^2].Sum(v => v); packet[^1] = 0x16;
            for (var i = 0; i < packet.Length; i += 3) DataReceived?.Invoke(this, packet.AsMemory(i, Math.Min(3, packet.Length - i)));
        }
    }
}
