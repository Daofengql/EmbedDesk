using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmbedDesk.Infrastructure.Networking;

public sealed partial class LanFileService
{
    public const int TransferChunkSize = 4 * 1024 * 1024;
    private readonly ConcurrentDictionary<string, byte> _uploadTargets = new(StringComparer.OrdinalIgnoreCase);
    private sealed record TransferCheckpoint(long Length, string Hash, long Offset);
    private sealed record RemoteFileInfo(long Length, string Hash, string Version);
    private sealed record UploadState(long Offset, bool Completed);
    private sealed class TransferConflictException(string message) : IOException(message);

    private static bool IsTransferFile(string name) => name.StartsWith(".embeddesk-upload-", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(".embeddesk-download-", StringComparison.OrdinalIgnoreCase);
    private static string FileVersion(string file, long length) => $"\"{length:X}-{File.GetLastWriteTimeUtc(file).Ticks:X}\"";
    // This identity reads metadata only. Chunk SHA-256 remains the transport guard; we do not scan a multi-gigabyte file twice.
    private static string FileIdentity(string file, long length)
        => $"{length:X16}-{File.GetLastWriteTimeUtc(file).Ticks:X16}-{File.GetCreationTimeUtc(file).Ticks:X16}";
    private static async Task<string> FastFileIdentityAsync(FileStream input, string file, CancellationToken token)
    {
        var length = input.Length; var metadata = FileIdentity(file, length); var originalPosition = input.Position;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(Encoding.ASCII.GetBytes(metadata));
        var bytes = new byte[(int)Math.Min(65536, length)];
        input.Position = 0; await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false); hash.AppendData(bytes);
        if (length > bytes.Length)
        {
            input.Position = Math.Max(bytes.Length, length - 65536); var last = new byte[(int)(length - input.Position)];
            await input.ReadExactlyAsync(last, token).ConfigureAwait(false); hash.AppendData(last);
        }
        input.Position = originalPosition;
        if (FileIdentity(file, length) != metadata) throw new InvalidDataException("文件在读取版本时发生变化，请重试。");
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static (string Part, string Metadata) TransferPaths(string file, string direction)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFileName(file).ToUpperInvariant())));
        var prefix = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!, $".embeddesk-{direction}-{key}");
        return (prefix + ".part", prefix + ".json");
    }
    private static void CheckDiskSpace(string file, long remaining, long total)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(file))!;
        if (root.StartsWith(@"\\", StringComparison.Ordinal)) return; // UNC shares report write errors through FileStream.
        var drive = new DriveInfo(root);
        if (!drive.IsReady) throw new IOException("目标磁盘尚未就绪。");
        if (drive.DriveFormat.Equals("FAT32", StringComparison.OrdinalIgnoreCase) && total > uint.MaxValue)
            throw new IOException("目标磁盘为 FAT32，无法保存超过 4 GiB 的单文件，请使用 NTFS 或 exFAT 目录。");
        if (remaining > drive.AvailableFreeSpace) throw new IOException("目标磁盘可用空间不足，已保留的数据可在释放空间后续传。");
    }
    private static async Task<TransferCheckpoint?> ReadCheckpointAsync(string path, CancellationToken token, bool includeParentLinks = true)
    {
        RejectLinks(path, includeParentLinks);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 2048) return null;
        try { return JsonSerializer.Deserialize<TransferCheckpoint>(await File.ReadAllBytesAsync(path, token).ConfigureAwait(false)); }
        catch (JsonException) { return null; }
    }
    private static async Task SaveCheckpointAsync(string path, TransferCheckpoint value, CancellationToken token, bool includeParentLinks = true)
    {
        RejectLinks(path, includeParentLinks); RejectLinks(path + ".new", includeParentLinks);
        await using (var output = new FileStream(path + ".new", FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
        {
            await output.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(value), token).ConfigureAwait(false);
            await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true);
        }
        File.Move(path + ".new", path, true);
    }
    private static bool Matches(TransferCheckpoint? checkpoint, long length, string hash) => checkpoint is not null
        && checkpoint.Length == length && checkpoint.Hash == hash && checkpoint.Offset >= 0 && checkpoint.Offset <= length;
    private static bool FileHasLength(string file, long length)
    {
        RejectLinks(file);
        return File.Exists(file) && new FileInfo(file).Length == length;
    }
    private async Task HandleResumableUploadAsync(Stream stream, FileRequest request, string relative, string file, CancellationToken token)
    {
        var hash = request.Headers.GetValueOrDefault("X-EmbedDesk-File-Hash", "").ToUpperInvariant();
        if (!IsHash(hash) || !long.TryParse(request.Headers.GetValueOrDefault("X-EmbedDesk-File-Length"), NumberStyles.None, CultureInfo.InvariantCulture, out var total) || total < 0)
            throw new InvalidDataException("文件长度或 SHA-256 无效，请使用支持续传的新版客户端。");
        if (!Directory.Exists(Path.GetDirectoryName(file))) throw new DirectoryNotFoundException("目标目录不存在。");
        if (!_uploadTargets.TryAdd(file, 0)) throw new TransferConflictException("此目标文件正在传输，请稍后重试。");
        try
        {
            var paths = TransferPaths(file, "upload"); RejectLinks(paths.Part);
            var checkpoint = await ReadCheckpointAsync(paths.Metadata, token).ConfigureAwait(false);
            var matches = Matches(checkpoint, total, hash);
            var completed = matches && checkpoint!.Offset == total && !File.Exists(paths.Part) && FileHasLength(file, total);
            var overwrite = request.Headers.GetValueOrDefault("X-EmbedDesk-Overwrite") == "true";
            if (File.Exists(file) && !overwrite && !completed) throw new TransferConflictException("目标文件已存在，请确认覆盖后重试。");
            if (request.Url.AbsolutePath == "/api/upload-state")
            {
                if (completed) { await RespondAsync(stream, 200, JsonSerializer.SerializeToUtf8Bytes(new UploadState(total, true)), token).ConfigureAwait(false); return; }
                var offset = matches && File.Exists(paths.Part) && new FileInfo(paths.Part).Length >= checkpoint!.Offset ? checkpoint!.Offset : 0;
                await using (var part = new FileStream(paths.Part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 65536, true)) part.SetLength(offset);
                CheckDiskSpace(paths.Part, total - offset, total);
                await SaveCheckpointAsync(paths.Metadata, new(total, hash, offset), token).ConfigureAwait(false);
                await RespondAsync(stream, 200, JsonSerializer.SerializeToUtf8Bytes(new UploadState(offset, false)), token).ConfigureAwait(false);
                return;
            }
            if (completed) { await RespondAsync(stream, 204, [], token).ConfigureAwait(false); return; }
            if (!matches || !File.Exists(paths.Part) || new FileInfo(paths.Part).Length < checkpoint!.Offset) throw new TransferConflictException("续传状态发生变化，请重新发起上传。");
            if (request.Url.AbsolutePath == "/api/upload-chunk")
            {
                if (!long.TryParse(request.Headers.GetValueOrDefault("X-EmbedDesk-Offset"), NumberStyles.None, CultureInfo.InvariantCulture, out var offset) || offset != checkpoint.Offset)
                    throw new TransferConflictException("续传偏移已变化，请重新查询上传进度。");
                var chunkHash = request.Headers.GetValueOrDefault("X-EmbedDesk-Chunk-Hash", "").ToUpperInvariant();
                if (request.Length is <= 0 or > TransferChunkSize || request.Length > total - offset || !IsHash(chunkHash) || request.BodyPrefix.Length > request.Length)
                    throw new InvalidDataException("上传分块长度或校验值无效。");
                var bytes = new byte[(int)request.Length]; request.BodyPrefix.CopyTo(bytes, 0);
                using (var buffer = new MemoryStream(bytes, request.BodyPrefix.Length, bytes.Length - request.BodyPrefix.Length, true))
                    await CopyAsync(stream, buffer, bytes.Length - request.BodyPrefix.Length, null, token).ConfigureAwait(false);
                if (Convert.ToHexString(SHA256.HashData(bytes)) != chunkHash) throw new InvalidDataException("上传分块校验失败，未接受此分块。");
                CheckDiskSpace(paths.Part, total - offset, total);
                await using (var output = new FileStream(paths.Part, FileMode.Open, FileAccess.Write, FileShare.None, 65536, true))
                {
                    output.SetLength(offset); output.Position = offset;
                    await output.WriteAsync(bytes, token).ConfigureAwait(false);
                    await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true);
                }
                await SaveCheckpointAsync(paths.Metadata, new(total, hash, offset + bytes.Length), token).ConfigureAwait(false);
                await RespondAsync(stream, 204, [], token).ConfigureAwait(false);
                return;
            }
            if (checkpoint.Offset != total) throw new TransferConflictException("文件尚未传输完成，已保留进度。");
            if (!FileHasLength(paths.Part, total))
            {
                await SaveCheckpointAsync(paths.Metadata, new(total, hash, 0), token).ConfigureAwait(false);
                throw new InvalidDataException("续传临时文件长度无效，目标文件未被替换，请重新上传。");
            }
            token.ThrowIfCancellationRequested(); Resolve(relative); File.Move(paths.Part, file, overwrite);
            // Keep one small receipt per target so a lost commit reply can be retried after a restart.
            await RespondAsync(stream, 204, [], token).ConfigureAwait(false);
        }
        finally { _uploadTargets.TryRemove(file, out _); }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15)); token = timeout.Token;
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var bytes = new byte[4096]; var count = await input.ReadAsync(bytes, token).ConfigureAwait(false);
        var detail = Encoding.UTF8.GetString(bytes, 0, count).Trim();
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "访问码无效，请获取服务端当前访问码。",
            HttpStatusCode.NotFound => "服务端不支持此操作或文件不存在，请确认双方使用新版软件。",
            HttpStatusCode.PreconditionFailed => "远端文件已发生变化，请重新发起下载。",
            HttpStatusCode.RequestedRangeNotSatisfiable => "续传范围已变化，请重新发起下载。",
            _ => string.IsNullOrEmpty(detail) ? $"服务端拒绝操作（HTTP {(int)response.StatusCode}）。" : detail
        };
        throw new HttpRequestException(message, null, response.StatusCode);
    }
    private async Task<HttpResponseMessage> SendHeadersAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
    }
    private static async Task<T> ReadSmallJsonAsync<T>(HttpResponseMessage response, CancellationToken token)
    {
        await EnsureSuccessAsync(response, token).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15)); token = timeout.Token;
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream(); var bytes = new byte[4096]; int count;
        while ((count = await input.ReadAsync(bytes, token).ConfigureAwait(false)) > 0)
        { if (output.Length + count > 16384) throw new InvalidDataException("服务端传输状态过大。"); output.Write(bytes, 0, count); }
        return JsonSerializer.Deserialize<T>(output.ToArray()) ?? throw new InvalidDataException("服务端未返回传输状态。");
    }
    private static bool IsTransient(Exception ex, CancellationToken token) => !token.IsCancellationRequested && (ex is IOException
        || ex is OperationCanceledException || ex is HttpRequestException { StatusCode: null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError });
    private static async Task RetryTransferAsync(Func<Task> action, IProgress<string>? status, CancellationToken token, Func<long>? committedBytes = null)
    {
        var attempt = 0; var previousProgress = committedBytes?.Invoke() ?? 0;
        while (true)
        {
            try { await action().ConfigureAwait(false); return; }
            catch (Exception ex) when (IsTransient(ex, token))
            {
                var currentProgress = committedBytes?.Invoke() ?? previousProgress;
                // Bound consecutive failures at the same committed position. A
                // long file must not spend its whole retry budget on earlier,
                // successfully recovered disconnects.
                if (currentProgress > previousProgress) { attempt = 0; previousProgress = currentProgress; }
                if (attempt >= 2)
                {
                    if (ex is OperationCanceledException) throw new TimeoutException("文件传输等待超时，已保留续传进度，请检查网络后重试。", ex);
                    throw;
                }
                attempt++;
                status?.Report($"连接中断，正在重试（{attempt}/2）…"); await Task.Delay(500 * attempt, token).ConfigureAwait(false);
            }
        }
    }
    public Task UploadAsync(string address, string code, string relative, string source, bool overwrite = false, IProgress<long>? progress = null, CancellationToken token = default, IProgress<string>? status = null)
        => UploadParallelAsync(address, code, relative, source, overwrite, progress, token, status, workers: 1);

    public async Task DownloadAsync(string address, string code, string relative, string destination, IProgress<long>? progress = null, CancellationToken token = default, IProgress<string>? status = null)
    {
        status?.Report("正在读取远端文件版本，可暂停…");
        RemoteFileInfo? info = null;
        await RetryTransferAsync(async () =>
        {
            using var query = await RequestAsync(HttpMethod.Get, address, code, relative, "file-info", token).ConfigureAwait(false);
            using var response = await SendHeadersAsync(query, token).ConfigureAwait(false);
            info = await ReadSmallJsonAsync<RemoteFileInfo>(response, token).ConfigureAwait(false);
        }, status, token).ConfigureAwait(false);
        if (info is null || info.Length < 0 || !IsHash(info.Hash) || info.Version is not { Length: > 0 and <= 96 }
            || !EntityTagHeaderValue.TryParse(info.Version, out var version) || version.IsWeak) throw new InvalidDataException("远端文件信息无效。");
        destination = ResolveDownloadDestination(destination); RejectLinks(destination, includeParents: false);
        var paths = TransferPaths(destination, "download"); RejectLinks(paths.Part, includeParents: false);
        await using (var output = new FileStream(paths.Part, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 65536, true))
        {
            var checkpoint = await ReadCheckpointAsync(paths.Metadata, token, includeParentLinks: false).ConfigureAwait(false);
            var offsetSaved = Matches(checkpoint, info.Length, info.Hash) && output.Length >= checkpoint!.Offset ? checkpoint!.Offset : 0;
            output.SetLength(offsetSaved);
            CheckDiskSpace(destination, info.Length - output.Length, info.Length);
            await SaveCheckpointAsync(paths.Metadata, new(info.Length, info.Hash, offsetSaved), token, includeParentLinks: false).ConfigureAwait(false);
            await RetryTransferAsync(async () =>
            {
                var offset = output.Length; output.Position = offset; progress?.Report(offset);
                while (offset < info.Length)
                {
                    token.ThrowIfCancellationRequested(); var count = (int)Math.Min(TransferChunkSize, info.Length - offset);
                    using var request = await RequestAsync(HttpMethod.Get, address, code, relative, "download-chunk", token).ConfigureAwait(false);
                    request.Headers.Range = new RangeHeaderValue(offset, offset + count - 1); request.Headers.IfMatch.Add(version);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    await EnsureSuccessAsync(response, timeout.Token).ConfigureAwait(false); timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                    if (response.StatusCode != HttpStatusCode.PartialContent || response.Content.Headers.ContentLength != count
                        || response.Content.Headers.ContentRange is not { From: var from, To: var to, Length: var length } || from != offset || to != offset + count - 1 || length != info.Length)
                        throw new InvalidDataException("服务端未返回正确的续传范围，请确认双方使用新版软件。");
                    var expectedHash = response.Headers.TryGetValues("X-EmbedDesk-Chunk-Hash", out var hashes) ? hashes.SingleOrDefault() : null;
                    if (expectedHash is not { Length: 64 } || !expectedHash.All(char.IsAsciiHexDigit)) throw new InvalidDataException("服务端缺少分块校验，请更新双方软件。");
                    await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    var bytes = new byte[count]; using (var buffer = new MemoryStream(bytes, true)) await CopyAsync(input, buffer, count, null, token).ConfigureAwait(false);
                    if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("下载分块校验失败，原目标文件未被替换，请重试。");
                    try
                    {
                        output.Position = offset; await output.WriteAsync(bytes, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true);
                        await SaveCheckpointAsync(paths.Metadata, new(info.Length, info.Hash, offset + count), token, includeParentLinks: false).ConfigureAwait(false);
                    }
                    catch { output.SetLength(offset); throw; }
                    offset += count; progress?.Report(offset);
                }
            }, status, token, () => output.Length).ConfigureAwait(false);
            status?.Report("正在确认下载文件版本…");
            await RetryTransferAsync(async () =>
            {
                using var query = await RequestAsync(HttpMethod.Get, address, code, relative, "file-info", token).ConfigureAwait(false);
                using var response = await SendHeadersAsync(query, token).ConfigureAwait(false);
                var finalInfo = await ReadSmallJsonAsync<RemoteFileInfo>(response, token).ConfigureAwait(false);
                if (finalInfo != info) throw new InvalidDataException("远端文件版本已变化，原目标文件未被替换，请重新下载。");
            }, status, token).ConfigureAwait(false);
            await output.FlushAsync(token).ConfigureAwait(false);
            if (output.Length != info.Length)
            { output.SetLength(0); throw new InvalidDataException("下载文件长度校验失败，原目标文件保持不变，请重新下载。"); }
            output.Flush(true);
        }
        token.ThrowIfCancellationRequested(); RejectLinks(destination, includeParents: false); File.Move(paths.Part, destination, true); File.Delete(paths.Metadata);
    }
    private static string ResolveDownloadDestination(string destination)
    {
        destination = Path.GetFullPath(destination);
        var directory = Path.GetDirectoryName(destination);
        if (directory is null || !Directory.Exists(directory)) throw new DirectoryNotFoundException("本地保存目录不存在，请重新选择保存位置。");
        var fileName = Path.GetFileName(destination);
        if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentException("本地保存路径必须包含文件名。", nameof(destination));
        // A local directory selected by the user may be a junction (for example
        // Downloads relocated to another disk). Resolve it before creating sibling
        // checkpoints and checking disk space; shared server paths stay confined.
        var resolved = Path.GetPathRoot(directory)!;
        foreach (var part in Path.GetRelativePath(resolved, directory).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            var folder = new DirectoryInfo(Path.Combine(resolved, part));
            resolved = (folder.ResolveLinkTarget(returnFinalTarget: true) ?? folder).FullName;
        }
        return Path.Combine(resolved, fileName);
    }
}
