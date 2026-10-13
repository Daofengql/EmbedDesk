using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using EmbedDesk.Infrastructure.Remote;

namespace EmbedDesk.App.SmokeTests;

internal static class S3ObjectStorageChecks
{
    public static async Task RunAsync()
    {
        await using var fixture = new S3ObjectFixture();
        using var client = new S3ObjectStorageClient(fixture.Endpoint, "us-east-1",
            new S3Credentials("smoke-access-key", "smoke-secret-key"));
        var directory = Path.Combine(Path.GetTempPath(), "EmbedDesk-S3-Smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "firmware.bin");
        var destination = Path.Combine(directory, "downloaded.bin");
        var payload = RandomNumberGenerator.GetBytes(8193);
        try
        {
            await File.WriteAllBytesAsync(source, payload);
            var initial = await client.ListObjectsAsync("sample-bucket", "firmware/");
            Require(initial.Entries.Count == 0, "S3 client lists an empty bucket prefix");

            await client.UploadAsync("sample-bucket", "firmware/image.bin", source, null, CancellationToken.None);
            var uploaded = await client.ListObjectsAsync("sample-bucket", "firmware/");
            Require(uploaded.Entries.Single() is { Key: "firmware/image.bin", Size: 8193, IsDirectory: false },
                "S3 upload stores an object and listing returns its key and size");

            await client.DownloadAsync("sample-bucket", "firmware/image.bin", destination, null, CancellationToken.None);
            Require((await File.ReadAllBytesAsync(destination)).SequenceEqual(payload),
                "S3 download writes the complete object through its temporary-file path");

            await client.DeleteAsync("sample-bucket", "firmware/image.bin");
            Require((await client.ListObjectsAsync("sample-bucket", "firmware/")).Entries.Count == 0,
                "S3 delete removes the object from subsequent listings");
            Require(fixture.SawSigV4, "S3 requests use AWS Signature Version 4 authorization headers");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class S3ObjectFixture : IAsyncDisposable
    {
        private const string Namespace = "http://s3.amazonaws.com/doc/2006-03-01/";
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
        private readonly Task _acceptLoop;
        private int _sawSigV4;

        public S3ObjectFixture()
        {
            _listener.Start();
            Endpoint = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _acceptLoop = AcceptAsync();
        }

        public string Endpoint { get; }
        public bool SawSigV4 => Volatile.Read(ref _sawSigV4) != 0;

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                _ = RespondAsync(client, _stop.Token);
            }
        }

        private async Task RespondAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var request = await ReadRequestAsync(stream, cancellationToken);
                    if (request is null) return;
                    if (request.Headers.TryGetValue("authorization", out var authorization)
                        && authorization.StartsWith("AWS4-HMAC-SHA256 ", StringComparison.Ordinal))
                        Interlocked.Exchange(ref _sawSigV4, 1);

                    var uri = new Uri(new Uri("http://localhost"), request.Target);
                    var path = Uri.UnescapeDataString(uri.AbsolutePath).Trim('/');
                    var parts = path.Split('/', 2, StringSplitOptions.None);
                    if (parts.Length == 0 || parts[0] != "sample-bucket")
                    {
                        await WriteResponseAsync(stream, 404, "Not Found", "application/xml", Encoding.UTF8.GetBytes("<Error><Code>NoSuchBucket</Code></Error>"), cancellationToken);
                        return;
                    }

                    var key = parts.Length == 2 ? parts[1] : string.Empty;
                    if (request.Method == "GET" && key.Length == 0)
                    {
                        var query = ParseQuery(uri.Query);
                        if (query.ContainsKey("location"))
                        {
                            await WriteResponseAsync(stream, 200, "OK", "application/xml", Encoding.UTF8.GetBytes("<LocationConstraint>us-east-1</LocationConstraint>"), cancellationToken);
                            return;
                        }

                        var prefix = query.TryGetValue("prefix", out var requestedPrefix) ? requestedPrefix : string.Empty;
                        var entries = _objects.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)
                                && !pair.Key[prefix.Length..].Contains('/', StringComparison.Ordinal))
                            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                            .Select(pair => $"<Contents><Key>{EscapeXml(pair.Key)}</Key><LastModified>2026-10-12T00:00:00.000Z</LastModified><ETag>\"fixture-etag\"</ETag><Size>{pair.Value.Length}</Size><StorageClass>STANDARD</StorageClass></Contents>");
                        var body = $"<ListBucketResult xmlns=\"{Namespace}\"><Name>sample-bucket</Name><Prefix>{EscapeXml(prefix)}</Prefix><KeyCount>{_objects.Count}</KeyCount><MaxKeys>500</MaxKeys><IsTruncated>false</IsTruncated>{string.Concat(entries)}</ListBucketResult>";
                        await WriteResponseAsync(stream, 200, "OK", "application/xml", Encoding.UTF8.GetBytes(body), cancellationToken);
                        return;
                    }

                    if (key.Length == 0)
                    {
                        await WriteResponseAsync(stream, 405, "Method Not Allowed", "application/xml", [], cancellationToken);
                        return;
                    }

                    if (request.Method == "PUT")
                    {
                        _objects[key] = DecodeBody(request.Headers, request.Body);
                        await WriteResponseAsync(stream, 200, "OK", "application/xml", [], cancellationToken);
                    }
                    else if (request.Method == "GET" && _objects.TryGetValue(key, out var objectBytes))
                    {
                        await WriteResponseAsync(stream, 200, "OK", "application/octet-stream", objectBytes, cancellationToken);
                    }
                    else if (request.Method == "DELETE")
                    {
                        _objects.TryRemove(key, out _);
                        await WriteResponseAsync(stream, 204, "No Content", "application/xml", [], cancellationToken);
                    }
                    else
                    {
                        await WriteResponseAsync(stream, 404, "Not Found", "application/xml", Encoding.UTF8.GetBytes("<Error><Code>NoSuchKey</Code></Error>"), cancellationToken);
                    }
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
            }
        }

        private static async Task<Request?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            var headerEnd = -1;
            while (headerEnd < 0)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0) return null;
                bytes.Write(buffer, 0, read);
                if (bytes.Length > 64 * 1024) throw new InvalidDataException("Fixture HTTP headers exceeded the test limit.");
                headerEnd = FindHeaderEnd(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)));
            }

            var headerBytes = bytes.GetBuffer();
            var headerText = Encoding.ASCII.GetString(headerBytes, 0, headerEnd);
            var lines = headerText.Split("\r\n", StringSplitOptions.None);
            var requestLine = lines[0].Split(' ', 3);
            if (requestLine.Length < 2) throw new InvalidDataException("Fixture received an invalid HTTP request line.");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var separator = line.IndexOf(':');
                if (separator > 0) headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }

            if (headers.TryGetValue("Expect", out var expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
                await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), cancellationToken);

            var bodyOffset = headerEnd + 4;
            var bodyLength = headers.TryGetValue("Content-Length", out var lengthText) && int.TryParse(lengthText, out var length) ? length : 0;
            if (bodyLength is < 0 or > 16 * 1024 * 1024) throw new InvalidDataException("Fixture received an invalid S3 request body length.");
            while (bytes.Length - bodyOffset < bodyLength)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0) throw new EndOfStreamException("Fixture received a truncated S3 request body.");
                bytes.Write(buffer, 0, read);
            }

            // Reading a body can grow MemoryStream's backing buffer. Get it after all reads,
            // otherwise a small initial header buffer can be retained and the request task
            // exits before sending a response for a larger upload.
            return new Request(requestLine[0], requestLine[1], headers,
                bytes.GetBuffer().AsSpan(bodyOffset, bodyLength).ToArray());
        }

        private static byte[] DecodeBody(IReadOnlyDictionary<string, string> headers, byte[] body)
        {
            if (!headers.TryGetValue("content-encoding", out var encoding)
                || !encoding.Contains("aws-chunked", StringComparison.OrdinalIgnoreCase)) return body;

            using var decoded = new MemoryStream();
            var offset = 0;
            while (offset < body.Length)
            {
                var lineEnd = Array.IndexOf(body, (byte)'\n', offset);
                if (lineEnd < 0) throw new InvalidDataException("Fixture received an incomplete aws-chunked size line.");
                var sizeLine = Encoding.ASCII.GetString(body, offset, lineEnd - offset).TrimEnd('\r');
                var separator = sizeLine.IndexOf(';');
                if (separator >= 0) sizeLine = sizeLine[..separator];
                if (!int.TryParse(sizeLine, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var chunkSize)
                    || chunkSize < 0 || chunkSize > 16 * 1024 * 1024)
                    throw new InvalidDataException("Fixture received an invalid aws-chunked size.");
                offset = lineEnd + 1;
                if (offset + chunkSize + 2 > body.Length) throw new InvalidDataException("Fixture received a truncated aws-chunked body.");
                if (chunkSize == 0) break;
                decoded.Write(body, offset, chunkSize);
                offset += chunkSize;
                if (body[offset] != '\r' || body[offset + 1] != '\n') throw new InvalidDataException("Fixture received an invalid aws-chunked terminator.");
                offset += 2;
            }
            return decoded.ToArray();
        }

        private static async Task WriteResponseAsync(NetworkStream stream, int statusCode, string reason, string contentType,
            byte[] body, CancellationToken cancellationToken)
        {
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {statusCode} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, cancellationToken);
            if (body.Length > 0) await stream.WriteAsync(body, cancellationToken);
        }

        private static int FindHeaderEnd(ReadOnlySpan<byte> bytes)
        {
            for (var index = 0; index <= bytes.Length - 4; index++)
                if (bytes[index] == '\r' && bytes[index + 1] == '\n' && bytes[index + 2] == '\r' && bytes[index + 3] == '\n') return index;
            return -1;
        }

        private static Dictionary<string, string> ParseQuery(string query) => query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => pair.Length == 2 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : string.Empty, StringComparer.OrdinalIgnoreCase);

        private static string EscapeXml(string value) => System.Security.SecurityElement.Escape(value) ?? string.Empty;

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _acceptLoop; }
            catch (SocketException) { }
            _stop.Dispose();
        }

        private sealed record Request(string Method, string Target, IReadOnlyDictionary<string, string> Headers, byte[] Body);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"S3 object client check failed: {message}");
        Console.WriteLine($"PASS {message}");
    }
}
