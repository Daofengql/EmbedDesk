using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Ports;
using EmbedDesk.Core.Abstractions;
using EmbedDesk.Core.Models;
using EmbedDesk.Infrastructure.Connections;
using EmbedDesk.Infrastructure.Serial;

namespace EmbedDesk.App.SmokeTests;

internal static class VirtualSerialChecks
{
    private static VirtualSerialPeerState State(bool open, int baud = 115200) => new(open, baud, 8, Parity.None, StopBits.One, false, true);

    public static async Task RunAsync()
    {
        var snapshot = new byte[] { 0, 3, 0xb0, 0, 16, 0, 0, 0, 0, 0, 17, 7, 2, 2 };
        BinaryPrimitives.WriteUInt32LittleEndian(snapshot.AsSpan(5), 230400);
        var decoded = Com0ComEndpoint.DecodeSnapshot(snapshot);
        Require(decoded == new VirtualSerialPeerState(true, 230400, 7, Parity.Even, StopBits.Two, true, true), "com0com snapshot decodes remote open, framing and control lines");
        try { Com0ComEndpoint.DecodeSnapshot(snapshot.AsSpan(0, 13)); throw new InvalidOperationException("invalid snapshot accepted"); } catch (InvalidDataException) { }
        snapshot[13] = 3;
        try { Com0ComEndpoint.DecodeSnapshot(snapshot); throw new InvalidOperationException("invalid framing accepted"); } catch (InvalidDataException) { }
        var endpoint = new Endpoint(); var remote = new Remote();
        await using (var bridge = new VirtualSerialBridge(endpoint, settings => { remote.Settings = settings; return remote; }, () => true))
        {
            await Task.Delay(60); Require(remote.Opens == 0 && !endpoint.Ready, "virtual mapping reserves only the bridge and does not pre-open remote hardware");
            endpoint.State = State(true);
            await Until(() => remote.IsOpen && endpoint.Ready);
            var binary = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(); endpoint.Input.Enqueue(binary);
            await Until(() => endpoint.Output.TryPeek(out _));
            Require(endpoint.Output.TryDequeue(out var output) && output.SequenceEqual(binary), "all 256 byte values survive system-COM bridge duplex");
            endpoint.State = State(true, 230400) with { DataBits = 7, Parity = Parity.Even, StopBits = StopBits.Two, Dtr = true };
            await Until(() => remote.Settings.BaudRate == 230400);
            Require(remote.Opens == 1 && remote.Settings is { DataBits: 7, Parity: Parity.Even, StopBits: StopBits.Two, DtrEnable: true }, "parameter changes apply without closing or reopening hardware");
            endpoint.State = State(false); await Until(() => !remote.IsOpen && !endpoint.Ready);
            endpoint.State = State(true); await Until(() => remote.IsOpen && remote.Opens == 2);
            Require(bridge.IsRunning, "closing the external COM releases hardware and retains the mapping for reopening");
        }
        Require(endpoint.Disposals == 1 && !remote.IsOpen, "stopping mapping awaits cleanup of endpoint and remote channel");
        var occupiedEndpoint = new Endpoint { State = State(true) };
        await using (var bridge = new VirtualSerialBridge(occupiedEndpoint, _ => new Remote { FailOpen = true }, () => true))
        {
            await Until(() => !bridge.IsRunning);
            Require(bridge.Error.Contains("occupied", StringComparison.Ordinal) && !occupiedEndpoint.Ready && occupiedEndpoint.Disposals == 1,
                "remote open failure preserves its real reason and releases the virtual bridge");
        }
        await CheckReceiveFlowAsync();
        await CheckCloseAndReopenAsync();
        await CheckBlockedDisposalAsync();
        var failedWriteEndpoint = new Endpoint { State = State(true), BeforeWrite = _ => throw new IOException("Test: native COM write failed") };
        var failedWriteRemote = new Remote { Response = Pattern(20 * 8192) };
        await using (var bridge = new VirtualSerialBridge(failedWriteEndpoint, _ => failedWriteRemote, () => true))
        {
            await Until(() => failedWriteEndpoint.Ready); failedWriteEndpoint.Input.Enqueue([1]);
            await Until(() => !bridge.IsRunning);
            Require(bridge.Error == "Test: native COM write failed" && !failedWriteRemote.IsOpen && failedWriteEndpoint.Disposals == 1,
                "real endpoint write failures retain their reason while unblocking a synchronous receive producer");
        }
        var disconnectedEndpoint = new Endpoint(); var connected = true;
        await using (var bridge = new VirtualSerialBridge(disconnectedEndpoint, _ => new Remote(), () => connected))
        {
            connected = false; await Until(() => !bridge.IsRunning);
            Require(disconnectedEndpoint.Disposals == 1 && bridge.Error.Contains("断开", StringComparison.Ordinal), "device loss releases a virtual mapping even while the external COM is closed");
        }
        Console.WriteLine("PASS virtual serial: native metadata codec, lazy open, binary duplex, live settings, bounded queues and cleanup (simulated driver)");
    }

    private static byte[] Pattern(int count) => Enumerable.Range(0, count).Select(i => (byte)(i * 31 + i / 8192)).ToArray();
    private static byte[] Flatten(ConcurrentQueue<byte[]> queue) => queue.SelectMany(bytes => bytes).ToArray();

    private static async Task CheckReceiveFlowAsync()
    {
        var payload = Pattern(24 * 8192 + 317);
        var endpoint = new Endpoint { State = State(true) }; var remote = new Remote { Response = payload };
        await using (var bridge = new VirtualSerialBridge(endpoint, _ => remote, () => true))
        {
            await Until(() => endpoint.Ready); endpoint.Input.Enqueue([1]);
            await Until(() => bridge.ReceivedBytes == payload.Length && remote.SendCompletions == 1);
            Require(Flatten(endpoint.Output).SequenceEqual(payload) && bridge.SentBytes == 1 && bridge.IsRunning && bridge.Error.Length == 0,
                "a synchronous echo exceeding eight frames applies backpressure and delivers every byte in order");
        }

        using var gate = new ManualResetEventSlim(false);
        var slowEndpoint = new Endpoint { State = State(true), BeforeWrite = _ => WaitForWrite(gate) };
        var duplex = new Remote { Echo = false };
        await using var duplexBridge = new VirtualSerialBridge(slowEndpoint, _ => duplex, () => true);
        try
        {
            await Until(() => slowEndpoint.Ready);
            var incoming = Task.Run(() => duplex.Emit(payload));
            await Until(() => slowEndpoint.WriteAttempts > 0);
            var outgoing = Enumerable.Range(0, 16).Select(i => Pattern(1024 + i)).ToArray();
            foreach (var block in outgoing) slowEndpoint.Input.Enqueue(block);
            await Until(() => duplex.SendCompletions == outgoing.Length);
            Require(!incoming.IsCompleted && slowEndpoint.Output.IsEmpty && duplexBridge.IsRunning,
                "a slow receiving COM backpressures a bounded burst while the opposite direction continues sending");
            gate.Set();
            await incoming.WaitAsync(TimeSpan.FromSeconds(3));
            await Until(() => duplexBridge.ReceivedBytes == payload.Length);
            Require(Flatten(slowEndpoint.Output).SequenceEqual(payload) && Flatten(duplex.Sent).SequenceEqual(outgoing.SelectMany(bytes => bytes)),
                "simultaneous inbound burst and outbound traffic retain complete ordered streams");
        }
        finally { gate.Set(); }
    }

    private static async Task CheckCloseAndReopenAsync()
    {
        using var gate = new ManualResetEventSlim(false);
        var endpoint = new Endpoint { State = State(true), BeforeWrite = _ => WaitForWrite(gate) };
        var remotes = new ConcurrentQueue<Remote>();
        await using var bridge = new VirtualSerialBridge(endpoint, _ => { var remote = new Remote { Echo = false }; remotes.Enqueue(remote); return remote; }, () => true);
        try
        {
            await Until(() => endpoint.Ready && remotes.Count == 1);
            var first = remotes.Single();
            var staleCallback = first.CaptureReceiver();
            var blocked = Task.Run(() => first.Emit(Pattern(24 * 8192)));
            await Until(() => endpoint.WriteAttempts > 0);
            Require(!blocked.IsCompleted, "receive producer waits when the virtual-COM queue reaches its bounded capacity");
            endpoint.State = State(false);
            await blocked.WaitAsync(TimeSpan.FromSeconds(3));
            await Until(() => !endpoint.Ready);
            Require(!endpoint.Ready, "external close cancels a blocked receive producer before joining the native write");
            gate.Set();
            await Until(() => !first.IsOpen);
            endpoint.BeforeWrite = null;
            endpoint.State = State(true);
            await Until(() => endpoint.Ready && remotes.Count == 2);
            staleCallback(new byte[] { 99 });
            await Task.Delay(60);
            Require(endpoint.Output.IsEmpty, "callbacks captured before closing cannot enqueue into a reopened mapping");
            var fresh = Pattern(521); remotes.Last().Emit(fresh);
            await Until(() => !endpoint.Output.IsEmpty);
            Require(Flatten(endpoint.Output).SequenceEqual(fresh) && bridge.IsRunning && bridge.Error.Length == 0,
                "reopening creates a fresh receive queue with no bytes left over from the previous COM opening");
        }
        finally { gate.Set(); }
    }

    private static async Task CheckBlockedDisposalAsync()
    {
        using var gate = new ManualResetEventSlim(false);
        var endpoint = new Endpoint { State = State(true), BeforeWrite = _ => WaitForWrite(gate) };
        var remote = new Remote { Echo = false };
        await using var bridge = new VirtualSerialBridge(endpoint, _ => remote, () => true);
        try
        {
            await Until(() => endpoint.Ready);
            var blocked = Task.Run(() => remote.Emit(Pattern(24 * 8192)));
            await Until(() => endpoint.WriteAttempts > 0);
            var disposing = bridge.DisposeAsync().AsTask();
            await blocked.WaitAsync(TimeSpan.FromSeconds(3));
            Require(!disposing.IsCompleted, "mapping shutdown cancels receive backpressure and waits for the in-flight native write");
            gate.Set();
            await disposing.WaitAsync(TimeSpan.FromSeconds(3));
            Require(endpoint.Disposals == 1 && !remote.IsOpen && bridge.Error.Length == 0,
                "disposing a mapping with a full receive queue joins both pumps and releases all resources");
        }
        finally { gate.Set(); }
    }

    private static void WaitForWrite(ManualResetEventSlim gate)
    {
        if (!gate.Wait(TimeSpan.FromSeconds(8))) throw new IOException("Test: simulated COM write gate timed out");
    }

    internal static async Task CheckDeviceBridgeAsync(DeviceNode node, DeviceRemoteService service, Func<SerialPortSettings?> settings, Func<bool> inUse)
    {
        var endpoint = new Endpoint();
        await using var bridge = new VirtualSerialBridge(endpoint, configuration => node.CreateSerialChannel(service.PortName, configuration),
            () => node.RemoteServices.Any(s => s.PortName == service.PortName));
        await Task.Delay(50); Require(!inUse(), "system-COM mapping remains non-invasive before external open over device transport");
        endpoint.State = State(true); await Until(() => endpoint.Ready && inUse());
        endpoint.Input.Enqueue(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray());
        await Until(() => endpoint.Output.TryPeek(out _));
        Require(endpoint.Output.TryDequeue(out var bytes) && bytes.SequenceEqual(Enumerable.Range(0, 256).Select(i => (byte)i)), "virtual endpoint interoperates with authenticated device serial channel");
        endpoint.State = State(true, 460800) with { Dtr = true, DataBits = 7, Parity = Parity.Even };
        await Until(() => settings()?.BaudRate == 460800);
        Require(settings() is { DtrEnable: true, DataBits: 7, Parity: Parity.Even }, "live framing and DTR changes cross the same encrypted device channel");
        endpoint.State = State(false); await Until(() => !inUse());
    }

    private static Task Until(Func<bool> condition) => DeviceConnectionsChecks.UntilAsync(condition);
    private static void Require(bool value, string text) => DeviceConnectionsChecks.Require(value, text);

    internal sealed class Endpoint : IVirtualSerialEndpoint
    {
        private VirtualSerialPeerState _state = VirtualSerialChecks.State(false);
        public VirtualSerialPeerState State { get => Volatile.Read(ref _state); set => Volatile.Write(ref _state, value); }
        public volatile bool Ready;
        public int Disposals;
        public int WriteAttempts;
        public Action<byte[]>? BeforeWrite;
        public ConcurrentQueue<byte[]> Input { get; } = new();
        public ConcurrentQueue<byte[]> Output { get; } = new();
        public VirtualSerialPeerState GetPeerState() => State;
        public int Read(byte[] buffer) { if (!Input.TryDequeue(out var bytes)) return 0; bytes.CopyTo(buffer, 0); return bytes.Length; }
        public void Write(byte[] bytes) { Interlocked.Increment(ref WriteAttempts); BeforeWrite?.Invoke(bytes); Output.Enqueue(bytes.ToArray()); }
        public void SetReady(bool ready) => Ready = ready;
        public void Discard() { Input.Clear(); Output.Clear(); }
        public ValueTask DisposeAsync() { Interlocked.Increment(ref Disposals); return ValueTask.CompletedTask; }
    }
    private sealed class Remote : ISerialPortChannel, ISerialPortConfiguration
    {
        private SerialPortSettings _settings = new();
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public SerialPortSettings Settings { get => Volatile.Read(ref _settings); set => Volatile.Write(ref _settings, value); }
        private volatile bool _open;
        public bool IsOpen => _open;
        public int Opens, SendCompletions;
        public bool FailOpen, Echo = true;
        public byte[]? Response;
        public ConcurrentQueue<byte[]> Sent { get; } = new();
        public event EventHandler<ReadOnlyMemory<byte>>? DataReceived;
        public Task OpenAsync(CancellationToken token = default) { if (FailOpen) throw new IOException("Test: remote COM occupied externally"); _open = true; Interlocked.Increment(ref Opens); return Task.CompletedTask; }
        public Task CloseAsync(CancellationToken token = default) { _open = false; return Task.CompletedTask; }
        public Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Sent.Enqueue(bytes.ToArray());
            if (Response is not null) Emit(Response); else if (Echo) Emit(bytes);
            token.ThrowIfCancellationRequested(); Interlocked.Increment(ref SendCompletions); return Task.CompletedTask;
        }
        public void Emit(ReadOnlyMemory<byte> bytes) => DataReceived?.Invoke(this, bytes);
        public Action<ReadOnlyMemory<byte>> CaptureReceiver() { var receiver = DataReceived; return bytes => receiver?.Invoke(this, bytes); }
        public Task ConfigureAsync(SerialPortSettings settings, CancellationToken token = default) { Settings = settings; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { _open = false; return ValueTask.CompletedTask; }
    }
}
