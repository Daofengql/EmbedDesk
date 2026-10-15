using EmbedDesk.Core.Abstractions;
using EmbedDesk.Core.Models;

namespace EmbedDesk.Infrastructure.Serial;

/// <summary>One arbiter for debug, firmware, legacy bridge and device-node serial access.</summary>
public sealed class SharedSerialPortService : ISerialPortService, ISerialPortSharing, ISerialPortChangeNotifier, IDisposable
{
    private readonly ISerialPortService _inner;
    private readonly object _gate = new();
    private readonly HashSet<string> _shared = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _open = new(StringComparer.OrdinalIgnoreCase);
    public SharedSerialPortService(ISerialPortService inner)
    {
        _inner = inner;
        if (inner is ISerialPortChangeNotifier notifier) notifier.PortsChanged += OnPortsChanged;
    }
    public event EventHandler? ReservationsChanged;
    public event EventHandler? PortsChanged;
    private void OnPortsChanged(object? sender, EventArgs e) => PortsChanged?.Invoke(this, e);
    public Task<IReadOnlyList<SerialPortDescriptor>> ScanAsync(CancellationToken cancellationToken = default) => _inner.ScanAsync(cancellationToken);
    public bool IsShared(string portName) { lock (_gate) return _shared.Contains(portName); }
    public bool IsInUse(string portName) { lock (_gate) return _open.Contains(portName); }
    public void SetSharedPorts(IEnumerable<string> portNames)
    {
        var next = portNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (next.Count > 128 || next.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("共享串口配置无效。");
        lock (_gate)
        {
            if (next.Except(_shared, StringComparer.OrdinalIgnoreCase).Any(_open.Contains))
                throw new InvalidOperationException("串口正在本地使用，请先关闭会话再启用共享。");
            if (_shared.Except(next, StringComparer.OrdinalIgnoreCase).Any(_open.Contains))
                throw new InvalidOperationException("串口正在远端使用，请先关闭远端会话再取消共享。");
            _shared.Clear(); _shared.UnionWith(next);
        }
        ReservationsChanged?.Invoke(this, EventArgs.Empty);
    }
    public ISerialPortChannel CreateChannel(SerialPortSettings settings) => new ReservedChannel(this, _inner.CreateChannel(settings), false);
    public ISerialPortChannel CreateSharedChannel(SerialPortSettings settings) => new ReservedChannel(this, _inner.CreateChannel(settings), true);
    private void Acquire(string name, bool shared)
    {
        lock (_gate)
        {
            if (shared != _shared.Contains(name)) throw new InvalidOperationException(shared ? "串口未启用共享。" : "串口已预留给网络共享，请先取消共享。");
            if (!_open.Add(name)) throw new InvalidOperationException("串口已被其他会话占用。");
        }
    }
    private void Release(string name) { lock (_gate) _open.Remove(name); }
    public void Dispose()
    {
        if (_inner is ISerialPortChangeNotifier notifier) notifier.PortsChanged -= OnPortsChanged;
    }
    private sealed class ReservedChannel : ISerialPortChannel, ISerialPortControl, ISerialPortConfiguration, ISerialPortFaultNotifier
    {
        private readonly SharedSerialPortService owner;
        private readonly ISerialPortChannel inner;
        private readonly bool shared;
        private readonly string portName;
        public ReservedChannel(SharedSerialPortService owner, ISerialPortChannel inner, bool shared)
        {
            this.owner = owner; this.inner = inner; this.shared = shared; portName = inner.Settings.PortName;
            inner.DataReceived += OnReceived;
            if (inner is ISerialPortFaultNotifier notifier) notifier.Faulted += OnFaulted;
        }
        private readonly SemaphoreSlim _operations = new(1, 1);
        private bool _acquired, _disposed;
        public string Id => inner.Id;
        public SerialPortSettings Settings => inner.Settings;
        public bool IsOpen => inner.IsOpen;
        public event EventHandler<ReadOnlyMemory<byte>>? DataReceived;
        public event EventHandler<string>? Faulted;
        private void OnReceived(object? sender, ReadOnlyMemory<byte> bytes) => DataReceived?.Invoke(this, bytes);
        private void OnFaulted(object? sender, string error) => Faulted?.Invoke(this, error);
        public async Task OpenAsync(CancellationToken cancellationToken = default)
        {
            await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_acquired) return;
                owner.Acquire(portName, shared); _acquired = true;
                try { await inner.OpenAsync(cancellationToken).ConfigureAwait(false); }
                catch { owner.Release(portName); _acquired = false; throw; }
            }
            finally { _operations.Release(); }
        }
        public async Task CloseAsync(CancellationToken cancellationToken = default)
        {
            await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await inner.CloseAsync(cancellationToken).ConfigureAwait(false);
                if (_acquired) { owner.Release(portName); _acquired = false; }
            }
            finally { _operations.Release(); }
        }
        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => inner.SendAsync(data, cancellationToken);
        public Task SetControlLinesAsync(bool dtrEnable, bool rtsEnable, CancellationToken cancellationToken = default) =>
            inner is ISerialPortControl control ? control.SetControlLinesAsync(dtrEnable, rtsEnable, cancellationToken) : throw new NotSupportedException("设备不支持控制线设置。");
        public Task SetBaudRateAsync(int baudRate, CancellationToken cancellationToken = default) =>
            inner is ISerialPortControl control ? control.SetBaudRateAsync(baudRate, cancellationToken) : throw new NotSupportedException("设备不支持波特率设置。");
        public Task ConfigureAsync(SerialPortSettings settings, CancellationToken cancellationToken = default) =>
            inner is ISerialPortConfiguration configuration ? configuration.ConfigureAsync(settings, cancellationToken) : throw new NotSupportedException("设备不支持在线修改串口参数。");
        public async ValueTask DisposeAsync()
        {
            await _operations.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) return; _disposed = true;
                inner.DataReceived -= OnReceived;
                if (inner is ISerialPortFaultNotifier notifier) notifier.Faulted -= OnFaulted;
                try { await inner.DisposeAsync().ConfigureAwait(false); }
                finally { if (_acquired) owner.Release(portName); _acquired = false; }
            }
            finally { _operations.Release(); }
        }
    }
}
