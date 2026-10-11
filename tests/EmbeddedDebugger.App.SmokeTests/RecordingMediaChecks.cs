using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EmbeddedDebugger.App.ViewModels;
using EmbeddedDebugger.Infrastructure.Bridging;
using EmbeddedDebugger.Infrastructure.Diagnostics;
using EmbeddedDebugger.Infrastructure.Firmware;
using EmbeddedDebugger.Infrastructure.Networking;

namespace EmbeddedDebugger.App.SmokeTests;

internal static class RecordingMediaChecks
{
    public static async Task RunAsync(MainWindowViewModel viewModel)
    {
        var now = DateTimeOffset.Now;
        var recording = new TrafficRecording("串口 · 二进制测试", [new(now, true, [0, 255, 13, 10]), new(now.AddMilliseconds(30), false, [1, 2, 3]), new(now.AddMilliseconds(50), true, [])]);
        using var encoded = new MemoryStream();
        await TrafficRecordingFile.SaveAsync(encoded, recording);
        encoded.Position = 0;
        var decoded = await TrafficRecordingFile.LoadAsync(encoded);
        Require(decoded.Source == recording.Source && decoded.Records.Zip(recording.Records).All(pair => pair.First.Timestamp == pair.Second.Timestamp && pair.First.IsReceived == pair.Second.IsReceived && pair.First.Data.SequenceEqual(pair.Second.Data)), "Raw recording round-trips timestamp/direction/NUL/non-UTF8 data");
        var damaged = encoded.ToArray(); damaged[^8] ^= 1;
        await RejectAsync(() => TrafficRecordingFile.LoadAsync(new MemoryStream(damaged)), "Damaged recording checksum rejected");
        await RejectAsync(() => TrafficRecordingFile.LoadAsync(new MemoryStream(encoded.ToArray()[..^1])), "Truncated recording/footer rejected");
        var oversized = encoded.ToArray();
        var headerOffset = 12 + System.Text.Encoding.UTF8.GetByteCount(recording.Source);
        BinaryPrimitives.WriteInt32LittleEndian(oversized.AsSpan(headerOffset + 9), int.MaxValue);
        await RejectAsync(() => TrafficRecordingFile.LoadAsync(new MemoryStream(oversized)), "Oversized untrusted record rejected before allocation");

        var retained = new MemoryStream();
        await using (var writer = new TrafficRecordingWriter(retained, "实时录制"))
        {
            Require(writer.TryAppend(now, true, new byte[TrafficRecordingFile.MaxRecordBytes + 10]), "Streaming capture accepts and chunks large frames");
        }
        var live = await TrafficRecordingFile.LoadAsync(new MemoryStream(retained.ToArray()));
        Require(live.Records.Count == 2 && live.Records.Sum(r => r.Data.Length) == TrafficRecordingFile.MaxRecordBytes + 10, "Live recording drains its queue and completes footer before close");
        await using (var failed = new TrafficRecordingWriter(new FailingStream(), "failure"))
        {
            await failed.Completion;
            Require(failed.Failure is IOException && !failed.TryAppend(now, true, new byte[] { 1 }), "Disk failure stops capture explicitly");
        }
        var delivered = new List<TrafficRecord>(); var clock = Stopwatch.StartNew();
        await TrafficRecordingFile.ReplayAsync(recording.Records, 2, (r, _) => { delivered.Add(r); return Task.CompletedTask; });
        Require(delivered.Count == 3 && clock.ElapsedMilliseconds >= 18, "Playback preserves ordering and scaled original timing");
        using (var cancellation = new CancellationTokenSource(50))
        {
            var canceled = false;
            try { await TrafficRecordingFile.ReplayAsync([new(now, false, [1]), new(now.AddHours(1), false, [2])], 1, (_, _) => Task.CompletedTask, cancellation.Token); }
            catch (OperationCanceledException) { canceled = true; }
            Require(canceled, "Playback cancellation interrupts long recorded gaps");
        }
        var buffer = new TerminalBufferViewModel();
        long captured = 0; buffer.TrafficCaptured += (_, _, data) => captured += data.Length;
        buffer.Add(now, true, new byte[TerminalBufferViewModel.MaxHistoryBytes + 19]);
        Require(captured == TerminalBufferViewModel.MaxHistoryBytes + 19 && buffer.Entries[0].Data.Length == TerminalBufferViewModel.MaxHistoryBytes, "Capture sees full raw data before bounded UI history truncates it");

        var serial = new FakeSerialService();
        await using (var remote = new RemoteSerialClient())
        await using (var sessions = new MainWindowViewModel(new TcpUdpTransportService(), serial, new FakeBridge(), remote))
        {
            await sessions.InitializeAsync();
            var session = sessions.SelectedSerialSession!;
            await session.OpenSelectedSerialCommand.ExecuteAsync(null);
            await session.ReplayRecordingAsync(recording.Records, 8, default);
            Require(serial.Writes.Count == 1 && serial.Writes[0].SequenceEqual(new byte[] { 1, 2, 3 }), "Serial playback sends TX only and preserves exact binary payload");
            var pending = session.ReplayRecordingAsync([new(now, true, []), new(now.AddHours(1), false, [99])], 1, default);
            Require(!session.RunQueueCommand.CanExecute(null) && !session.SendSelectedSerialCommand.CanExecute(null), "Active replay excludes competing send jobs");
            var writes = serial.Writes.Count;
            await session.SendShellInputAsync("blocked");
            Require(serial.Writes.Count == writes, "Shell input cannot interleave a recorded serial transfer");
            await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(3));
            var interrupted = false;
            try { await pending; } catch (OperationCanceledException) { interrupted = true; }
            Require(interrupted && !session.IsOpen && serial.Writes.Count == writes, "Closing a replaying serial session cancels future TX and releases the port");
        }

        foreach (var capacity in new[] { 256L << 20, 512L << 20, (512L << 20) + 512, (8L << 30) - 512, 8L << 30, 2L << 40 })
        {
            var layout = MediaImageOperations.GetFat32Layout(capacity, "EDBG");
            Require(layout.Clusters >= 65525 && (ulong)layout.FatSectors * 128 >= layout.Clusters + 2 && ((long)layout.PartitionStart + layout.TotalSectors) * 512 <= capacity, $"Valid FAT32 geometry at {capacity:N0} bytes");
        }
        var disk = new SparseDisk(256L << 20);
        await MediaImageOperations.FormatFat32Async(disk, disk.Length, "EDBG");
        var sector = new byte[512]; disk.Position = 2048 * 512;
        await disk.ReadExactlyAsync(sector);
        var sectorsPerCluster = sector[13];
        Require(sectorsPerCluster == 1 && sector[510] == 0x55 && sector[511] == 0xAA && BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(11)) == 512, "256 MiB FAT32 writes a valid boot sector and usable cluster geometry");
        disk.Position = 2049 * 512; await disk.ReadExactlyAsync(sector);
        var geometry = MediaImageOperations.GetFat32Layout(disk.Length, "EDBG");
        Require(BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(488)) == geometry.Clusters - 1, "FAT32 free cluster count reserves the root directory");
        var canceledDisk = new SparseDisk(256L << 20);
        var wasCanceled = false;
        try { await MediaImageOperations.FormatFat32Async(canceledDisk, canceledDisk.Length, "EDBG", token: new CancellationToken(true)); }
        catch (OperationCanceledException) { wasCanceled = true; }
        Require(wasCanceled && canceledDisk.WrittenBytes == 0, "Pre-canceled format does not write any media bytes");
        await using var source = new MemoryStream([1, 2, 3]);
        await RejectAsync(() => MediaImageOperations.WriteAndVerifyAsync(source, new CorruptDisk(), 4096, 512), "Image readback corruption is rejected");

        var recorderWindow = new TrafficRecordingWindow(viewModel); recorderWindow.Show();
        recorderWindow.UpdateLayout();
        Require(Descendants<TextBlock>(recorderWindow).Any(t => t.Text == "串口 · 串口 1") && !Descendants<TextBlock>(recorderWindow).Any(t => t.Text.Contains("SourceChoice {")), "ComboBox DisplayMemberPath renders the session label, not the internal record");
        await FileWorkspaceChecks.CaptureAsync(recorderWindow, "embedded-debugger-recording.png");
        recorderWindow.Close();
        var imageWindow = new ImageWriterWindow(); imageWindow.Show();
        await FileWorkspaceChecks.CaptureAsync(imageWindow, "embedded-debugger-image-writer.png");
        imageWindow.Close();
        Console.WriteLine("PASS recording/replay integrity, timing/cancellation and FAT32/media checks (no physical media written)");
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T value) yield return value;
            foreach (var value2 in Descendants<T>(child)) yield return value2;
        }
    }
    private static async Task RejectAsync(Func<Task> operation, string message)
    {
        try { await operation(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { Require(true, message); return; }
        throw new InvalidOperationException("FAIL " + message);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException("FAIL " + message); Console.WriteLine("PASS " + message); }
    private sealed class FailingStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException(new IOException("simulated disk failure"));
    }
    private sealed class CorruptDisk : MemoryStream
    {
        public CorruptDisk() => SetLength(4096);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await base.ReadAsync(buffer, cancellationToken);
            if (count > 0) buffer.Span[0] ^= 1;
            return count;
        }
    }
    private sealed class SparseDisk(long capacity) : Stream
    {
        private readonly Dictionary<long, byte[]> _sectors = [];
        public long WrittenBytes { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => capacity;
        public override long Position { get; set; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            count = (int)Math.Min(count, Length - Position);
            for (var i = 0; i < count; i++, Position++) buffer[offset + i] = _sectors.TryGetValue(Position / 512, out var sector) ? sector[Position % 512] : (byte)0;
            return count;
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Position < 0 || Position + count > Length) throw new IOException("Out of disk bounds");
            WrittenBytes += count;
            for (var i = 0; i < count; i++, Position++)
            {
                if (!_sectors.TryGetValue(Position / 512, out var sector))
                {
                    if (buffer[offset + i] == 0) continue;
                    _sectors.Add(Position / 512, sector = new byte[512]);
                }
                sector[Position % 512] = buffer[offset + i];
            }
        }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, _ => Length + offset };
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
