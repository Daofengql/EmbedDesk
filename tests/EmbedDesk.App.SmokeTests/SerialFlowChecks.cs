using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using EmbedDesk.Core.Abstractions;
using EmbedDesk.Core.Models;
using EmbedDesk.Infrastructure.Connections;
using EmbedDesk.Infrastructure.Serial;

namespace EmbedDesk.App.SmokeTests;

internal static class SerialFlowChecks
{
    public static async Task RunAsync()
    {
        var url = Environment.GetEnvironmentVariable("EMBEDDESK_TEST_RELAY_URL");
        if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("Set EMBEDDESK_TEST_RELAY_URL to an isolated test relay before running this check.");
        var admission = Environment.GetEnvironmentVariable("EMBEDDESK_TEST_RELAY_ADMISSION_KEY") ?? string.Empty;
        await CheckAsync(url, admission, false);
        await CheckAsync(url, admission, true);
    }

    internal static async Task CheckAsync(string url, string admission, bool direct)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        var hardware = new SerialSource();
        using var shared = new SharedSerialPortService(hardware);
        await using var owner = new DeviceNode(shared);
        await using var visitor = new DeviceNode(new SharedSerialPortService(new SerialSource()));
        var failures = new ConcurrentQueue<string>();
        owner.OperationFailed += (_, error) => failures.Enqueue(error);
        visitor.OperationFailed += (_, error) => failures.Enqueue(error);
        var service = new SharedDeviceService { Name = "flow regression", Kind = SharedServiceKind.Serial, PortName = "COM3" };
        owner.ConfigureServices([service]);
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        DeviceNodeOptions Options() => new() { ServerUrl = url, AdmissionKey = admission, Credential = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), Password = password, PreferDirect = direct };
        await owner.StartAsync(Options(), token);
        await visitor.StartAsync(Options(), token);
        await UntilAsync(() => owner.IsOnline && visitor.IsOnline, token);
        var idsPath = Environment.GetEnvironmentVariable("EMBEDDESK_TEST_DEVICE_IDS_PATH");
        if (!string.IsNullOrEmpty(idsPath))
        {
            var ids = File.Exists(idsPath) ? JsonSerializer.Deserialize<List<string>>(await File.ReadAllTextAsync(idsPath, token)) ?? [] : [];
            ids.Add(owner.DeviceId); ids.Add(visitor.DeviceId);
            await File.WriteAllTextAsync(idsPath, JsonSerializer.Serialize(ids.Distinct()), token);
        }
        await visitor.ConnectPeerAsync(owner.DeviceId, password, token);
        await UntilAsync(() => visitor.RemoteServices.Count == 1, token);
        var remote = visitor.RemoteServices.Single();
        await using var channel = visitor.CreateSerialChannel(remote.PortName, new() { PortName = remote.PortName });
        var received = new MemoryStream();
        var gate = new object();
        var delay = 2;
        using var blocked = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var pause = false;
        channel.DataReceived += (_, bytes) =>
        {
            if (Volatile.Read(ref pause)) { blocked.Set(); resume.Wait(token); }
            if (Volatile.Read(ref delay) > 0) Thread.Sleep(Volatile.Read(ref delay));
            lock (gate) received.Write(bytes.Span);
        };
        await channel.OpenAsync(token);
        var tiny = RandomNumberGenerator.GetBytes(73 * 128);
        await Task.Run(() => hardware.Emit(tiny, 73), token).WaitAsync(TimeSpan.FromSeconds(15), token);
        await ExpectBytesAsync(tiny);
        Require(channel.IsOpen, "more than eight small serial callbacks backpressure without closing COM");

        var burst = RandomNumberGenerator.GetBytes(128 * 1024);
        await Task.Run(() => hardware.Emit(burst, burst.Length), token).WaitAsync(TimeSpan.FromSeconds(15), token);
        await ExpectBytesAsync(burst);
        Require(channel.IsOpen, "serial burst beyond the credit window arrives complete and ordered");

        var duplex = RandomNumberGenerator.GetBytes(24 * 1024);
        await channel.SendAsync(duplex, token).WaitAsync(TimeSpan.FromSeconds(20), token);
        await ExpectBytesAsync(duplex);
        Require(failures.IsEmpty && channel.IsOpen, "synchronous fragmented duplex echo releases credit without an RPC deadlock");

        await channel.CloseAsync(token);
        await UntilAsync(() => !shared.IsInUse("COM3"), token);
        await channel.OpenAsync(token);
        Volatile.Write(ref delay, 0);
        var reopened = RandomNumberGenerator.GetBytes(16 * 1024);
        await channel.SendAsync(reopened, token);
        await ExpectBytesAsync(reopened);
        Require(channel.IsOpen, "closed serial channel reopens with fresh flow credits");

        Volatile.Write(ref pause, true);
        var publishing = Task.Run(() => hardware.Emit(new byte[256 * 1024], 8192), token);
        try
        {
            await Task.Run(() => blocked.Wait(token), token).WaitAsync(TimeSpan.FromSeconds(5), token);
            await Task.Delay(100, token);
            Require(!publishing.IsCompleted, "slow consumer keeps serial producer bounded and waiting");
            await owner.StopAsync().WaitAsync(TimeSpan.FromSeconds(5), token);
            await publishing.WaitAsync(TimeSpan.FromSeconds(5), token);
            Require(!shared.IsInUse("COM3"), "disconnect cancels credit waits and releases the hardware lease");
        }
        finally { resume.Set(); }
        await visitor.StopAsync().WaitAsync(TimeSpan.FromSeconds(5), token);
        Console.WriteLine("PASS serial flow over " + (direct ? "LAN direct" : "relay") + " (simulated COM)");

        async Task ExpectBytesAsync(byte[] expected)
        {
            await UntilAsync(() => { lock (gate) return received.Length >= expected.Length; }, token);
            lock (gate)
            {
                Require(received.ToArray().SequenceEqual(expected), "serial payload is byte-exact and ordered");
                received.SetLength(0);
            }
        }
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }
    private static void Require(bool value, string text) => DeviceConnectionsChecks.Require(value, text);

    private sealed class SerialSource : ISerialPortService
    {
        private SourceChannel? _channel;
        public Task<IReadOnlyList<SerialPortDescriptor>> ScanAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SerialPortDescriptor>>([]);
        public ISerialPortChannel CreateChannel(SerialPortSettings settings) => _channel = new(settings);
        public void Emit(byte[] bytes, int chunkSize) => _channel!.Emit(bytes, chunkSize);
        private sealed class SourceChannel(SerialPortSettings settings) : ISerialPortChannel
        {
            public string Id { get; } = Guid.NewGuid().ToString("N");
            public SerialPortSettings Settings => settings;
            public bool IsOpen { get; private set; }
            public event EventHandler<ReadOnlyMemory<byte>>? DataReceived;
            public Task OpenAsync(CancellationToken cancellationToken = default) { IsOpen = true; return Task.CompletedTask; }
            public Task CloseAsync(CancellationToken cancellationToken = default) { IsOpen = false; return Task.CompletedTask; }
            public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) { Emit(data.ToArray(), 37); return Task.CompletedTask; }
            public void Emit(byte[] bytes, int chunkSize)
            {
                for (var offset = 0; IsOpen && offset < bytes.Length; offset += chunkSize)
                    DataReceived?.Invoke(this, bytes.AsMemory(offset, Math.Min(chunkSize, bytes.Length - offset)));
            }
            public ValueTask DisposeAsync() { IsOpen = false; return ValueTask.CompletedTask; }
        }
    }
}
