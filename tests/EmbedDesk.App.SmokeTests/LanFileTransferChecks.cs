using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using EmbedDesk.Infrastructure.Networking;

namespace EmbedDesk.App.SmokeTests;

internal static class LanFileTransferChecks
{
    private const string Code = "012345ABCDEF";
    public static void CheckConnectionInfo()
    {
        var packed = LanFileConnectionInfo.Encode(["http://127.0.0.1:9100", "http://127.0.0.2:9100", "http://127.0.0.1:9100"], Code);
        var parsed = LanFileConnectionInfo.Parse("\r\n" + packed + "\r\n");
        DeviceConnectionsChecks.Require(parsed.Addresses.Count == 2 && parsed.AccessCode == Code, "clipboard payload preserves candidate addresses and access code");
        foreach (var value in new[] { "", "http://127.0.0.1:9100", LanFileConnectionInfo.Prefix + "invalid base64!", new string('a', 20 * 1024),
            Pack(2, ["http://127.0.0.1:9100"], Code), Pack(1, [], Code), Pack(1, ["http://127.0.0.1:9100"], "bad"),
            Pack(1, [null!], Code), Pack(1, Enumerable.Repeat("http://127.0.0.1:9100", 33).ToArray(), Code) })
            Reject(value);
        foreach (var address in new[] { "https://127.0.0.1:9100", "http://example.com:9100", "http://8.8.8.8:9100", "http://[::1]:9100",
            "http://user@127.0.0.1:9100", "http://127.0.0.1:9100/api/list", "http://127.0.0.1:9100/?path=x", "http://127.0.0.1:9100/#fragment" })
            Reject(Pack(1, [address], Code));
        Console.WriteLine("PASS bounded clipboard payload validation");
    }
    private static string Pack(int version, string[] addresses, string code) => LanFileConnectionInfo.Prefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { Version = version, Addresses = addresses, AccessCode = code }));
    private static void Reject(string text)
    {
        try { LanFileConnectionInfo.Parse(text); throw new InvalidOperationException("Invalid clipboard input was accepted."); }
        catch (FormatException) { }
    }

    public static async Task RunAsync(string directory, string shared, LanFileService server)
    {
        var bytes = RandomNumberGenerator.GetBytes(2 * LanFileService.TransferChunkSize + 91);
        var source = Path.Combine(directory, "resume-source.bin"); await File.WriteAllBytesAsync(source, bytes);
        var remote = Path.Combine(shared, "resume.bin"); await File.WriteAllBytesAsync(remote, [7, 8, 9]);
        var address = $"http://127.0.0.1:{server.Port}";
        await using (var client = new LanFileService())
        using (var cancel = new CancellationTokenSource())
        {
            await DeviceConnectionsChecks.ExpectAsync<OperationCanceledException>(() => client.UploadAsync(address, server.AccessCode, "resume.bin", source, true, new PauseProgress(cancel, LanFileService.TransferChunkSize), cancel.Token));
            DeviceConnectionsChecks.Require((await File.ReadAllBytesAsync(remote)).SequenceEqual(new byte[] { 7, 8, 9 }), "paused upload preserves the previous destination");
        }
        var port = server.Port; await server.StopServerAsync(); await server.StartServerAsync(shared, port: port);
        await using var resumed = new LanFileService(); var uploadProgress = new CaptureProgress();
        await resumed.UploadAsync(address, server.AccessCode, "resume.bin", source, true, uploadProgress);
        DeviceConnectionsChecks.Require(uploadProgress.Values.First() == LanFileService.TransferChunkSize && (await File.ReadAllBytesAsync(remote)).SequenceEqual(bytes), "upload resumes committed chunks across client/server restart and verifies content");
        var destination = Path.Combine(directory, "resumed-download.bin"); await File.WriteAllBytesAsync(destination, [4, 5, 6]);
        using (var cancel = new CancellationTokenSource())
            await DeviceConnectionsChecks.ExpectAsync<OperationCanceledException>(() => resumed.DownloadAsync(address, server.AccessCode, "resume.bin", destination, new PauseProgress(cancel, 65536), cancel.Token));
        DeviceConnectionsChecks.Require((await File.ReadAllBytesAsync(destination)).SequenceEqual(new byte[] { 4, 5, 6 }), "paused download preserves the previous destination");
        await using (var freshClient = new LanFileService())
        {
            var downloadProgress = new CaptureProgress(); await freshClient.DownloadAsync(address, server.AccessCode, "resume.bin", destination, downloadProgress);
            DeviceConnectionsChecks.Require(downloadProgress.Values.First() > 0 && (await File.ReadAllBytesAsync(destination)).SequenceEqual(bytes), "HTTP Range download resumes with a new client and checks length/version");
        }
        await CheckCorruptPartialAsync(directory, resumed, server);
        await CheckBadChunkAsync(server);
        await CheckInterruptedTransferAsync(directory, shared, server, false);
        await CheckInterruptedTransferAsync(directory, shared, server, true);
        await CheckSamePathUploadAsync(shared, server);
        await CheckDownloadToJunctionAsync(directory, shared, server);
        await CheckLargeOffsetsAsync(shared, server);
        await CheckParallelUploadsAsync(directory, shared, server);
        await CheckOutOfOrderAsync(shared, server);
        await CheckCorruptDownloadChunkAsync(directory);
        Console.WriteLine("PASS resumable uploads/downloads, integrity, retries and 64-bit offsets");
    }
    private static async Task CheckSamePathUploadAsync(string shared, LanFileService server)
    {
        var path = Path.Combine(shared, "same-path.bin");
        var bytes = RandomNumberGenerator.GetBytes(LanFileService.TransferChunkSize + 37);
        await File.WriteAllBytesAsync(path, bytes);
        await using var client = new LanFileService();
        await client.UploadAsync($"http://127.0.0.1:{server.Port}", server.AccessCode, "same-path.bin", path, overwrite: true);
        DeviceConnectionsChecks.Require((await File.ReadAllBytesAsync(path)).SequenceEqual(bytes), "loopback upload can replace a source file after closing its read handle");
        File.Delete(path);
    }
    private static async Task CheckDownloadToJunctionAsync(string directory, string shared, LanFileService server)
    {
        var target = Path.Combine(directory, "download-target"); Directory.CreateDirectory(target);
        var link = Path.Combine(directory, "download-link");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var fixture = System.Diagnostics.Process.Start(start)!;
        await Task.WhenAll(fixture.StandardOutput.ReadToEndAsync(), fixture.StandardError.ReadToEndAsync()); await fixture.WaitForExitAsync();
        if (fixture.ExitCode != 0) { Directory.Delete(target); Console.WriteLine("SKIP junction download fixture"); return; }
        try
        {
            var destination = Path.Combine(link, "download.bin"); await using var client = new LanFileService();
            await client.DownloadAsync($"http://127.0.0.1:{server.Port}", server.AccessCode, "resume.bin", destination);
            DeviceConnectionsChecks.Require((await File.ReadAllBytesAsync(destination)).SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(shared, "resume.bin"))), "download accepts a user-selected local junction path");
        }
        finally { Directory.Delete(link); Directory.Delete(target, true); }
    }
    private static async Task CheckParallelUploadsAsync(string directory, string shared, LanFileService server)
    {
        var address = $"http://127.0.0.1:{server.Port}"; var files = new List<(string Source, string Name)>();
        for (var i = 0; i < 4; i++)
        {
            var source = Path.Combine(directory, $"parallel-{i}.bin"); var bytes = RandomNumberGenerator.GetBytes(5 * 1024 * 1024 + i);
            await File.WriteAllBytesAsync(source, bytes); files.Add((source, Path.GetFileName(source)));
        }
        await using var client = new LanFileService();
        await Task.WhenAll(files.Select(file => client.UploadParallelAsync(address, server.AccessCode, file.Name, file.Source, true, workers: 3)));
        foreach (var file in files) DeviceConnectionsChecks.Require((await File.ReadAllBytesAsync(Path.Combine(shared, file.Name))).SequenceEqual(await File.ReadAllBytesAsync(file.Source)), "concurrent multi-file upload preserves every byte");
        Console.WriteLine("PASS bounded parallel multi-file uploads");
    }
    private static async Task CheckCorruptPartialAsync(string directory, LanFileService client, LanFileService server)
    {
        var address = $"http://127.0.0.1:{server.Port}"; var destination = Path.Combine(directory, "corrupt-download.bin");
        await File.WriteAllBytesAsync(destination, [10, 20]);
        using (var cancel = new CancellationTokenSource())
            await DeviceConnectionsChecks.ExpectAsync<OperationCanceledException>(() => client.DownloadAsync(address, server.AccessCode, "resume.bin", destination, new PauseProgress(cancel, 65536), cancel.Token));
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFileName(destination).ToUpperInvariant())));
        var part = Path.Combine(directory, ".embeddesk-download-" + key + ".part");
        await using (var corrupt = new FileStream(part, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) corrupt.SetLength(Math.Max(1, corrupt.Length / 2));
        DeviceConnectionsChecks.Require((await File.ReadAllBytesAsync(destination)).SequenceEqual(new byte[] { 10, 20 }), "partial download cache corruption never replaces the original destination");
        await client.DownloadAsync(address, server.AccessCode, "resume.bin", destination);
        var expected = Path.Combine(directory, "resume-source.bin");
        DeviceConnectionsChecks.Require((await File.ReadAllBytesAsync(destination)).SequenceEqual(await File.ReadAllBytesAsync(expected)), "truncated download cache restarts safely and preserves every byte");
    }
    private static async Task CheckOutOfOrderAsync(string shared, LanFileService server)
    {
        var bytes = RandomNumberGenerator.GetBytes(2 * LanFileService.TransferChunkSize + 17); var identity = new string('A', 64);
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        HttpRequestMessage Message(HttpMethod method, string route)
        {
            var message = Request(server, method, "upload-v2-" + route, "out-of-order.bin", bytes.Length, identity);
            message.Headers.Add("X-EmbedDesk-File-Id", identity); return message;
        }
        async Task<long> State()
        {
            using var request = Message(HttpMethod.Post, "state"); using var response = await http.SendAsync(request); response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.GetProperty("Offset").GetInt64();
        }
        async Task Chunk(int offset)
        {
            var block = bytes.AsSpan(offset, Math.Min(LanFileService.TransferChunkSize, bytes.Length - offset)).ToArray();
            using var request = Message(HttpMethod.Put, "chunk"); request.Headers.Add("X-EmbedDesk-Offset", offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Headers.Add("X-EmbedDesk-Chunk-Hash", Convert.ToHexString(SHA256.HashData(block))); request.Content = new ByteArrayContent(block);
            using var response = await http.SendAsync(request); response.EnsureSuccessStatusCode();
        }
        DeviceConnectionsChecks.Require(await State() == 0, "parallel transfer starts with an empty prefix");
        await Chunk(LanFileService.TransferChunkSize);
        using (var request = Message(HttpMethod.Post, "commit"))
        using (var response = await http.SendAsync(request)) DeviceConnectionsChecks.Require(response.StatusCode == HttpStatusCode.Conflict, "a hole in out-of-order chunks cannot be published");
        var port = server.Port; await server.StopServerAsync(); await server.StartServerAsync(shared, port: port);
        DeviceConnectionsChecks.Require(await State() == 0, "restart discards uncommitted out-of-order chunks instead of skipping a hole");
        await Chunk(LanFileService.TransferChunkSize); await Chunk(0); await Chunk(2 * LanFileService.TransferChunkSize);
        DeviceConnectionsChecks.Require(await State() == bytes.Length, "out-of-order chunks merge into the contiguous committed prefix");
        using (var request = Message(HttpMethod.Post, "commit"))
        using (var response = await http.SendAsync(request)) response.EnsureSuccessStatusCode();
        DeviceConnectionsChecks.Require((await File.ReadAllBytesAsync(Path.Combine(shared, "out-of-order.bin"))).SequenceEqual(bytes), "parallel commit preserves byte ordering after restart");
        Console.WriteLine("PASS out-of-order chunk checkpoints and restart recovery");
    }
    private static async Task CheckCorruptDownloadChunkAsync(string directory)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); using var life = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var address = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}"; var bytes = new byte[] { 1, 2, 3, 4 };
        var serving = Task.Run(async () =>
        {
            for (var index = 0; index < 2; index++)
            {
                using var peer = await listener.AcceptTcpClientAsync(life.Token); var stream = peer.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true); string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(life.Token))) { }
                var body = index == 0 ? JsonSerializer.SerializeToUtf8Bytes(new { Length = bytes.Length, Hash = new string('A', 64), Version = "\"TEST\"" }) : new byte[] { 1, 2, 99, 4 };
                var extra = index == 0 ? "" : $"Content-Range: bytes 0-3/4\r\nX-EmbedDesk-Chunk-Hash: {Convert.ToHexString(SHA256.HashData(bytes))}\r\n";
                var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(index == 0 ? "200 OK" : "206 Partial Content")}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n{extra}\r\n");
                await stream.WriteAsync(header, life.Token); await stream.WriteAsync(body, life.Token);
            }
        });
        try
        {
            var destination = Path.Combine(directory, "bad-response.bin"); await File.WriteAllBytesAsync(destination, [7, 8]); await using var client = new LanFileService();
            await DeviceConnectionsChecks.ExpectAsync<InvalidDataException>(() => client.DownloadAsync(address, Code, "file.bin", destination, token: life.Token));
            DeviceConnectionsChecks.Require((await File.ReadAllBytesAsync(destination)).SequenceEqual(new byte[] { 7, 8 }), "download rejects corrupted chunk bytes before replacing the original file");
        }
        finally { life.Cancel(); listener.Stop(); await serving; }
        Console.WriteLine("PASS download chunk corruption rejection without a full-file scan");
    }
    private static async Task CheckBadChunkAsync(LanFileService server)
    {
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        var bytes = new byte[] { 1, 2, 3 }; var hash = Convert.ToHexString(SHA256.HashData(bytes));
        using (var prepare = Request(server, HttpMethod.Post, "upload-state", "bad-chunk.bin", bytes.Length, hash))
        using (var response = await http.SendAsync(prepare)) response.EnsureSuccessStatusCode();
        using (var chunk = Request(server, HttpMethod.Put, "upload-chunk", "bad-chunk.bin", bytes.Length, hash))
        {
            chunk.Headers.Add("X-EmbedDesk-Offset", "0"); chunk.Headers.Add("X-EmbedDesk-Chunk-Hash", new string('0', 64)); chunk.Content = new ByteArrayContent(bytes);
            using var response = await http.SendAsync(chunk); DeviceConnectionsChecks.Require(response.StatusCode == HttpStatusCode.BadRequest, "bad chunk SHA-256 is rejected");
        }
        using (var prepare = Request(server, HttpMethod.Post, "upload-state", "bad-chunk.bin", bytes.Length, hash))
        using (var response = await http.SendAsync(prepare))
        {
            using var state = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            DeviceConnectionsChecks.Require(state.RootElement.GetProperty("Offset").GetInt64() == 0, "invalid chunk never advances committed progress");
        }
    }
    private static HttpRequestMessage Request(LanFileService server, HttpMethod method, string route, string path, long length, string hash)
    {
        var request = new HttpRequestMessage(method, $"http://127.0.0.1:{server.Port}/api/{route}?path={Uri.EscapeDataString(path)}");
        request.Headers.Add("X-EmbedDesk-Code", server.AccessCode); request.Headers.Add("X-EmbedDesk-File-Length", length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("X-EmbedDesk-File-Hash", hash); request.Content = new ByteArrayContent([]); return request;
    }
    private static async Task CheckInterruptedTransferAsync(string directory, string shared, LanFileService server, bool upload)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); using var life = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cut = 0; var relays = new List<Task>();
        var accepting = Task.Run(async () =>
        {
            try
            {
                while (!life.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(life.Token);
                    relays.Add(RelayAsync(client));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException) { }
        });
        async Task RelayAsync(TcpClient client)
        {
            using var ownClient = client; using var upstream = new TcpClient();
            try
            {
                await upstream.ConnectAsync(IPAddress.Loopback, server.Port, life.Token);
                var request = new List<byte>(); var one = new byte[1];
                while (request.Count < 16384)
                {
                    if (await client.GetStream().ReadAsync(one, life.Token) == 0) return;
                    request.Add(one[0]);
                    if (request.Count >= 4 && request.TakeLast(4).SequenceEqual("\r\n\r\n"u8.ToArray())) break;
                }
                await upstream.GetStream().WriteAsync(request.ToArray(), life.Token);
                var header = Encoding.ASCII.GetString(request.ToArray());
                var lengthLine = header.Split("\r\n").FirstOrDefault(s => s.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                var remaining = lengthLine is null ? 0 : int.Parse(lengthLine.Split(':', 2)[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                var body = new byte[65536];
                while (remaining > 0)
                {
                    var received = await client.GetStream().ReadAsync(body.AsMemory(0, Math.Min(remaining, body.Length)), life.Token);
                    if (received == 0) return;
                    await upstream.GetStream().WriteAsync(body.AsMemory(0, received), life.Token); remaining -= received;
                }
                var interrupt = header.StartsWith(upload ? "PUT /api/upload-v2-chunk?" : "GET /api/download", StringComparison.Ordinal) && Interlocked.CompareExchange(ref cut, 1, 0) == 0;
                var buffer = new byte[65536]; var total = 0; int count;
                while ((count = await upstream.GetStream().ReadAsync(buffer, life.Token)) > 0)
                {
                    if (interrupt && upload) return; // The chunk was committed but its reply is lost.
                    await client.GetStream().WriteAsync(buffer.AsMemory(0, count), life.Token); total += count;
                    if (interrupt && total >= 128 * 1024) return;
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
        }
        try
        {
            await using var client = new LanFileService(); var statuses = new CaptureStatus(); var destination = Path.Combine(directory, "retry-download.bin");
            var address = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            if (upload)
            {
                await client.UploadAsync(address, server.AccessCode, "retry-upload.bin", Path.Combine(directory, "resume-source.bin"), token: life.Token, status: statuses);
                DeviceConnectionsChecks.Require(cut == 1 && statuses.Messages.Any(s => s.Contains("重试", StringComparison.Ordinal)) && (await File.ReadAllBytesAsync(Path.Combine(shared, "retry-upload.bin"))).SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(shared, "resume.bin"))), "lost chunk reply retries from server-confirmed progress without duplicate bytes");
            }
            else
            {
                await client.DownloadAsync(address, server.AccessCode, "resume.bin", destination, token: life.Token, status: statuses);
                DeviceConnectionsChecks.Require(cut == 1 && statuses.Messages.Any(s => s.Contains("重试", StringComparison.Ordinal)) && (await File.ReadAllBytesAsync(destination)).SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(shared, "resume.bin"))), "real TCP disconnect retries with Range and preserves complete content");
            }
        }
        finally { life.Cancel(); listener.Stop(); await accepting; await Task.WhenAll(relays); }
    }
    private static async Task CheckLargeOffsetsAsync(string shared, LanFileService server)
    {
        const long offset = 20L * 1024 * 1024 * 1024 + 4096;
        var marker = RandomNumberGenerator.GetBytes(64); var file = Path.Combine(shared, "large-sparse.bin");
        await using (var sparse = CreateSparse(file)) { sparse.SetLength(offset + marker.Length); sparse.Position = offset; await sparse.WriteAsync(marker); }
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        using (var query = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{server.Port}/api/file-info?path=large-sparse.bin"))
        using (var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            query.Headers.Add("X-EmbedDesk-Code", server.AccessCode);
            using var response = await http.SendAsync(query, bounded.Token); response.EnsureSuccessStatusCode();
            using var info = JsonDocument.Parse(await response.Content.ReadAsStringAsync(bounded.Token));
            DeviceConnectionsChecks.Require(info.RootElement.GetProperty("Length").GetInt64() == offset + marker.Length, "20 GiB file metadata uses bounded sampling instead of a full-file scan");
        }
        using (var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{server.Port}/api/download?path=large-sparse.bin"))
        {
            request.Headers.Add("X-EmbedDesk-Code", server.AccessCode); request.Headers.Range = new RangeHeaderValue(offset, null);
            using var response = await http.SendAsync(request); response.EnsureSuccessStatusCode();
            DeviceConnectionsChecks.Require(response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.From == offset && (await response.Content.ReadAsByteArrayAsync()).SequenceEqual(marker), "download seeks and serves ranges beyond 20 GiB");
        }
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("LARGE-UPLOAD.BIN")));
        var prefix = Path.Combine(shared, ".embeddesk-upload-" + key); var hash = new string('0', 64);
        await using (var sparse = CreateSparse(prefix + ".part")) sparse.SetLength(offset);
        await File.WriteAllBytesAsync(prefix + ".json", JsonSerializer.SerializeToUtf8Bytes(new { Length = offset + marker.Length, Hash = hash, Offset = offset }));
        using (var query = Request(server, HttpMethod.Post, "upload-state", "large-upload.bin", offset + marker.Length, hash))
        using (var response = await http.SendAsync(query)) response.EnsureSuccessStatusCode();
        using (var chunk = Request(server, HttpMethod.Put, "upload-chunk", "large-upload.bin", offset + marker.Length, hash))
        {
            chunk.Headers.Add("X-EmbedDesk-Offset", offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
            chunk.Headers.Add("X-EmbedDesk-Chunk-Hash", Convert.ToHexString(SHA256.HashData(marker))); chunk.Content = new ByteArrayContent(marker);
            using var response = await http.SendAsync(chunk); response.EnsureSuccessStatusCode();
        }
        DeviceConnectionsChecks.Require(new FileInfo(prefix + ".part").Length == offset + marker.Length, "upload commits verified chunks at offsets beyond 20 GiB without a fixed file cap");
        File.Delete(file); File.Delete(prefix + ".part"); File.Delete(prefix + ".json");
    }
    private static FileStream CreateSparse(string path)
    {
        var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 65536, true);
        if (!DeviceIoControl(stream.SafeFileHandle, 0x900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero)) { stream.Dispose(); throw new IOException("Sparse fixture unavailable."); }
        return stream;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
    private sealed class PauseProgress(CancellationTokenSource cancel, long threshold) : IProgress<long>
    { public void Report(long value) { if (value >= threshold) cancel.Cancel(); } }
    private sealed class CaptureProgress : IProgress<long>
    { public List<long> Values { get; } = []; public void Report(long value) => Values.Add(value); }
    private sealed class CaptureStatus : IProgress<string>
    { public List<string> Messages { get; } = []; public void Report(string value) => Messages.Add(value); }
}
