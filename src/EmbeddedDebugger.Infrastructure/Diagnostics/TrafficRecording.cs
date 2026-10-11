using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace EmbeddedDebugger.Infrastructure.Diagnostics;

public sealed record TrafficRecord(DateTimeOffset Timestamp, bool IsReceived, byte[] Data);
public sealed record TrafficRecording(string Source, IReadOnlyList<TrafficRecord> Records);

/// <summary>Versioned raw traffic format with bounded allocation and per-record integrity checks.</summary>
public static class TrafficRecordingFile
{
    private static readonly byte[] Magic = "EDBGTRF1"u8.ToArray();
    public const int MaxRecordBytes = 1024 * 1024;
    public const int MaxDataBytes = 64 * 1024 * 1024;
    public const int MaxRecords = 100_000;

    internal static async Task WriteHeaderAsync(Stream stream, string source, CancellationToken token)
    {
        var name = Encoding.UTF8.GetBytes(source);
        if (name.Length > 4096) throw new ArgumentException("会话名称过长。");
        await stream.WriteAsync(Magic, token).ConfigureAwait(false);
        var length = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, name.Length);
        await stream.WriteAsync(length, token).ConfigureAwait(false);
        await stream.WriteAsync(name, token).ConfigureAwait(false);
    }

    internal static async Task WriteRecordAsync(Stream stream, TrafficRecord record, CancellationToken token)
    {
        var header = new byte[13];
        header[0] = record.IsReceived ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(1), record.Timestamp.UtcTicks);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(9), record.Data.Length);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(header); hash.AppendData(record.Data);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(record.Data, token).ConfigureAwait(false);
        await stream.WriteAsync(hash.GetHashAndReset(), token).ConfigureAwait(false);
    }

    internal static async Task WriteEndAsync(Stream stream, int count, CancellationToken token)
    {
        var end = new byte[5]; end[0] = 255; BinaryPrimitives.WriteInt32LittleEndian(end.AsSpan(1), count);
        await stream.WriteAsync(end, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public static async Task SaveAsync(Stream stream, TrafficRecording recording, CancellationToken token = default)
    {
        // Reject oversized exports before writing any bytes.
        long size = 0, count = 0;
        foreach (var record in recording.Records)
        {
            size += record.Data.Length;
            count += Math.Max(1, (record.Data.Length + (long)MaxRecordBytes - 1) / MaxRecordBytes);
            if (size > MaxDataBytes || count > MaxRecords) throw new InvalidDataException("录制上限为 64 MiB / 100,000 条。");
        }
        token.ThrowIfCancellationRequested();
        await WriteHeaderAsync(stream, recording.Source, token).ConfigureAwait(false);
        foreach (var record in recording.Records)
        {
            for (var offset = 0; offset < Math.Max(1, record.Data.Length); offset += MaxRecordBytes)
            {
                var part = record.Data.AsSpan(offset, Math.Min(MaxRecordBytes, record.Data.Length - offset)).ToArray();
                await WriteRecordAsync(stream, record with { Data = part }, token).ConfigureAwait(false);
            }
        }
        await WriteEndAsync(stream, (int)count, token).ConfigureAwait(false);
    }

    public static async Task<TrafficRecording> LoadAsync(Stream stream, CancellationToken token = default)
    {
        try
        {
            var signature = new byte[8]; await stream.ReadExactlyAsync(signature, token).ConfigureAwait(false);
            if (!signature.AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("不是受支持的 EDBG 原始流量录制文件。");
            var number = new byte[4]; await stream.ReadExactlyAsync(number, token).ConfigureAwait(false);
            var nameLength = BinaryPrimitives.ReadInt32LittleEndian(number);
            if (nameLength is < 0 or > 4096) throw new InvalidDataException("录制名称长度无效。");
            var name = new byte[nameLength]; await stream.ReadExactlyAsync(name, token).ConfigureAwait(false);
            var records = new List<TrafficRecord>();
            long total = 0;
            var header = new byte[13]; var digest = new byte[32];
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                await stream.ReadExactlyAsync(header.AsMemory(0, 1), token).ConfigureAwait(false);
                if (header[0] == 255)
                {
                    await stream.ReadExactlyAsync(number, token).ConfigureAwait(false);
                    if (BinaryPrimitives.ReadInt32LittleEndian(number) != records.Count || await stream.ReadAsync(header.AsMemory(0, 1), token).ConfigureAwait(false) != 0)
                        throw new InvalidDataException("录制结束标记无效。");
                    return new(new UTF8Encoding(false, true).GetString(name), records);
                }
                if (header[0] > 1) throw new InvalidDataException("录制方向字段无效。");
                await stream.ReadExactlyAsync(header.AsMemory(1), token).ConfigureAwait(false);
                var ticks = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(1));
                var size = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(9));
                if (ticks < 0 || ticks > DateTimeOffset.MaxValue.UtcTicks || size is < 0 or > MaxRecordBytes || (total += size) > MaxDataBytes || records.Count >= MaxRecords)
                    throw new InvalidDataException("录制条目无效或超过 64 MiB / 100,000 条限制。");
                var data = new byte[size]; await stream.ReadExactlyAsync(data, token).ConfigureAwait(false);
                await stream.ReadExactlyAsync(digest, token).ConfigureAwait(false);
                hash.AppendData(header); hash.AppendData(data);
                if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), digest)) throw new InvalidDataException("录制校验失败，文件可能损坏。");
                records.Add(new(new DateTimeOffset(ticks, TimeSpan.Zero), header[0] == 1, data));
            }
        }
        catch (EndOfStreamException ex) { throw new InvalidDataException("录制未正常结束或文件已被截断。", ex); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("会话名称编码无效。", ex); }
    }

    public static async Task ReplayAsync(IReadOnlyList<TrafficRecord> records, double speed,
        Func<TrafficRecord, CancellationToken, Task> consume, CancellationToken token = default)
    {
        if (!double.IsFinite(speed) || speed is < 0.1 or > 100) throw new ArgumentOutOfRangeException(nameof(speed));
        if (records.Count == 0) return;
        var clock = Stopwatch.StartNew();
        double due = 0;
        foreach (var record in records)
        {
            token.ThrowIfCancellationRequested();
            due = Math.Max(due, Math.Max(0, (record.Timestamp - records[0].Timestamp).TotalMilliseconds / speed));
            while (due - clock.Elapsed.TotalMilliseconds > 1)
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, due - clock.Elapsed.TotalMilliseconds)), token).ConfigureAwait(false);
            await consume(record, token).ConfigureAwait(false);
        }
    }
}

/// <summary>Nonblocking UI producer; never silently drops bytes when disk IO falls behind.</summary>
public sealed class TrafficRecordingWriter : IAsyncDisposable
{
    private readonly Channel<TrafficRecord> _queue = Channel.CreateBounded<TrafficRecord>(new BoundedChannelOptions(256) { SingleReader = true });
    private long _pendingBytes, _totalBytes;
    private int _count;
    private Exception? _failure;
    public Exception? Failure => Volatile.Read(ref _failure);
    public Task Completion { get; }
    public TrafficRecordingWriter(Stream ownedStream, string source) => Completion = RunAsync(ownedStream, source);

    public bool TryAppend(DateTimeOffset timestamp, bool received, ReadOnlyMemory<byte> data)
    {
        if (Completion.IsCompleted || Failure is not null) return false;
        for (var offset = 0; offset < Math.Max(1, data.Length); offset += TrafficRecordingFile.MaxRecordBytes)
        {
            var count = Math.Min(TrafficRecordingFile.MaxRecordBytes, data.Length - offset);
            if (_totalBytes + count > TrafficRecordingFile.MaxDataBytes || _count >= TrafficRecordingFile.MaxRecords || Interlocked.Read(ref _pendingBytes) + count > 8 * 1024 * 1024)
                return Fail(new IOException("录制已停止：达到 64 MiB / 100,000 条上限，或磁盘写入跟不上收发速度。已接受的数据会保存。"));
            Interlocked.Add(ref _pendingBytes, count);
            if (!_queue.Writer.TryWrite(new(timestamp, received, data.Slice(offset, count).ToArray())))
            {
                Interlocked.Add(ref _pendingBytes, -count);
                return Fail(new IOException("录制写入队列已满或已关闭，已停止录制。"));
            }
            _totalBytes += count; _count++;
        }
        return true;
    }

    private bool Fail(Exception error)
    {
        Interlocked.CompareExchange(ref _failure, error, null);
        _queue.Writer.TryComplete();
        return false;
    }
    private async Task RunAsync(Stream ownedStream, string source)
    {
        await using (ownedStream.ConfigureAwait(false))
        {
            try
            {
                await TrafficRecordingFile.WriteHeaderAsync(ownedStream, source, default).ConfigureAwait(false);
                var written = 0;
                await foreach (var record in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    await TrafficRecordingFile.WriteRecordAsync(ownedStream, record, default).ConfigureAwait(false);
                    Interlocked.Add(ref _pendingBytes, -record.Data.Length); written++;
                }
                await TrafficRecordingFile.WriteEndAsync(ownedStream, written, default).ConfigureAwait(false);
            }
            catch (Exception ex) { Fail(ex); }
        }
    }
    public async ValueTask DisposeAsync() { _queue.Writer.TryComplete(); await Completion.ConfigureAwait(false); }
}
