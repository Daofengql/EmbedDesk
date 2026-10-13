using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EmbedDesk.App.Controls;
using EmbedDesk.App.ViewModels;
using EmbedDesk.Core.Abstractions;
using EmbedDesk.Core.Models;
using EmbedDesk.Infrastructure.Bridging;
using EmbedDesk.Infrastructure.Networking;
using EmbedDesk.Protocols;
using Microsoft.Extensions.Logging.Abstractions;

namespace EmbedDesk.App.SmokeTests;

internal static class SerialBridgeChecks
{
    internal static async Task RunAsync()
    {
        var service = new Ports();
        await using var bridge = new SerialBridgeService(service, new Mapper(), NullLogger<SerialBridgeService>.Instance);
        var port = FreePort();
        var id = await bridge.StartAsync(Profile(port));
        Require(service.Created.Count == 0, "Publishing COM mappings does not create or open physical channels");
        await using var client = new RemoteSerialClient();
        await client.ConnectAsync("127.0.0.1", port);
        await Until(() => client.Ports.Count == 2);
        Require(service.Created.Count == 0, "Connecting to the mapping service only discovers ports");
        var settings = new SerialPortSettings
        {
            PortName = "REMOTE:1", BaudRate = 57600, DataBits = 7,
            Parity = System.IO.Ports.Parity.Even, StopBits = System.IO.Ports.StopBits.Two,
            Handshake = System.IO.Ports.Handshake.XOnXOff, DtrEnable = true
        };
        await using var channel = client.CreateChannel("REMOTE:1", settings);
        service.OpenDelay = 100;
        var opening = channel.OpenAsync();
        Require(!channel.IsOpen, "Client does not report open before server acknowledgement");
        await opening;
        var physical = service.Last;
        Require(channel.IsOpen && physical.Settings.PortName == "COM9" && physical.Settings.BaudRate == 57600 &&
            physical.Settings.DataBits == 7 && physical.Settings.Parity == System.IO.Ports.Parity.Even &&
            physical.Settings.StopBits == System.IO.Ports.StopBits.Two && physical.Settings.Handshake == System.IO.Ports.Handshake.XOnXOff && physical.Settings.DtrEnable,
            "Client-selected baud, framing, flow control and DTR reach the mapped COM");
        service.OpenDelay = 0;
        var fiveMiB = new byte[5 * 1024 * 1024]; new Random(1234).NextBytes(fiveMiB);
        using var received = new MemoryStream(); var receiveGate = new object();
        channel.DataReceived += (_, data) => { lock (receiveGate) received.Write(data.Span); };
        await Task.Run(() => physical.Emit(fiveMiB));
        await Until(() => { lock (receiveGate) return received.Length == fiveMiB.Length; });
        Require(SHA256.HashData(received.ToArray()).SequenceEqual(SHA256.HashData(fiveMiB)), "5 MiB serial-to-client bytes retain order and exact contents");
        await channel.SendAsync(fiveMiB);
        await Until(() => physical.ReceivedLength == fiveMiB.Length);
        Require(SHA256.HashData(physical.Bytes()).SequenceEqual(SHA256.HashData(fiveMiB)) && physical.MaxWrite <= 32768,
            "5 MiB client-to-serial bytes retain order and use bounded write chunks");
        await using var other = client.CreateChannel("REMOTE:2", new() { PortName = "REMOTE:2", BaudRate = 9600 });
        await other.OpenAsync(); var otherPhysical = service.Last;
        await channel.CloseAsync(); await Until(() => physical.Disposed);
        Require(otherPhysical.IsOpen && client.Ports.Count == 2, "Closing one remote tab releases its COM while retaining other channels and mappings");
        await using (var reopened = client.CreateChannel("REMOTE:1", new() { PortName = "REMOTE:1", BaudRate = 230400 }))
        {
            await reopened.OpenAsync(); physical = service.Last;
            Require(physical.Settings.BaudRate == 230400, "Reopening a COM applies the newly selected baud rate");
        }
        await Until(() => physical.Disposed);
        Require(physical.Disposed, "Disposing a remote tab sends Close and releases the real COM");
        service.FailOpen = true;
        await using (var failed = client.CreateChannel("REMOTE:1", settings))
        {
            await Throws<InvalidOperationException>(() => failed.OpenAsync(), "COM open failures are returned to the client");
            Require(!failed.IsOpen && client.IsConnected && other.IsOpen && service.Last.Disposed,
                "Failed Open leaves the tab closed, disposes the failed handle and preserves other channels");
        }
        service.FailOpen = false;
        await client.DisconnectAsync(); await Until(() => otherPhysical.Disposed);
        Require(!other.IsOpen, "Network disconnection releases remote COMs and marks client channels closed");
        await client.ConnectAsync("127.0.0.1", port); await Until(() => client.Ports.Count == 2);
        await Throws<InvalidOperationException>(() => channel.OpenAsync(), "A stale channel cannot accidentally open the same ID on a new connection");
        service.OpenDelay = 200;
        await using (var canceled = client.CreateChannel("REMOTE:1", settings))
        {
            using var cancel = new CancellationTokenSource(50);
            await Throws<OperationCanceledException>(() => canceled.OpenAsync(cancel.Token), "Canceling pending Open cancels its acknowledgement wait");
            await Until(() => !client.IsConnected && service.Last.Disposed);
            Require(!canceled.IsOpen, "Canceling pending Open cannot leak a late-opened physical COM");
        }
        service.OpenDelay = 0;
        await CheckMalformedPeerAsync(port, service);
        await CheckUiAsync(port, service);
        await CheckBackpressureAsync(port, bridge, id, service);
        Console.WriteLine("PASS deferred network serial settings, lifecycle and long-payload checks");
    }

    private static async Task CheckMalformedPeerAsync(int port, Ports service)
    {
        using (var raw = new TcpClient())
        {
            await raw.ConnectAsync("127.0.0.1", port); var stream = raw.GetStream();
            await MultiplexFrameCodec.ReadAsync(stream, default); await MultiplexFrameCodec.ReadAsync(stream, default);
            var before = service.Created.Count;
            var invalid = SerialBridgeControlCodec.EncodeSettings(new() { PortName = "COM99", BaudRate = 115200 });
            await stream.WriteAsync(MultiplexFrameCodec.Encode(new(1, MultiplexFrameFlags.Open, invalid)));
            var response = await MultiplexFrameCodec.ReadAsync(stream, default);
            Require(response?.Flags == MultiplexFrameFlags.Error && service.Created.Count == before, "Client cannot retarget an allowed channel to an unmapped COM");
            var noneStop = SerialBridgeControlCodec.EncodeSettings(new() { PortName = "COM9", StopBits = System.IO.Ports.StopBits.None });
            await stream.WriteAsync(MultiplexFrameCodec.Encode(new(1, MultiplexFrameFlags.Open, noneStop)));
            Require((await MultiplexFrameCodec.ReadAsync(stream, default))?.Flags == MultiplexFrameFlags.Error, "Invalid framing parameters are rejected before physical Open");
            var header = MultiplexFrameCodec.Encode(new(1, MultiplexFrameFlags.Data, ReadOnlyMemory<byte>.Empty));
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), int.MaxValue);
            await stream.WriteAsync(header);
            using var deadline = new CancellationTokenSource(2000);
            Require(await stream.ReadAsync(new byte[1], deadline.Token) == 0, "Oversized frame header closes only its connection before payload allocation");
        }
        using var raw2 = new TcpClient(); await raw2.ConnectAsync("127.0.0.1", port);
        var stream2 = raw2.GetStream();
        await MultiplexFrameCodec.ReadAsync(stream2, default); await MultiplexFrameCodec.ReadAsync(stream2, default);
        var bytes = MultiplexFrameCodec.Encode(new(1, MultiplexFrameFlags.Open,
            SerialBridgeControlCodec.EncodeSettings(new() { PortName = "COM9", BaudRate = 38400 })));
        // Exercise actual TCP fragmentation, independent of the normal client writer.
        for (var offset = 0; offset < bytes.Length; offset += 3) await stream2.WriteAsync(bytes.AsMemory(offset, Math.Min(3, bytes.Length - offset)));
        Require((await MultiplexFrameCodec.ReadAsync(stream2, default))?.Flags == MultiplexFrameFlags.OpenAcknowledged,
            "A new client can open COM using fragmented frames after a malformed peer disconnects");
        var physical = service.Last;
        bytes = MultiplexFrameCodec.Encode(new(1, MultiplexFrameFlags.Data, new byte[] { 1, 2, 3 })); bytes[^1] ^= 1;
        await stream2.WriteAsync(bytes);
        await Until(() => physical.Disposed);
        Require(physical.ReceivedLength == 0, "Checksum failures close COM and never forward corrupted bytes");
    }

    private static async Task CheckUiAsync(int port, Ports service)
    {
        await using var remote = new RemoteSerialClient();
        using var settings = new TemporarySettingsDirectory();
        await using var vm = new MainWindowViewModel(new TcpUdpTransportService(), new FakeSerialService(), new FakeBridge(), remote, remoteSettingsDirectory: settings.DirectoryPath);
        await vm.InitializeAsync();
        var bridgeWindow = new BridgeWindow { DataContext = vm }; bridgeWindow.Show();
        await FileWorkspaceChecks.CaptureAsync(bridgeWindow, "embeddesk-deferred-bridge.png");
        bridgeWindow.Width = bridgeWindow.MinWidth; bridgeWindow.Height = bridgeWindow.MinHeight;
        await FileWorkspaceChecks.CaptureAsync(bridgeWindow, "embeddesk-deferred-bridge-small.png");
        bridgeWindow.Close();
        await remote.ConnectAsync("127.0.0.1", port);
        await Until(() => vm.SerialPorts.Count(p => p.IsRemote) == 2);
        var tab = vm.SelectedSerialSession!;
        tab.SelectedSerialPort = tab.SerialPorts.Single(p => p.PortName == "REMOTE:1");
        var main = new MainWindow(vm); main.Show();
        try
        {
            await Task.Delay(100);
            var workspace = Descendants<SerialWorkspace>(main).Single(w => w.IsVisible);
            var baud = (ComboBox)workspace.FindName("BaudSelector");
            Require(baud.IsEnabled, "Remote serial baud selector is enabled before opening a tab");
            baud.Text = "460800"; baud.GetBindingExpression(ComboBox.TextProperty)!.UpdateSource();
            await tab.OpenSelectedSerialCommand.ExecuteAsync(null);
            Require(tab.IsOpen && service.Last.Settings.BaudRate == 460800, "Baud selected in the actual serial UI reaches the server COM");
            var payload = Encoding.UTF8.GetBytes(new string('X', 5 * 1024 * 1024));
            var physical = service.Last;
            await Task.Run(() => physical.Emit(payload));
            await Until(() => tab.Terminal.ReceivedBytes == payload.Length);
            var receive = (TextBox)workspace.FindName("ReceiveText");
            await Until(() => receive.Text.Length > 0);
            await main.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            Require(receive.Text.Length <= 1_000_000 && tab.Terminal.Entries.Sum(e => e.Data.Length) <= TerminalBufferViewModel.MaxHistoryBytes,
                "A 5 MiB unbroken receive is rendered with bounded history and exact byte counters");
            tab.SerialSendText = new string('Y', 5 * 1024 * 1024);
            await tab.SendSelectedSerialCommand.ExecuteAsync(null);
            await Until(() => physical.ReceivedLength == 5 * 1024 * 1024);
            Require(tab.Terminal.SentBytes == 5 * 1024 * 1024 && tab.IsOpen, "Long sends through the UI complete without closing the remote tab");
            await vm.CloseSerialSessionAsync(tab); await Until(() => physical.Disposed);
            Require(remote.IsConnected, "Closing the actual tab disposes COM but keeps the mapping connection available");
        }
        finally { main.Close(); }
    }

    private static async Task CheckBackpressureAsync(int port, SerialBridgeService bridge, string id, Ports service)
    {
        using var slow = new TcpClient { ReceiveBufferSize = 1024 };
        await slow.ConnectAsync("127.0.0.1", port);
        var stream = slow.GetStream();
        await MultiplexFrameCodec.ReadAsync(stream, default); await MultiplexFrameCodec.ReadAsync(stream, default);
        await stream.WriteAsync(MultiplexFrameCodec.Encode(new(1, MultiplexFrameFlags.Open,
            SerialBridgeControlCodec.EncodeSettings(new() { PortName = "COM9" }))));
        await MultiplexFrameCodec.ReadAsync(stream, default);
        var physical = service.Last;
        var producing = Task.Run(() => physical.Emit(new byte[16 * 1024 * 1024]));
        await Task.Delay(200);
        await bridge.StopAsync(id).WaitAsync(TimeSpan.FromSeconds(3));
        await producing.WaitAsync(TimeSpan.FromSeconds(3));
        Require(physical.Disposed, "Stopping with a stalled peer unblocks bounded forwarding and releases COM without unobserved tasks");
    }

    private static SerialBridgeProfile Profile(int port) => new()
    {
        Mode = SerialBridgeMode.Server, LocalHost = "127.0.0.1", LocalPort = port,
        Channels = [new() { PortName = "COM9" }, new() { PortName = "COM10" }]
    };
    private static int FreePort()
    { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
    private static async Task Until(Func<bool> ready)
    { var deadline = DateTime.UtcNow.AddSeconds(15); while (!ready()) { if (DateTime.UtcNow > deadline) throw new TimeoutException(); await Task.Delay(20); } }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); Console.WriteLine("PASS " + message); }
    private static async Task Throws<T>(Func<Task> action, string message) where T : Exception
    { try { await action(); } catch (T) { Console.WriteLine("PASS " + message); return; } throw new InvalidOperationException(message); }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T typed) yield return typed; foreach (var match in Descendants<T>(child)) yield return match; } }

    private sealed class Mapper : IVirtualSerialMapper
    {
        public Task<IReadOnlyList<string>> CreatePairAsync(string displayName, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task RemovePairAsync(IReadOnlyList<string> portNames, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Ports : ISerialPortService
    {
        public System.Collections.Concurrent.ConcurrentQueue<Channel> Created { get; } = new();
        public Channel Last => Created.Last();
        public int OpenDelay { get; set; }
        public bool FailOpen { get; set; }
        public Task<IReadOnlyList<SerialPortDescriptor>> ScanAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SerialPortDescriptor>>([new("COM9", "Fixture COM9", "Fixture", "Test")]);
        public ISerialPortChannel CreateChannel(SerialPortSettings settings)
        { var channel = new Channel(settings, OpenDelay, FailOpen); Created.Enqueue(channel); return channel; }
    }
    private sealed class Channel(SerialPortSettings settings, int delay, bool fail) : ISerialPortChannel
    {
        private readonly MemoryStream _received = new();
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public SerialPortSettings Settings { get; } = settings;
        public bool IsOpen { get; private set; }
        public bool Disposed { get; private set; }
        public int MaxWrite { get; private set; }
        public long ReceivedLength { get { lock (_received) return _received.Length; } }
        public byte[] Bytes() { lock (_received) return _received.ToArray(); }
        public event EventHandler<ReadOnlyMemory<byte>>? DataReceived;
        public async Task OpenAsync(CancellationToken cancellationToken = default)
        { if (delay > 0) await Task.Delay(delay, cancellationToken); if (fail) throw new IOException("fixture COM busy"); IsOpen = true; }
        public Task CloseAsync(CancellationToken cancellationToken = default) { IsOpen = false; return Task.CompletedTask; }
        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        { lock (_received) { _received.Write(data.Span); MaxWrite = Math.Max(MaxWrite, data.Length); } return Task.CompletedTask; }
        public void Emit(ReadOnlyMemory<byte> data) => DataReceived?.Invoke(this, data);
        public ValueTask DisposeAsync() { IsOpen = false; Disposed = true; return ValueTask.CompletedTask; }
    }
}
