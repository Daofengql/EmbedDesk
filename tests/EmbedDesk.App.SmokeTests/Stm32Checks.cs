using System.Buffers.Binary;
using System.IO;
using EmbedDesk.Core.Abstractions;
using EmbedDesk.Core.Models;
using EmbedDesk.Infrastructure.Firmware;

namespace EmbedDesk.App.SmokeTests;

internal static class Stm32Checks
{
    internal static async Task RunAsync()
    {
        Reject(() => Stm32BootloaderClient.ValidateFlashSegments([new(0x08000000, [1]), new(0x08000002, [2])]), "STM32 padding/alignment overlap refused");
        Reject(() => Stm32BootloaderClient.ValidateFlashSegments([new(0x081FFFFC, new byte[5])]), "STM32 padded write exceeding Flash range refused");
        var serial = new Stm32Fixture();
        await using var client = new Stm32BootloaderClient(serial);
        await client.ConnectAsync();
        Require(await client.ReadDeviceIdAsync() == 0x410 && client.SupportsRead && client.SupportsErase && client.SupportsGo, "STM32 discovery and command capabilities");
        await client.MassEraseAsync();
        var firmware = Enumerable.Range(0, 301).Select(i => (byte)i).ToArray();
        await client.WriteMemoryAsync(0x08000000, firmware);
        Require(serial.Flash[..firmware.Length].SequenceEqual(firmware) && serial.Flash[301] == 0xFF, "STM32 multi-block write with padded final block and readback");
        await client.VerifyMemoryAsync(0x08000000, firmware);
        using var backup = new MemoryStream(); await client.ReadFlashAsync(0x08000000, 512, backup);
        Require(backup.ToArray().SequenceEqual(serial.Flash[..512]), "STM32 streamed backup equals device bytes");
        serial.Flash[299] ^= 1;
        try { await client.VerifyMemoryAsync(0x08000000, firmware); throw new Exception("Mismatch accepted"); }
        catch (IOException) { }
        await client.GoAsync(0x08000000);
        Require(serial.GoAddress == 0x08000000, "STM32 GO sends address checksum");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await client.ReadMemoryAsync(0x08000000, 1, cancellation.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS STM32 protocol, write validation, readback mismatch, backup, GO and cancellation");
    }
    private static void Reject(Action action, string description)
    { try { action(); } catch (ArgumentException) { Console.WriteLine("PASS " + description); return; } throw new InvalidOperationException("FAIL " + description); }
    private static void Require(bool condition, string description)
    { if (!condition) throw new InvalidOperationException("FAIL " + description); Console.WriteLine("PASS " + description); }
    private sealed class Stm32Fixture : ISerialPortChannel
    {
        public string Id => "stm-fixture";
        public SerialPortSettings Settings { get; } = new() { PortName = "TEST", BaudRate = 115200 };
        public bool IsOpen => true;
        public event EventHandler<ReadOnlyMemory<byte>>? DataReceived;
        public byte[] Flash { get; } = new byte[1024];
        public uint GoAddress { get; private set; }
        private byte? _command;
        private uint? _address;
        public Task OpenAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); var bytes = data.ToArray();
            if (_command is null)
            {
                if (bytes.SequenceEqual(new byte[] { 0x7F })) { Reply([0x79]); return Task.CompletedTask; }
                if (bytes.Length != 2 || (bytes[0] ^ bytes[1]) != 255) throw new InvalidDataException("Invalid STM command");
                switch (bytes[0])
                {
                    case 0: Reply([0x79, 6, 0x31, 0, 2, 0x11, 0x21, 0x31, 0x44, 0x79]); break;
                    case 2: Reply([0x79, 1, 4, 0x10, 0x79]); break;
                    case 0x44: case 0x31: case 0x11: case 0x21: _command = bytes[0]; _address = null; Reply([0x79]); break;
                    default: throw new InvalidDataException("Unexpected STM command");
                }
            }
            else if (_command == 0x44)
            {
                if (!bytes.SequenceEqual(new byte[] { 0xFF, 0xFF, 0 })) throw new InvalidDataException();
                Array.Fill(Flash, (byte)0xFF); _command = null; Reply([0x79]);
            }
            else if (_address is null)
            {
                if (bytes.Length != 5 || bytes.Aggregate((byte)0, (a, b) => (byte)(a ^ b)) != 0) throw new InvalidDataException("Bad address checksum");
                _address = BinaryPrimitives.ReadUInt32BigEndian(bytes); Reply([0x79]);
                if (_command == 0x21) { GoAddress = _address.Value; _command = null; }
            }
            else if (_command == 0x31)
            {
                if (bytes.Length != bytes[0] + 3 || bytes.Aggregate((byte)0, (a, b) => (byte)(a ^ b)) != 0) throw new InvalidDataException("Bad write checksum");
                bytes.AsSpan(1, bytes[0] + 1).CopyTo(Flash.AsSpan(checked((int)(_address.Value - 0x08000000))));
                _command = null; Reply([0x79]);
            }
            else if (_command == 0x11)
            {
                if (bytes.Length != 2 || (bytes[0] ^ bytes[1]) != 255) throw new InvalidDataException();
                var count = bytes[0] + 1; var result = new byte[count + 1]; result[0] = 0x79;
                Flash.AsSpan(checked((int)(_address.Value - 0x08000000)), count).CopyTo(result.AsSpan(1));
                _command = null; Reply(result);
            }
            return Task.CompletedTask;
        }
        private void Reply(byte[] bytes)
        { for (var i = 0; i < bytes.Length; i += 5) DataReceived?.Invoke(this, bytes.AsMemory(i, Math.Min(5, bytes.Length - i))); }
    }
}
