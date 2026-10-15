using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace EmbedDesk.Infrastructure.Networking;

public sealed partial class LanFileService
{
    private const int MaximumUploadWorkers = 4;
    private readonly SemaphoreSlim[] _uploadGates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private sealed record PendingBlock(long Offset, int Length);
    private sealed record ParallelCheckpoint(int Protocol, long Length, string Identity, long Offset, PendingBlock[] Pending, string? ReceiptVersion = null);
    private sealed record ParallelUploadState(int Protocol, long Offset, bool Completed);
    private SemaphoreSlim UploadGate(string file) => _uploadGates[(uint)StringComparer.OrdinalIgnoreCase.GetHashCode(file) % (uint)_uploadGates.Length];
    private async Task<ParallelCheckpoint?> ReadParallelCheckpointAsync(string path, CancellationToken token)
    {
        RejectLinks(path); if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 4096) return null;
        try
        {
            var state = JsonSerializer.Deserialize<ParallelCheckpoint>(await File.ReadAllBytesAsync(path, token).ConfigureAwait(false));
            if (state is null || state.Protocol != 2 || state.Offset < 0 || state.Offset > state.Length || !IsHash(state.Identity)
                || state.Pending is null || state.Pending.Length > MaximumUploadWorkers || state.Pending.Any(p => p is null || p.Offset < state.Offset || p.Offset >= state.Length
                    || p.Offset % TransferChunkSize != 0 || p.Length != Math.Min(TransferChunkSize, state.Length - p.Offset))) return null;
            return state;
        }
        catch (JsonException) { return null; }
    }
    private static async Task SaveParallelCheckpointAsync(string path, ParallelCheckpoint checkpoint, CancellationToken token)
    {
        RejectLinks(path); RejectLinks(path + ".new");
        await using (var output = new FileStream(path + ".new", FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
        {
            await output.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(checkpoint), token).ConfigureAwait(false);
            await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true);
        }
        File.Move(path + ".new", path, true);
    }
    private async Task HandleParallelUploadAsync(Stream stream, FileRequest request, string relative, string file, CancellationToken token)
    {
        var identity = request.Headers.GetValueOrDefault("X-EmbedDesk-File-Id", "");
        if (!IsHash(identity) || !long.TryParse(request.Headers.GetValueOrDefault("X-EmbedDesk-File-Length"), NumberStyles.None, CultureInfo.InvariantCulture, out var total) || total < 0)
            throw new InvalidDataException("文件版本或长度无效，请更新双方软件。");
        if (!Directory.Exists(Path.GetDirectoryName(file))) throw new DirectoryNotFoundException("目标目录不存在。");
        // Receive and verify outside the short disk/metadata gate so concurrent HTTP streams really overlap.
        byte[]? bytes = null; var offset = 0L;
        if (request.Url.AbsolutePath == "/api/upload-v2-chunk")
        {
            if (!long.TryParse(request.Headers.GetValueOrDefault("X-EmbedDesk-Offset"), NumberStyles.None, CultureInfo.InvariantCulture, out offset)
                || offset < 0 || offset >= total || offset % TransferChunkSize != 0 || request.Length != Math.Min(TransferChunkSize, total - offset)
                || request.BodyPrefix.Length > request.Length) throw new InvalidDataException("上传分块范围无效。");
            var expectedHash = request.Headers.GetValueOrDefault("X-EmbedDesk-Chunk-Hash", "");
            if (expectedHash.Length != 64 || !expectedHash.All(char.IsAsciiHexDigit)) throw new InvalidDataException("缺少分块校验。");
            bytes = new byte[(int)request.Length]; request.BodyPrefix.CopyTo(bytes, 0);
            using (var buffer = new MemoryStream(bytes, request.BodyPrefix.Length, bytes.Length - request.BodyPrefix.Length, true))
                await CopyAsync(stream, buffer, bytes.Length - request.BodyPrefix.Length, null, token).ConfigureAwait(false);
            if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("分块校验失败，此分块未提交。");
        }
        var gate = UploadGate(file); await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var paths = TransferPaths(file, "upload"); var metadata = paths.Metadata + ".v2"; RejectLinks(paths.Part);
            var state = await ReadParallelCheckpointAsync(metadata, token).ConfigureAwait(false);
            var matches = state is not null && state.Length == total && state.Identity == identity;
            var completed = matches && state!.Offset == total && state.ReceiptVersion is not null && !File.Exists(paths.Part)
                && File.Exists(file) && new FileInfo(file).Length == total && FileVersion(file, total) == state.ReceiptVersion;
            var overwrite = request.Headers.GetValueOrDefault("X-EmbedDesk-Overwrite") == "true";
            if (File.Exists(file) && !overwrite && !completed) throw new TransferConflictException("目标文件已存在，请确认覆盖后重试。");
            if (request.Url.AbsolutePath == "/api/upload-v2-state")
            {
                if (!completed)
                {
                    var prefix = matches && File.Exists(paths.Part) && new FileInfo(paths.Part).Length >= state!.Offset ? state!.Offset : 0;
                    // Retry from the committed prefix. At most four out-of-order blocks need retransmission.
                    await using (var part = new FileStream(paths.Part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 65536, true)) part.SetLength(prefix);
                    CheckDiskSpace(paths.Part, total - prefix, total);
                    state = new(2, total, identity, prefix, []);
                    await SaveParallelCheckpointAsync(metadata, state, token).ConfigureAwait(false);
                }
                await RespondAsync(stream, 200, JsonSerializer.SerializeToUtf8Bytes(new ParallelUploadState(2, state!.Offset, completed)), token).ConfigureAwait(false); return;
            }
            if (!matches || state is null) throw new TransferConflictException("上传版本已变化，请重新发起任务。");
            if (completed) { await RespondAsync(stream, 204, [], token).ConfigureAwait(false); return; }
            if (!File.Exists(paths.Part) || new FileInfo(paths.Part).Length < state.Offset) throw new TransferConflictException("续传数据缺失，请重新发起任务。");
            if (bytes is not null)
            {
                if (offset < state.Offset || state.Pending.Any(p => p.Offset == offset))
                { await RespondAsync(stream, 204, [], token).ConfigureAwait(false); return; }
                if ((offset - state.Offset) / TransferChunkSize >= MaximumUploadWorkers) throw new TransferConflictException("并行上传窗口超限，请重新查询进度。");
                CheckDiskSpace(paths.Part, Math.Max(0, total - new FileInfo(paths.Part).Length), total);
                await using (var output = new FileStream(paths.Part, FileMode.Open, FileAccess.Write, FileShare.None, 65536, true))
                {
                    output.Position = offset; await output.WriteAsync(bytes, token).ConfigureAwait(false);
                    await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true);
                }
                var pending = state.Pending.Append(new PendingBlock(offset, bytes.Length)).OrderBy(p => p.Offset).ToList(); var prefix = state.Offset;
                while (pending.Count > 0 && pending[0].Offset == prefix) { prefix += pending[0].Length; pending.RemoveAt(0); }
                state = state with { Offset = prefix, Pending = pending.ToArray() };
                await SaveParallelCheckpointAsync(metadata, state, token).ConfigureAwait(false);
                await RespondAsync(stream, 204, [], token).ConfigureAwait(false); return;
            }
            if (state.Offset != total || state.Pending.Length != 0 || new FileInfo(paths.Part).Length != total) throw new TransferConflictException("文件尚有未提交分块。");
            Resolve(relative); var receipt = FileVersion(paths.Part, total);
            await SaveParallelCheckpointAsync(metadata, state with { ReceiptVersion = receipt }, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); File.Move(paths.Part, file, overwrite);
            await RespondAsync(stream, 204, [], token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    public async Task UploadParallelAsync(string address, string code, string relative, string source, bool overwrite = false,
        IProgress<long>? progress = null, CancellationToken token = default, IProgress<string>? status = null, int workers = 3)
    {
        if (workers is < 1 or > MaximumUploadWorkers) throw new ArgumentOutOfRangeException(nameof(workers));
        string identity; long length;
        await using (var initial = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
        {
            identity = await FastFileIdentityAsync(initial, source, token).ConfigureAwait(false); length = initial.Length;
        }
        async Task<HttpRequestMessage> Request(HttpMethod method, string route, CancellationToken cancellation)
        {
            var message = await RequestAsync(method, address, code, relative, route, cancellation).ConfigureAwait(false);
            message.Headers.Add("X-EmbedDesk-File-Id", identity); message.Headers.Add("X-EmbedDesk-File-Length", length.ToString(CultureInfo.InvariantCulture));
            message.Headers.Add("X-EmbedDesk-Overwrite", overwrite ? "true" : "false"); return message;
        }
        var confirmedBytes = 0L;
        await RetryTransferAsync(async () =>
        {
            status?.Report($"正在查询断点，单文件 {workers} 路并行上传…");
            using var query = await Request(HttpMethod.Post, "upload-v2-state", token).ConfigureAwait(false); query.Content = new ByteArrayContent([]);
            using var response = await SendHeadersAsync(query, token).ConfigureAwait(false);
            var state = await ReadSmallJsonAsync<ParallelUploadState>(response, token).ConfigureAwait(false);
            if (state.Protocol != 2 || state.Offset < 0 || state.Offset > length || state.Offset < length && state.Offset % TransferChunkSize != 0) throw new InvalidDataException("服务端续传范围无效。");
            confirmedBytes = state.Offset;
            progress?.Report(state.Offset); if (state.Completed) { if (state.Offset != length) throw new InvalidDataException("完成状态无效。"); return; }
            // Close the source before commit: in a loopback transfer it can be the
            // same file that the server atomically replaces. Reopen on a retry only
            // after querying the receipt, since a lost commit reply may be complete.
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
            {
                if (input.Length != length || await FastFileIdentityAsync(input, source, token).ConfigureAwait(false) != identity)
                    throw new InvalidDataException("源文件版本已变化，请重新上传。");
                var sent = state.Offset;
                for (var first = state.Offset; first < length;)
                {
                    token.ThrowIfCancellationRequested(); var tasks = new List<Task>();
                    using var batch = CancellationTokenSource.CreateLinkedTokenSource(token);
                    for (var worker = 0; worker < workers && first < length; worker++)
                    {
                        var offset = first; var count = (int)Math.Min(TransferChunkSize, length - offset); first += count;
                        tasks.Add(SendBlockAsync(offset, count));
                    }
                    async Task SendBlockAsync(long offset, int count)
                    {
                        try
                        {
                            var bytes = new byte[count]; var read = 0;
                            while (read < count)
                            {
                                var n = await RandomAccess.ReadAsync(input.SafeFileHandle, bytes.AsMemory(read), offset + read, batch.Token).ConfigureAwait(false);
                                if (n == 0) throw new EndOfStreamException("源文件在传输过程中发生变化。"); read += n;
                            }
                            using var chunk = await Request(HttpMethod.Put, "upload-v2-chunk", batch.Token).ConfigureAwait(false);
                            chunk.Headers.Add("X-EmbedDesk-Offset", offset.ToString(CultureInfo.InvariantCulture));
                            chunk.Headers.Add("X-EmbedDesk-Chunk-Hash", Convert.ToHexString(SHA256.HashData(bytes))); chunk.Content = new ByteArrayContent(bytes);
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(batch.Token); timeout.CancelAfter(TimeSpan.FromSeconds(60));
                            using var received = await _http.SendAsync(chunk, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                            await EnsureSuccessAsync(received, timeout.Token).ConfigureAwait(false);
                            var done = Interlocked.Add(ref sent, count); progress?.Report(done);
                        }
                        catch { batch.Cancel(); throw; }
                    }
                    // Await every in-flight request before a state query can reset the retry window.
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                    confirmedBytes = first;
                }
                if (await FastFileIdentityAsync(input, source, token).ConfigureAwait(false) != identity) throw new InvalidDataException("源文件版本已变化，请重新上传。");
            }
            status?.Report("所有分块已校验，正在提交…");
            using var commit = await Request(HttpMethod.Post, "upload-v2-commit", token).ConfigureAwait(false); commit.Content = new ByteArrayContent([]);
            using var committed = await SendHeadersAsync(commit, token).ConfigureAwait(false);
            await EnsureSuccessAsync(committed, token).ConfigureAwait(false);
        }, status, token, () => confirmedBytes).ConfigureAwait(false);
    }
}
