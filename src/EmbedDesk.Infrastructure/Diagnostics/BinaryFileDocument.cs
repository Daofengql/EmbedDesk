using Microsoft.Win32.SafeHandles;

namespace EmbedDesk.Infrastructure.Diagnostics;

/// <summary>Paged, overwrite-only file editor. Source bytes remain unchanged until an atomic save.</summary>
public sealed class BinaryFileDocument : IAsyncDisposable
{
    private sealed record Change(long Offset, byte Original, byte Before, byte After);
    private FileStream _source;
    private readonly SortedList<long, (byte Original, byte Value)> _patches = new();
    private readonly List<Change[]> _undo = [];
    private readonly Stack<Change[]> _redo = new();
    private long _cachedOffset;
    private byte[] _cachedOriginal = [];
    private const int MaximumChanges = 65536;
    public string FilePath { get; private set; }
    public long Length => _source.Length;
    public int ModifiedBytes => _patches.Count;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public BinaryFileDocument(string path) { FilePath = Path.GetFullPath(path); _source = Open(FilePath); }
    private static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.RandomAccess);

    public async Task<byte[]> ReadAsync(long offset, int count, CancellationToken token = default)
    {
        if (offset < 0 || offset > Length || count < 0 || count > 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(offset));
        var bytes = new byte[(int)Math.Min(count, Length - offset)];
        await ReadExactlyAsync(_source.SafeFileHandle, bytes, offset, token);
        token.ThrowIfCancellationRequested(); _cachedOffset = offset; _cachedOriginal = (byte[])bytes.Clone();
        Apply(bytes, offset); return bytes;
    }

    public async Task ReplaceAsync(long offset, byte[] bytes, CancellationToken token = default)
    {
        if (bytes.Length is < 1 or > 256 || offset < 0 || offset > Length - bytes.Length) throw new ArgumentException("每次覆盖 1–256 字节，不能越过文件末尾。");
        var original = new byte[bytes.Length]; await ReadExactlyAsync(_source.SafeFileHandle, original, offset, token);
        var changes = new List<Change>();
        for (var i = 0; i < bytes.Length; i++)
        {
            var before = _patches.TryGetValue(offset + i, out var patch) ? patch.Value : original[i];
            if (before != bytes[i]) changes.Add(new(offset + i, original[i], before, bytes[i]));
        }
        Record(changes);
    }

    public void ReplaceCachedByte(long offset, byte value)
    {
        if (offset < _cachedOffset || offset - _cachedOffset >= _cachedOriginal.Length) throw new InvalidOperationException("请先载入目标字节。");
        var original = _cachedOriginal[(int)(offset - _cachedOffset)];
        var before = _patches.TryGetValue(offset, out var patch) ? patch.Value : original;
        if (before != value) Record([new(offset, original, before, value)]);
    }

    public long[] GetModifiedOffsets(long start, int count) => _patches.Keys.Skip(LowerBound(start)).TakeWhile(offset => offset - start < count).ToArray();

    private void Record(List<Change> changes)
    {
        if (_patches.Count + changes.Count(c => c.Original != c.After && !_patches.ContainsKey(c.Offset)) > MaximumChanges)
            throw new IOException("单次编辑会话最多保留 65,536 个修改字节，请先保存。");
        if (changes.Count == 0) return;
        foreach (var change in changes) Set(change.Offset, change.Original, change.After);
        _undo.Add(changes.ToArray()); if (_undo.Count > 128) _undo.RemoveAt(0); _redo.Clear();
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        var changes = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        foreach (var change in changes) Set(change.Offset, change.Original, change.Before);
        _redo.Push(changes);
    }
    public void Redo()
    {
        if (!_redo.TryPop(out var changes)) return;
        foreach (var change in changes) Set(change.Offset, change.Original, change.After);
        _undo.Add(changes);
    }
    private void Set(long offset, byte original, byte value) { if (original == value) _patches.Remove(offset); else _patches[offset] = (original, value); }

    private int LowerBound(long offset)
    {
        var left = 0; var right = _patches.Count;
        while (left < right) { var middle = left + (right - left) / 2; if (_patches.Keys[middle] < offset) left = middle + 1; else right = middle; }
        return left;
    }
    private void Apply(Span<byte> bytes, long offset)
    {
        for (var i = LowerBound(offset); i < _patches.Count && _patches.Keys[i] - offset < bytes.Length; i++) bytes[(int)(_patches.Keys[i] - offset)] = _patches.Values[i].Value;
    }

    public async Task<long> FindAsync(byte[] pattern, long start, CancellationToken token = default)
    {
        if (pattern.Length is < 1 or > 256 || start < 0 || start > Length) throw new ArgumentException("搜索内容应为 1–256 字节。");
        var buffer = new byte[65536 + pattern.Length - 1];
        var position = start;
        while (position < Length)
        {
            token.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, Length - position);
            await ReadExactlyAsync(_source.SafeFileHandle, buffer.AsMemory(0, count), position, token);
            Apply(buffer.AsSpan(0, count), position);
            var found = buffer.AsSpan(0, count).IndexOf(pattern);
            if (found >= 0) return position + found;
            position += Math.Min(65536, count);
        }
        return -1;
    }

    public async Task SaveAsync(string target, CancellationToken token = default)
    {
        var destination = Path.GetFullPath(target);
        var same = string.Equals(destination, FilePath, StringComparison.OrdinalIgnoreCase);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".embeddesk-edit-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                var buffer = new byte[1024 * 1024];
                for (long offset = 0; offset < Length; offset += buffer.Length)
                {
                    token.ThrowIfCancellationRequested(); var count = (int)Math.Min(buffer.Length, Length - offset);
                    await ReadExactlyAsync(_source.SafeFileHandle, buffer.AsMemory(0, count), offset, token); Apply(buffer.AsSpan(0, count), offset);
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                }
                await output.FlushAsync(token); output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            if (same) await _source.DisposeAsync();
            try { File.Move(temporary, destination, true); }
            catch { if (same) _source = Open(FilePath); throw; }
            if (!same) await _source.DisposeAsync();
            FilePath = destination; _source = Open(destination); _patches.Clear(); _undo.Clear(); _redo.Clear(); _cachedOriginal = [];
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task ReadExactlyAsync(SafeFileHandle file, Memory<byte> bytes, long offset, CancellationToken token)
    {
        while (!bytes.IsEmpty)
        {
            var count = await RandomAccess.ReadAsync(file, bytes, offset, token);
            if (count == 0) throw new EndOfStreamException("文件已改变或意外结束。");
            offset += count; bytes = bytes[count..];
        }
    }
    public ValueTask DisposeAsync() => _source.DisposeAsync();
}
