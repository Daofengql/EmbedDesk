using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;

namespace EmbedDesk.Infrastructure.Remote;

public sealed record S3BucketInfo(string Name, DateTime? Created);
public sealed record S3ObjectInfo(string Name, string Key, bool IsDirectory, long Size, DateTime? Modified);
public sealed record S3ObjectPage(IReadOnlyList<S3ObjectInfo> Entries, string? NextToken);
public sealed record S3TransferProgress(long BytesTransferred, long TotalBytes, string Operation)
{
    public int Percent => TotalBytes <= 0 ? 0 : (int)Math.Clamp(BytesTransferred * 100d / TotalBytes, 0, 100);
}
public sealed record S3Credentials(string AccessKeyId, string SecretAccessKey);

/// <summary>Thin, cancellable S3 browser client. Credentials are supplied by the caller and never persisted here.</summary>
public sealed class S3ObjectStorageClient : IDisposable
{
    private readonly AmazonS3Client _client;
    private bool _disposed;

    public S3ObjectStorageClient(string? endpoint, string region, S3Credentials credentials)
    {
        if (string.IsNullOrWhiteSpace(region)) throw new ArgumentException("S3 区域不能为空。", nameof(region));
        var config = new AmazonS3Config { RegionEndpoint = RegionEndpoint.GetBySystemName(region.Trim()) };
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var serviceUri)
                || serviceUri.Scheme is not ("https" or "http")
                || serviceUri.Scheme == "http" && !serviceUri.IsLoopback)
                throw new ArgumentException("S3 服务端点必须是有效的 HTTP 或 HTTPS URL。", nameof(endpoint));
            config.ServiceURL = serviceUri.AbsoluteUri.TrimEnd('/');
            config.ForcePathStyle = true;
        }
        _client = new AmazonS3Client(new BasicAWSCredentials(credentials.AccessKeyId, credentials.SecretAccessKey), config);
    }

    public async Task<IReadOnlyList<S3BucketInfo>> ListBucketsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var response = await _client.ListBucketsAsync(cancellationToken).ConfigureAwait(false);
        return response.Buckets.OrderBy(bucket => bucket.BucketName, StringComparer.OrdinalIgnoreCase)
            .Select(bucket => new S3BucketInfo(bucket.BucketName, bucket.CreationDate)).ToArray();
    }

    public async Task<S3ObjectPage> ListObjectsAsync(string bucket, string prefix, string? continuationToken = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var request = new ListObjectsV2Request { BucketName = bucket, Prefix = prefix, Delimiter = "/", MaxKeys = 500, ContinuationToken = continuationToken };
        var response = await _client.ListObjectsV2Async(request, cancellationToken).ConfigureAwait(false);
        var entries = (response.CommonPrefixes ?? []).Select(key => new S3ObjectInfo(
                key[prefix.Length..].TrimEnd('/'), key, true, 0, null))
            .Concat((response.S3Objects ?? [])
                .Where(item => !string.Equals(item.Key, prefix, StringComparison.Ordinal)
                    && !(item.Key.EndsWith("/", StringComparison.Ordinal) && item.Size is 0 or null))
                .Select(item => new S3ObjectInfo(item.Key[prefix.Length..], item.Key, false, item.Size ?? 0, item.LastModified)))
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new S3ObjectPage(entries, response.IsTruncated == true ? response.NextContinuationToken : null);
    }

    public async Task UploadAsync(string bucket, string key, string localPath, IProgress<S3TransferProgress>? progress, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var total = new FileInfo(localPath).Length;
        var transfer = new TransferUtility(_client);
        var request = new TransferUtilityUploadRequest
        {
            BucketName = bucket,
            Key = key,
            FilePath = localPath,
            PartSize = 16 * 1024 * 1024
        };
        request.UploadProgressEvent += (_, args) => progress?.Report(new S3TransferProgress(args.TransferredBytes, args.TotalBytes, "上传"));
        await transfer.UploadAsync(request, cancellationToken).ConfigureAwait(false);
        progress?.Report(new S3TransferProgress(total, total, "上传"));
    }

    public async Task DownloadAsync(string bucket, string key, string localPath, IProgress<S3TransferProgress>? progress, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var temporaryPath = localPath + ".embeddesk-part";
        try
        {
        using var response = await _client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key }, cancellationToken).ConfigureAwait(false);
        var total = response.ContentLength;
        var buffer = new byte[128 * 1024];
        long transferred = 0;
        await using var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, useAsync: true);
        while (true)
        {
            var count = await response.ResponseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            transferred += count;
            progress?.Report(new S3TransferProgress(transferred, total, "下载"));
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(new S3TransferProgress(transferred, total, "下载"));
        output.Close();
        File.Move(temporaryPath, localPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporaryPath); } catch (IOException) { }
            throw;
        }
    }

    public async Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key }, cancellationToken).ConfigureAwait(false);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    public void Dispose() { if (_disposed) return; _disposed = true; _client.Dispose(); }
}
