using System.IO;
using EmbedDesk.App;
using EmbedDesk.Infrastructure.Firmware;

namespace EmbedDesk.App.SmokeTests;

internal static class Ch347Checks
{
    internal static async Task RunAsync()
    {
        var nor = new FakeFlash(false);
        await using (var client = await SpiFlashClient.IdentifyAsync(nor))
        {
            Require(client.Info.JedecId == "EF4015" && client.Info.Capacity == 2 * 1048576, "NOR JEDEC / geometry");
            var firmware = Enumerable.Range(0, 777).Select(index => (byte)(index * 13)).ToArray();
            await client.ProgramAndVerifyAsync(new MemoryStream(firmware), 4096);
            await using var backup = new MemoryStream();
            await client.BackupAsync(backup, 4096, 4096);
            Require(backup.ToArray()[..777].SequenceEqual(firmware) && backup.ToArray()[777..].All(value => value == 0xff), "NOR page writes, erase tail and verification");
            Require(nor.NorMemory[4095] == 0xa5 && nor.NorMemory[8192] == 0xa5, "NOR bounds preserve neighboring blocks");
            var writes = nor.DestructiveCommands;
            await ThrowsAsync<ArgumentException>(() => client.ProgramAndVerifyAsync(new MemoryStream([1]), 1), "unaligned image rejected");
            await ThrowsAsync<ArgumentOutOfRangeException>(() => client.EraseAsync(2097152, 4096), "overflow range rejected");
            Require(nor.DestructiveCommands == writes, "range rejection precedes erase");
            nor.Protected = true;
            await ThrowsAsync<IOException>(() => client.EraseAsync(0, 4096), "NOR protection refused");
            Require(nor.DestructiveCommands == writes, "protected chip never erased");
            nor.Protected = false; nor.CorruptRead = true;
            await ThrowsAsync<IOException>(() => client.ProgramAndVerifyAsync(new MemoryStream([3, 4]), 0), "readback mismatch stops programming");
            nor.CorruptRead = false;
            using var cts = new CancellationTokenSource(); nor.AfterErase = cts.Cancel;
            writes = nor.DestructiveCommands;
            await ThrowsAsync<OperationCanceledException>(() => client.EraseAsync(0, 8192, token: cts.Token), "cancel stops between erase blocks");
            Require(nor.DestructiveCommands == writes + 1, "cancellation does not erase the next block");
        }
        Require(nor.Disposed, "NOR transport disposed");

        var nand = new FakeFlash(true);
        await using (var client = await SpiFlashClient.IdentifyAsync(nand))
        {
            Require(client.Info.JedecId == "EFAA21" && nand.Configuration == 0x18, "NAND dummy-byte ID and ECC/buffer mode");
            nand.BadBlocks.Add(1);
            await ThrowsAsync<IOException>(() => client.ProgramAndVerifyAsync(new MemoryStream(new byte[131073]), 0), "bad block rejected before any destructive command");
            Require(nand.DestructiveCommands == 0 && nand.Configuration == 0x18, "bad-block scan is nondestructive and restores ECC");
            nand.BadBlocks.Clear();
            var firmware = Enumerable.Range(0, 5001).Select(index => (byte)(index % 251)).ToArray();
            await client.ProgramAndVerifyAsync(new MemoryStream(firmware), 0);
            Require(nand.Protection == 0x38, "NAND write protection restored");
            await using var image = new MemoryStream();
            await client.BackupAsync(image, 0, 6144);
            Require(image.ToArray()[..5001].SequenceEqual(firmware) && image.ToArray()[5001..].All(value => value == 0xff), "NAND page program and ECC readback");
            nand.EccErrorRow = 1;
            await ThrowsAsync<IOException>(() => client.BackupAsync(new MemoryStream(), 0, 4096), "uncorrectable ECC refuses normal backup");
            await using var raw = new MemoryStream();
            await client.BackupAsync(raw, 0, 4096, true);
            Require(raw.Length == 4224 && raw.ToArray()[2048..2112].All(value => value == 0xff) && nand.Configuration == 0x18, "raw backup includes OOB and restores ECC");
            nand.EccErrorRow = -1;
            nand.BadBlocks.Add(2);
            var badBlocks = await client.ScanBadBlocksAsync();
            Require(badBlocks.SequenceEqual(new[] { 2 }), "NAND bad-block scan reports physical indexes");
            nand.BadBlocks.Clear(); nand.ProgramFailure = true;
            await ThrowsAsync<IOException>(() => client.ProgramAndVerifyAsync(new MemoryStream([1]), 0), "NAND P_FAIL prevents success");
            Require(nand.Protection == 0x38, "NAND protection restored after failure");
        }
        Require(nand.Configuration == 0 && nand.Disposed, "NAND original configuration and transport restored on dispose");
        var unknown = new FakeFlash(false) { UnknownId = true };
        await ThrowsAsync<NotSupportedException>(() => SpiFlashClient.IdentifyAsync(unknown), "unknown JEDEC chip rejected");
        Require(unknown.DestructiveCommands == 0, "unknown chip never written");
        await unknown.DisposeAsync();
        var window = new Ch347ToolWindow(); window.Show(); await Task.Delay(40); window.Close();
        Console.WriteLine("PASS CH347 HID window, supported chip protocol simulation, bounds, bad blocks, ECC, cancellation and verification");
    }

    private static async Task ThrowsAsync<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); }
        catch (T) { Require(true, message); return; }
        throw new InvalidOperationException("FAIL " + message);
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("FAIL " + message); Console.WriteLine("PASS " + message); }

    private sealed class FakeFlash(bool nand) : ISpiFlashTransport
    {
        internal byte[] NorMemory { get; } = Enumerable.Repeat((byte)0xa5, 2 * 1048576).ToArray();
        internal HashSet<int> BadBlocks { get; } = [];
        private readonly Dictionary<int, byte[]> _pages = [];
        private byte[] _cache = Enumerable.Repeat((byte)0xff, 2112).ToArray();
        private bool _writeEnable;
        private byte _failure;
        private int _row;
        internal byte Configuration { get; private set; }
        internal byte Protection { get; private set; } = 0x38;
        internal bool Protected { get; set; }
        internal bool CorruptRead { get; set; }
        internal bool UnknownId { get; set; }
        internal bool ProgramFailure { get; set; }
        internal bool Disposed { get; private set; }
        internal int EccErrorRow { get; set; } = -1;
        internal int DestructiveCommands { get; private set; }
        internal Action? AfterErase { get; set; }

        public Task<byte[]> ExchangeAsync(byte[] command, int readLength, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] result = [];
            switch (command[0])
            {
                case 0x9f: result = UnknownId ? [0x01, 0x02, 0x03] : nand ? command.Length == 2 ? [0xef, 0xaa, 0x21] : [0, 0xef, 0xaa] : [0xef, 0x40, 0x15]; break;
                case 0x05: result = [(byte)((_writeEnable ? 2 : 0) | (Protected ? 0x1c : 0))]; break;
                case 0x35: result = [0]; break;
                case 0x0f: result = [command[1] switch { 0xb0 => Configuration, 0xa0 => Protection, _ => (byte)((_writeEnable ? 2 : 0) | _failure | (_row == EccErrorRow && (Configuration & 0x10) != 0 ? 0x20 : 0)) }]; break;
                case 0x1f: if (command[1] == 0xb0) Configuration = command[2]; else Protection = command[2]; break;
                case 0x06: _writeEnable = true; break;
                case 0x04: _writeEnable = false; break;
                case 0x13:
                    _row = Address(command); _failure = 0;
                    _cache = _pages.TryGetValue(_row, out var stored) ? stored.ToArray() : Enumerable.Repeat((byte)0xff, 2112).ToArray();
                    if (BadBlocks.Contains(_row / 64) && _row % 64 < 2) _cache[2048] = 0;
                    break;
                case 0x03:
                    result = nand ? _cache.AsSpan((command[1] << 8) | command[2], readLength).ToArray() : NorMemory.AsSpan(Address(command), readLength).ToArray();
                    if (CorruptRead && result.Length > 0) result[0] ^= 1;
                    break;
                case 0x20:
                    Require(_writeEnable, "NOR erase WREN");
                    Array.Fill(NorMemory, (byte)0xff, Address(command), 4096); _writeEnable = false; DestructiveCommands++; AfterErase?.Invoke(); break;
                case 0xd8:
                    Require(_writeEnable && (Protection & 0x7c) == 0, "NAND erase WREN / unlocked");
                    var first = Address(command); for (var i = 0; i < 64; i++) _pages.Remove(first + i);
                    _writeEnable = false; _failure = 0; DestructiveCommands++; AfterErase?.Invoke(); break;
                case 0x02:
                    if (!_writeEnable) throw new InvalidOperationException("Page program without WREN");
                    if (nand) { _cache = Enumerable.Repeat((byte)0xff, 2112).ToArray(); command.AsSpan(3).CopyTo(_cache); }
                    else
                    {
                        var address = Address(command); var data = command.AsSpan(4);
                        if (address / 256 != (address + data.Length - 1) / 256) throw new InvalidOperationException("Page boundary violated");
                        for (var i = 0; i < data.Length; i++) NorMemory[address + i] &= data[i];
                        _writeEnable = false; DestructiveCommands++;
                    }
                    break;
                case 0x10:
                    if (!_writeEnable) throw new InvalidOperationException("NAND program without WREN");
                    _pages[Address(command)] = _cache.ToArray(); _writeEnable = false; _failure = ProgramFailure ? (byte)8 : (byte)0; DestructiveCommands++; break;
                default: throw new InvalidOperationException($"Unexpected SPI command {command[0]:X2}");
            }
            if (result.Length != readLength) throw new InvalidOperationException($"Read length mismatch for SPI {command[0]:X2}");
            return Task.FromResult(result);
        }
        private static int Address(byte[] command) => (command[1] << 16) | (command[2] << 8) | command[3];
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
