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
        var overflowEndpoint = new Endpoint { State = State(true) }; var flood = new Remote { Burst = true };
        await using (var bridge = new VirtualSerialBridge(overflowEndpoint, _ => flood, () => true))
        {
            await Until(() => flood.IsOpen); overflowEndpoint.Input.Enqueue([1]);
            await Until(() => !bridge.IsRunning);
            Require(bridge.Error.Contains("队列已满", StringComparison.Ordinal) && !flood.IsOpen, "bounded virtual-COM receive queue stops on overflow instead of dropping data silently");
        }
        var disconnectedEndpoint = new Endpoint(); var connected = true;
        await using (var bridge = new VirtualSerialBridge(disconnectedEndpoint, _ => new Remote(), () => connected))
        {
            connected = false; await Until(() => !bridge.IsRunning);
            Require(disconnectedEndpoint.Disposals == 1 && bridge.Error.Contains("断开", StringComparison.Ordinal), "device loss releases a virtual mapping even while the external COM is closed");
        }
        Console.WriteLine("PASS virtual serial: native metadata codec, lazy open, binary duplex, live settings, bounded queues and cleanup (simulated driver)");
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
        public ConcurrentQueue<byte[]> Input { get; } = new();
        public ConcurrentQueue<byte[]> Output { get; } = new();
        public VirtualSerialPeerState GetPeerState() => State;
        public int Read(byte[] buffer) { if (!Input.TryDequeue(out var bytes)) return 0; bytes.CopyTo(buffer, 0); return bytes.Length; }
        public void Write(byte[] bytes) => Output.Enqueue(bytes.ToArray());
        public void SetReady(bool ready) => Ready = ready;
        public void Discard() { Input.Clear(); Output.Clear(); }
        public ValueTask DisposeAsync() { Interlocked.Increment(ref Disposals); return ValueTask.CompletedTask; }
    }
    private sealed class Remote : ISerialPortChannel, ISerialPortConfiguration
    {
        private SerialPortSettings _settings = new();
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public SerialPortSettings Settings { get => Volatile.Read(ref _settings); set => Volatile.Write(ref _settings, value); }
        public bool IsOpen { get; private set; }
        public int Opens;
        public bool FailOpen, Burst;
        public event EventHandler<ReadOnlyMemory<byte>>? DataReceived;
        public Task OpenAsync(CancellationToken token = default) { if (FailOpen) throw new IOException("Test: remote COM occupied externally"); IsOpen = true; Opens++; return Task.CompletedTask; }
        public Task CloseAsync(CancellationToken token = default) { IsOpen = false; return Task.CompletedTask; }
        public Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default) { DataReceived?.Invoke(this, Burst ? new byte[9 * 8192] : bytes); return Task.CompletedTask; }
        public Task ConfigureAsync(SerialPortSettings settings, CancellationToken token = default) { Settings = settings; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { IsOpen = false; return ValueTask.CompletedTask; }
    }
}
