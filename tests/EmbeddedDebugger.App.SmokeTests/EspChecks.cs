using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EmbeddedDebugger.App;
using EmbeddedDebugger.Core.Abstractions;
using EmbeddedDebugger.Core.Models;
using EmbeddedDebugger.Infrastructure.Firmware;

namespace EmbeddedDebugger.App.SmokeTests;

internal static class EspChecks
{
    internal static async Task RunAsync()
    {
        foreach (var (magic, name, extended) in new (uint, string, bool)[]
        {
            (0xFFF0C101, "ESP8266", false), (0x00F01D83, "ESP32", false), (0x7C6, "ESP32-S2", true),
            (9, "ESP32-S3", true), (0x6921506F, "ESP32-C3", true), (0x6F51306F, "ESP32-C2", true),
            (0x2CE0806F, "ESP32-C6", true), (0xD7B73E80, "ESP32-H2", true)
        })
        {
            var rom = new RomFixture(magic);
            await using var client = new EspRomBootloaderClient(rom);
            await client.ConnectAsync();
            Require(client.IsConnected && client.ChipFamily == name && client.ChipId == magic && client.FlashSize == 4 * 1024 * 1024,
                $"{name} READ_REG header identification, JEDEC size, fragmented SLIP / boot noise / duplicate SYNC");
            var a = Enumerable.Range(0, 1301).Select(i => (byte)i).ToArray();
            var b = new byte[] { 0xC0, 0xDB, 0x20, 0xC0, 0xDB };
            await client.FlashImagesAsync([new(0x1000, a, "a.bin"), new(0x3000, b, "b.bin")]);
            Require(rom.Flash.AsSpan(0x1000, a.Length).SequenceEqual(a) && rom.Flash.AsSpan(0x3000, b.Length).SequenceEqual(b), $"{name} multi-file writes preserve escaped bytes");
            Require(rom.BeginLengths.All(size => size == (extended ? 20 : 16)) && rom.EndCount == 0 && rom.Verified >= 2,
                $"{name} correct FLASH_BEGIN shape, verification before exit, no premature FLASH_END");
            if (name == "ESP8266") Require(rom.EraseArguments.Contains(4096), "ESP8266 ROM erase-size workaround");
            Require((await client.ReadFlashAsync(0x1000, 131)).SequenceEqual(a.Take(131)), $"{name} Flash backup reads arbitrary length");
            await client.EraseAsync(0x1000, 4096);
            Require(rom.Flash.AsSpan(0x1000, 4096).IndexOfAnyExcept((byte)0xFF) < 0, $"{name} range erase is verified");
        }

        await using (var client = new EspRomBootloaderClient(new RomFixture(0xDEADBEEF)))
        {
            await ThrowsAsync<NotSupportedException>(() => client.ConnectAsync(false), "Unknown ROM rejects operations before erase");
            Require(!client.IsConnected, "Unknown chip never becomes connected");
        }
        foreach (var magic in new uint[] { 0x00F01D83, 9 })
        {
            var rom = new RomFixture(magic) { SecureBoot = true };
            await using var client = new EspRomBootloaderClient(rom);
            await client.ConnectAsync(false);
            await ThrowsAsync<InvalidOperationException>(() => client.FlashImagesAsync([new(0, [1], "x")]), "Secure Boot prevents writes");
            Require(rom.BeginLengths.Count == 0, "Secure device received no FLASH_BEGIN");
        }
        {
            var rom = new RomFixture(9);
            await using var client = new EspRomBootloaderClient(rom);
            await client.ConnectAsync(false);
            await ThrowsAsync<ArgumentException>(() => client.FlashImagesAsync([new(0x1000, new byte[5000], "a"), new(0x2000, [1], "b")]), "Overlapping erase sectors rejected before writing");
            Require(rom.BeginLengths.Count == 0, "Invalid image layout issued no destructive command");
            await client.ConnectAsync(false);
            var wrong = new byte[24]; wrong[0] = 0xE9; wrong[1] = 1; wrong[12] = 5;
            await ThrowsAsync<InvalidDataException>(() => client.FlashImagesAsync([new(0x10000, wrong, "c3-image")]), "Wrong-chip ESP image rejected before writing");
        }
        {
            var rom = new RomFixture(0x00F01D83);
            await using var client = new EspRomBootloaderClient(rom);
            await client.ConnectAsync(false);
            rom.FailCommand = 3;
            await ThrowsAsync<IOException>(() => client.FlashImagesAsync([new(0x1000, [1], "bad")]), "ROM error status propagates and invalidates session");
            Require(!client.IsConnected && rom.EndCount == 0, "Failed write never reports success or reboots");
            rom.FailCommand = null;
            await client.ConnectAsync(false);
            rom.BadDigest = true;
            await ThrowsAsync<InvalidDataException>(() => client.FlashImagesAsync([new(0x1000, [1], "bad-digest")]), "MD5 mismatch is a failed burn");
            rom.BadDigest = false;
            await client.ConnectAsync(false);
            rom.WithholdCommand = 0x0E;
            await ThrowsAsync<TimeoutException>(() => client.ReadFlashAsync(0, 64), "Deadline becomes TimeoutException instead of user cancellation");
            rom.WithholdCommand = null;
            await client.ConnectAsync(false);
            rom.BadLengthCommand = 0x0E;
            await ThrowsAsync<InvalidDataException>(() => client.ReadFlashAsync(0, 64), "Truncated response rejected");
            rom.BadLengthCommand = null;
            await client.ConnectAsync(false);
            rom.BadEscapeCommand = 0x0E;
            await ThrowsAsync<InvalidDataException>(() => client.ReadFlashAsync(0, 64), "Invalid SLIP escape rejected");
        }
        {
            var rom = new RomFixture(9);
            await using var client = new EspRomBootloaderClient(rom);
            await client.ConnectAsync(false);
            using var cancel = new CancellationTokenSource();
            rom.WithholdCommand = 3;
            rom.CommandSent = code =>
            {
                if (code != 3) return;
                try { client.StartMonitor((_, _) => { }); throw new InvalidOperationException("ROM/monitor interlock missing"); }
                catch (InvalidOperationException ex) when (ex.Message.Contains("ROM 操作", StringComparison.Ordinal)) { }
                cancel.Cancel();
            };
            await ThrowsAsync<OperationCanceledException>(() => client.FlashImagesAsync([new(0, new byte[3000], "cancel")], cancellationToken: cancel.Token), "Cancellation stops blocked serial read");
            Require(!client.IsConnected && rom.WriteCount == 1, "Cancelled write sends no following block");
            rom.WithholdCommand = null; rom.CommandSent = null;
            await client.ConnectAsync(false);
            var monitorBytes = new List<byte>();
            client.StartMonitor((_, data) => monitorBytes.AddRange(data.ToArray()));
            rom.Emit([1, 2, 3]);
            Require(monitorBytes.SequenceEqual(new byte[] { 1, 2, 3 }) && !client.IsConnected, "Monitor receives raw bytes and disables ROM commands");
            await ThrowsAsync<InvalidOperationException>(() => client.ReadFlashAsync(0, 4), "Monitor and ROM cannot share the stream");
            client.StopMonitor();
            await client.ConnectAsync(false);
            Require(client.IsConnected, "Reconnect works after cancel/monitor without replacing client");
        }
        CheckPartitions();
        await CheckWindowAsync();
        Console.WriteLine("PASS ESP ROM protocol and workspace checks (simulated hardware; no physical writes)");
    }

    private static void CheckPartitions()
    {
        var bytes = Enumerable.Repeat((byte)0xFF, 4096).ToArray();
        Entry(bytes.AsSpan(0, 32), 1, 2, 0x9000, 0x6000, "nvs");
        Entry(bytes.AsSpan(32, 32), 0, 0, 0x10000, 0x100000, "factory");
        bytes[64] = bytes[65] = 0xEB;
        MD5.HashData(bytes.AsSpan(0, 64)).CopyTo(bytes, 80);
        var partitions = EspPartitionTable.Parse(bytes, 4 * 1024 * 1024);
        Require(partitions.Count == 2 && partitions[0].TypeText.Contains("NVS", StringComparison.Ordinal) && EspPartitionTable.ToCsv(partitions).Contains("factory", StringComparison.Ordinal), "ESP-IDF table parsing, MD5, typed labels and CSV export");
        bytes[90] ^= 1;
        Throws<InvalidDataException>(() => EspPartitionTable.Parse(bytes), "Corrupted partition MD5 rejected");
        Entry(bytes.AsSpan(32, 32), 1, 2, 0xA000, 0x1000, "overlap");
        Throws<InvalidDataException>(() => EspPartitionTable.Parse(bytes), "Overlapping partitions rejected");
    }
    private static void Entry(Span<byte> entry, byte type, byte subtype, uint address, uint size, string label)
    {
        entry.Clear(); entry[0] = 0xAA; entry[1] = 0x50; entry[2] = type; entry[3] = subtype;
        BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], address); BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], size);
        Encoding.UTF8.GetBytes(label).CopyTo(entry[12..]);
    }

    private static async Task CheckWindowAsync()
    {
        var rom = new RomFixture(9);
        var window = new EspConnectToolWindow(new FixtureService(rom));
        window.Show();
        await Task.Delay(50);
        ((Button)window.FindName("ConnectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (var count = 0; count < 100 && !((Button)window.FindName("DisconnectButton")).IsEnabled; count++) await Task.Delay(10);
        Require(((TextBlock)window.FindName("ChipFamilyText")).Text == "ESP32-S3", "ESP workspace connects using real ROM client");
        ((Button)window.FindName("MonitorButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (var count = 0; count < 100 && !((Button)window.FindName("SendMonitorButton")).IsEnabled; count++) await Task.Delay(10);
        var utf8 = Encoding.UTF8.GetBytes("你好 ESP");
        rom.Emit(utf8[..1]); rom.Emit(utf8[1..4]); rom.Emit(utf8[4..]);
        await Task.Delay(150);
        Require(((TextBox)window.FindName("MonitorBox")).Text == "你好 ESP", "Monitor decodes split UTF-8 without corruption");
        var folder = Path.Combine(Path.GetTempPath(), "EmbeddedDebugger-EspChecks"); Directory.CreateDirectory(folder);
        window.Width = window.MinWidth; window.Height = window.MinHeight;
        var tabs = (TabControl)window.FindName("WorkTabs");
        for (var index = 0; index < tabs.Items.Count; index++)
        {
            tabs.SelectedIndex = index; window.UpdateLayout(); await Task.Delay(20);
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(folder, $"esp-min-{index}.png")); png.Save(output);
        }
        window.Close();
        Console.WriteLine("ESP layout captures: " + folder);
    }

    private static void Require(bool condition, string description) { if (!condition) throw new InvalidOperationException("FAIL " + description); Console.WriteLine("PASS " + description); }
    private static async Task ThrowsAsync<T>(Func<Task> operation, string description) where T : Exception
    {
        try { await operation(); } catch (T) { Console.WriteLine("PASS " + description); return; }
        throw new InvalidOperationException("FAIL " + description);
    }
    private static void Throws<T>(Action operation, string description) where T : Exception
    {
        try { operation(); } catch (T) { Console.WriteLine("PASS " + description); return; }
        throw new InvalidOperationException("FAIL " + description);
    }
    private sealed class FixtureService(RomFixture rom) : ISerialPortService
    {
        public Task<IReadOnlyList<SerialPortDescriptor>> ScanAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SerialPortDescriptor>>([new("SIM", "Simulated ESP UART", null, null)]);
        public ISerialPortChannel CreateChannel(SerialPortSettings settings) { rom.Settings = settings; return rom; }
    }

    // This emulator parses complete wire packets independently of the client and models
    // the ROM's response header/status distinction, SPI register side effects and flash.
    private sealed class RomFixture(uint magic) : ISerialPortChannel, ISerialPortControl
    {
        public string Id => "ESP-ROM-fixture";
        public SerialPortSettings Settings { get; set; } = new() { PortName = "SIM" };
        public bool IsOpen { get; private set; } = true;
        public event EventHandler<ReadOnlyMemory<byte>>? DataReceived;
        public byte[] Flash { get; } = Enumerable.Repeat((byte)0xFF, 4 * 1024 * 1024).ToArray();
        public List<int> BeginLengths { get; } = [];
        public List<uint> EraseArguments { get; } = [];
        public int EndCount { get; private set; }
        public int Verified { get; private set; }
        public int WriteCount { get; private set; }
        public bool SecureBoot { get; init; }
        public bool BadDigest { get; set; }
        public byte? FailCommand { get; set; }
        public byte? WithholdCommand { get; set; }
        public byte? BadLengthCommand { get; set; }
        public byte? BadEscapeCommand { get; set; }
        public Action<byte>? CommandSent { get; set; }
        private readonly Dictionary<uint, uint> _registers = [];
        private uint _flashAddress;
        private bool Is8266 => magic == 0xFFF0C101;
        private uint SpiBase => magic switch { 0xFFF0C101 => 0x60000200, 0x00F01D83 => 0x3FF42000, 0x7C6 => 0x3F402000, 0x2CE0806F or 0xD7B73E80 => 0x60003000, _ => 0x60002000 };
        private uint User => SpiBase + (Is8266 || magic == 0x00F01D83 ? 0x1Cu : 0x18u);
        private uint Data => SpiBase + (Is8266 ? 0x40u : magic == 0x00F01D83 ? 0x80u : 0x58u);
        public Task OpenAsync(CancellationToken cancellationToken = default) { IsOpen = true; return Task.CompletedTask; }
        public Task CloseAsync(CancellationToken cancellationToken = default) { IsOpen = false; return Task.CompletedTask; }
        public Task SetControlLinesAsync(bool dtrEnable, bool rtsEnable, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetBaudRateAsync(int baudRate, CancellationToken cancellationToken = default) { Settings = new() { PortName = Settings.PortName, BaudRate = baudRate }; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { IsOpen = false; return ValueTask.CompletedTask; }
        public void Emit(byte[] data) => DataReceived?.Invoke(this, data);
        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wire = data.Span; RequirePacket(wire[0] == 0xC0 && wire[^1] == 0xC0);
            var decoded = new List<byte>();
            for (var i = 1; i < wire.Length - 1; i++)
            {
                if (wire[i] != 0xDB) decoded.Add(wire[i]);
                else decoded.Add(wire[++i] switch { 0xDC => 0xC0, 0xDD => 0xDB, _ => throw new InvalidDataException("Bad host SLIP") });
            }
            var packet = decoded.ToArray(); var opcode = packet[1]; var payload = packet[8..];
            RequirePacket(packet[0] == 0 && BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2, 2)) == payload.Length);
            if (opcode == 3) WriteCount++;
            CommandSent?.Invoke(opcode);
            if (WithholdCommand == opcode) return Task.CompletedTask;
            if (FailCommand == opcode) { Respond(opcode, 0, [], 7); return Task.CompletedTask; }
            switch (opcode)
            {
                case 8:
                    RequirePacket(payload.Length == 36 && payload[0] == 7 && payload[2] == 0x12 && payload[4..].All(value => value == 0x55));
                    Emit(Encoding.ASCII.GetBytes("boot: UART download\r\n"));
                    for (var index = 0; index < 8; index++) Respond(opcode, 0, []);
                    break;
                case 0x0A:
                    var address = Word(payload, 0);
                    Respond(opcode, address == 0x40001000 ? magic : address == 0x3FF5A018 && SecureBoot ? 0x10u : _registers.GetValueOrDefault(address), []);
                    break;
                case 9:
                    var register = Word(payload, 0); _registers[register] = Word(payload, 4);
                    if (register == SpiBase && _registers[register] == 1 << 18)
                    {
                        var command = _registers[User + 8] & 255;
                        if (command == 0x9F) _registers[Data] = 0x1640EF;
                        else if (command == 3)
                        {
                            var offset = (int)(_registers[SpiBase + 4] >> 8);
                            for (var index = 0; index < 64; index += 4) _registers[Data + (uint)index] = Word(Flash, offset + index);
                            Verified++;
                        }
                        else throw new InvalidDataException("Unexpected SPI operation");
                        _registers[register] = 0;
                    }
                    Respond(opcode, 0, []); break;
                case 0x0D: RequirePacket(payload.Length == 8); Respond(opcode, 0, []); break;
                case 0x0B: RequirePacket(payload.Length == 24 && Word(payload, 4) == Flash.Length); Respond(opcode, 0, []); break;
                case 0x14: var security = new byte[20]; security[0] = SecureBoot ? (byte)1 : (byte)0; Respond(opcode, 0, security); break;
                case 2:
                    BeginLengths.Add(payload.Length); EraseArguments.Add(Word(payload, 0));
                    _flashAddress = Word(payload, 12);
                    var length = (int)Word(payload, 4) * 1024;
                    if (length > 0) Flash.AsSpan((int)_flashAddress, (length + 4095) / 4096 * 4096).Fill(0xFF);
                    Respond(opcode, 0, []); break;
                case 3:
                    RequirePacket(Word(payload, 0) == 1024 && payload.Length == 1040);
                    byte checksum = 0xEF; foreach (var item in payload[16..]) checksum ^= item;
                    RequirePacket(checksum == Word(packet, 4));
                    payload.AsSpan(16).CopyTo(Flash.AsSpan((int)(_flashAddress + Word(payload, 4) * 1024)));
                    Respond(opcode, 0, []); break;
                case 4: EndCount++; Respond(opcode, 0, []); break;
                case 0x0E:
                    var read = new byte[64]; Flash.AsSpan((int)Word(payload, 0), (int)Word(payload, 4)).CopyTo(read);
                    Respond(opcode, 0, read); break;
                case 0x13:
                    var digest = MD5.HashData(Flash.AsSpan((int)Word(payload, 0), (int)Word(payload, 4)));
                    if (BadDigest) digest[0] ^= 1;
                    Verified++; Respond(opcode, 0, Encoding.ASCII.GetBytes(Convert.ToHexString(digest))); break;
                default: throw new InvalidDataException($"Unexpected ROM opcode 0x{opcode:X2}");
            }
            return Task.CompletedTask;
        }
        private void Respond(byte opcode, uint value, byte[] payload, byte error = 0)
        {
            if (BadEscapeCommand == opcode) { Emit([0xC0, 1, opcode, 0xDB, 0xDA, 0xC0]); return; }
            var packet = new byte[8 + payload.Length + (Is8266 ? 2 : 4)];
            packet[0] = 1; packet[1] = opcode;
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), (ushort)(packet.Length - 8 + (BadLengthCommand == opcode ? 1 : 0)));
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), value); payload.CopyTo(packet, 8);
            if (error != 0) { packet[8 + payload.Length] = 1; packet[9 + payload.Length] = error; }
            var encoded = new List<byte> { 0xC0 };
            foreach (var b in packet)
            {
                if (b == 0xC0) encoded.AddRange([0xDB, 0xDC]);
                else if (b == 0xDB) encoded.AddRange([0xDB, 0xDD]);
                else encoded.Add(b);
            }
            encoded.Add(0xC0);
            var bytes = encoded.ToArray();
            for (var index = 0; index < bytes.Length; index += 3) Emit(bytes[index..Math.Min(index + 3, bytes.Length)]);
        }
        private static uint Word(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
        private static void RequirePacket(bool condition) { if (!condition) throw new InvalidDataException("Invalid host ROM packet"); }
    }
}
