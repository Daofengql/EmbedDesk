using System.IO;
using System.Net.Http;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using EmbedDesk.Infrastructure.Networking;
namespace EmbedDesk.App.SmokeTests;
internal static class LanFileChecks
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "EmbedDesk-Lan-Test", Guid.NewGuid().ToString("N")); var shared = Path.Combine(directory, "share"); Directory.CreateDirectory(shared); Directory.CreateDirectory(Path.Combine(shared, "sub"));
        await using var server = new LanFileService(); await using var client = new LanFileService();
        try
        {
            LanFileTransferChecks.CheckConnectionInfo();
            var discovering = client.DiscoverAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(450); await server.StartServerAsync(shared); var address = $"http://127.0.0.1:{server.Port}";
            var discovered = await discovering; DeviceConnectionsChecks.Require(discovered.Any(s => s.Port == server.Port && s.FileTransport == "tcp-http") && discovered.DistinctBy(s => s.Endpoint).Count() == discovered.Count, "UDP retries discover a late-started TCP file server without duplicate endpoints");
            using (var discoveringCancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
                await DeviceConnectionsChecks.ExpectAsync<OperationCanceledException>(() => client.DiscoverAsync(TimeSpan.FromSeconds(10), discoveringCancel.Token));
            await DeviceConnectionsChecks.ExpectAsync<HttpRequestException>(() => client.ListRemoteAsync(address, "invalid"));
            var payload = RandomNumberGenerator.GetBytes(2 * 1024 * 1024); var source = Path.Combine(directory, "source.bin"); var output = Path.Combine(directory, "output.bin"); await File.WriteAllBytesAsync(source, payload);
            await client.UploadAsync(address, server.AccessCode, "sub/test.bin", source);
            var list = await client.ListRemoteAsync(address, server.AccessCode, "sub/"); DeviceConnectionsChecks.Require(list.Single().Length == payload.Length, "directory browse and upload length");
            await client.DownloadAsync(address, server.AccessCode, "sub/test.bin", output); DeviceConnectionsChecks.Require((await File.ReadAllBytesAsync(output)).SequenceEqual(payload), "streamed file integrity");
            await client.UploadAsync(address, server.AccessCode, "sub/test.bin", source);
            var differentSource = Path.Combine(directory, "different.bin"); await File.WriteAllBytesAsync(differentSource, [1, 2, 3]);
            await DeviceConnectionsChecks.ExpectAsync<HttpRequestException>(() => client.UploadAsync(address, server.AccessCode, "sub/test.bin", differentSource));
            await DeviceConnectionsChecks.ExpectAsync<HttpRequestException>(() => client.ListRemoteAsync(address, server.AccessCode, "../"));
            await DeviceConnectionsChecks.ExpectAsync<HttpRequestException>(() => client.UploadAsync(address, server.AccessCode, "../escape.bin", source));
            await DeviceConnectionsChecks.ExpectAsync<HttpRequestException>(() => client.ListRemoteAsync(address, server.AccessCode, "sub:stream"));
            await client.UploadAsync(address, server.AccessCode, "sub/test.bin", source, true);
            await CheckBufferedUploadAsync(server.Port, server.AccessCode, shared);
            var empty = Path.Combine(directory, "empty.bin"); await File.WriteAllBytesAsync(empty, []);
            await client.UploadAsync(address, server.AccessCode, "sub/empty.bin", empty);
            var emptyOutput = Path.Combine(directory, "empty-output.bin"); await client.DownloadAsync(address, server.AccessCode, "sub/empty.bin", emptyOutput);
            DeviceConnectionsChecks.Require(new FileInfo(emptyOutput).Length == 0, "empty TCP files transfer successfully");
            using (var midCancel = new CancellationTokenSource())
            {
                await DeviceConnectionsChecks.ExpectAsync<OperationCanceledException>(() => client.DownloadAsync(address, server.AccessCode, "sub/test.bin", output, new CancelProgress(midCancel), midCancel.Token));
                DeviceConnectionsChecks.Require((await File.ReadAllBytesAsync(output)).SequenceEqual(payload), "mid-transfer cancellation preserves destination");
            }
            using (var midCancel = new CancellationTokenSource())
            {
                await DeviceConnectionsChecks.ExpectAsync<OperationCanceledException>(() => client.UploadAsync(address, server.AccessCode, "sub/cancel.bin", source, progress: new CancelProgress(midCancel), token: midCancel.Token));
                DeviceConnectionsChecks.Require(!File.Exists(Path.Combine(shared, "sub", "cancel.bin")), "canceled upload never publishes partial file");
            }
            var link = Path.Combine(shared, "linked");
            try
            {
                try { Directory.CreateSymbolicLink(link, directory); }
                catch (IOException ex) when ((ex.HResult & 0xffff) == 1314)
                {
                    var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    foreach (var argument in new[] { "/c", "mklink", "/J", link, directory }) start.ArgumentList.Add(argument);
                    using var fixture = System.Diagnostics.Process.Start(start)!; await Task.WhenAll(fixture.StandardOutput.ReadToEndAsync(), fixture.StandardError.ReadToEndAsync()); await fixture.WaitForExitAsync();
                    if (fixture.ExitCode != 0) throw new UnauthorizedAccessException("Junction fixture unavailable");
                }
                await DeviceConnectionsChecks.ExpectAsync<HttpRequestException>(() => client.ListRemoteAsync(address, server.AccessCode, "linked"));
                Directory.Delete(link); Console.WriteLine("PASS linked directory confinement");
            }
            catch (UnauthorizedAccessException) { Console.WriteLine("SKIP symlink fixture: Windows host lacks symbolic-link privilege"); }
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await DeviceConnectionsChecks.ExpectAsync<OperationCanceledException>(() => client.DownloadAsync(address, server.AccessCode, "sub/test.bin", output, token: cancel.Token));
            DeviceConnectionsChecks.Require((await File.ReadAllBytesAsync(output)).SequenceEqual(payload), "cancel preserves existing destination");
            DeviceConnectionsChecks.Require((await client.ListRemoteAsync(address, server.AccessCode, "sub/")).All(e => !e.Name.StartsWith(".embeddesk-", StringComparison.Ordinal)) && !File.Exists(Path.Combine(directory, "escape.bin")), "resume checkpoints stay hidden and paths stay confined");
            await LanFileTransferChecks.RunAsync(directory, shared, server);
            await LanFileClipboardChecks.RunAsync(shared);
            Console.WriteLine("PASS LAN file server/client checks");
        }
        finally { await server.StopServerAsync(); Directory.Delete(directory, true); }
    }
    private static async Task CheckBufferedUploadAsync(int port, string code, string shared)
    {
        using var client = new TcpClient { NoDelay = true }; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        var bytes = RandomNumberGenerator.GetBytes(2000);
        var header = Encoding.ASCII.GetBytes($"PUT /api/upload?path=sub/buffered.bin HTTP/1.1\r\nHost: localhost\r\nX-EmbedDesk-Code: {code}\r\nContent-Length: {bytes.Length}\r\n\r\n");
        await client.GetStream().WriteAsync(header.AsMemory(0, 11), timeout.Token); await Task.Delay(20, timeout.Token);
        await client.GetStream().WriteAsync(header.AsMemory(11).ToArray().Concat(bytes).ToArray(), timeout.Token);
        using var reader = new StreamReader(client.GetStream(), Encoding.ASCII);
        var response = await reader.ReadToEndAsync(timeout.Token);
        DeviceConnectionsChecks.Require(response.StartsWith("HTTP/1.1 204", StringComparison.Ordinal) && (await File.ReadAllBytesAsync(Path.Combine(shared, "sub", "buffered.bin"))).SequenceEqual(bytes), "buffered TCP parsing preserves coalesced body bytes and fragmented headers");
    }
    private sealed class CancelProgress(CancellationTokenSource cancellation) : IProgress<long>
    { public void Report(long value) { if (value > 0) cancellation.Cancel(); } }
}
