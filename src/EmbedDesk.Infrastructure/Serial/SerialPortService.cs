using System.IO.Ports;
using System.Management;
using System.Text.RegularExpressions;
using EmbedDesk.Core.Abstractions;
using EmbedDesk.Core.Models;

namespace EmbedDesk.Infrastructure.Serial;

public sealed class SerialPortService : ISerialPortService, ISerialPortChangeNotifier, IDisposable
{
    private readonly ManagementEventWatcher? _deviceWatcher;
    private int _disposed;

    public event EventHandler? PortsChanged;

    public SerialPortService()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            // Win32_DeviceChangeEvent reports both insertion (2) and removal (3).
            // The app debounces these notifications before performing a complete scan.
            _deviceWatcher = new ManagementEventWatcher(
                new WqlEventQuery("SELECT * FROM Win32_DeviceChangeEvent WHERE EventType = 2 OR EventType = 3"));
            _deviceWatcher.EventArrived += OnDeviceChanged;
            _deviceWatcher.Start();
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // Device notifications are optional. Scanning still works when WMI is unavailable.
            _deviceWatcher?.Dispose();
        }
    }

    public Task<IReadOnlyList<SerialPortDescriptor>> ScanAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<SerialPortDescriptor>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var descriptors = new Dictionary<string, SerialPortDescriptor>(StringComparer.OrdinalIgnoreCase);
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher("SELECT Name, Description, Manufacturer FROM Win32_PnPEntity WHERE PNPClass = 'Ports'");
                    using var devices = searcher.Get();
                    foreach (ManagementObject device in devices)
                    {
                        using (device)
                        {
                            var name = device["Name"]?.ToString() ?? string.Empty;
                            var match = Regex.Match(name, @"\((COM\d+)\)", RegexOptions.IgnoreCase);
                            if (match.Success)
                            {
                                var port = match.Groups[1].Value;
                                descriptors[port] = new SerialPortDescriptor(port, name, device["Description"]?.ToString(), device["Manufacturer"]?.ToString());
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
                {
                    // Ports remain available even when device metadata cannot be queried.
                }
            }
            return SerialPort.GetPortNames()
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(name => descriptors.GetValueOrDefault(name) ?? new SerialPortDescriptor(name, name, "本地串口", null))
                .ToArray();
        }, cancellationToken);
    }

    public ISerialPortChannel CreateChannel(SerialPortSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new SerialPortChannel(settings);
    }

    private void OnDeviceChanged(object? sender, EventArrivedEventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try { PortsChanged?.Invoke(this, EventArgs.Empty); }
        catch { /* Subscribers schedule their own UI work; one handler must not stop WMI delivery. */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_deviceWatcher is null) return;
        try { _deviceWatcher.EventArrived -= OnDeviceChanged; } catch { }
        try { _deviceWatcher.Stop(); } catch { }
        _deviceWatcher.Dispose();
    }
}

internal sealed class SerialPortChannel : ISerialPortChannel, ISerialPortControl, ISerialPortConfiguration
{
    private readonly SerialPort _port;
    private int _disposed;

    public SerialPortChannel(SerialPortSettings settings)
    {
        Settings = settings;
        Id = Guid.NewGuid().ToString("N");
        _port = new SerialPort(settings.PortName, settings.BaudRate, settings.Parity, settings.DataBits, settings.StopBits)
        {
            Handshake = settings.Handshake,
            DtrEnable = settings.DtrEnable,
            RtsEnable = settings.RtsEnable,
            ReadBufferSize = settings.ReadBufferSize,
            ReadTimeout = 500,
            WriteTimeout = 500
        };
    }

    public string Id { get; }
    public SerialPortSettings Settings { get; private set; }
    public bool IsOpen => _port.IsOpen;
    public event EventHandler<ReadOnlyMemory<byte>>? DataReceived;

    public Task OpenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (!_port.IsOpen)
        {
            _port.DataReceived += OnDataReceived;
            _port.Open();
        }
        return Task.CompletedTask;
    }

    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_port.IsOpen)
        {
            _port.DataReceived -= OnDataReceived;
            _port.Close();
        }
        return Task.CompletedTask;
    }

    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (!_port.IsOpen) throw new InvalidOperationException("Serial port is not open.");
        // Keep each blocking driver write near 100 ms at the configured baud rate.
        // A single large Write otherwise hits the 500 ms timeout even on a healthy COM.
        var chunkSize = Math.Clamp(Settings.BaudRate / 100, 1, 1024);
        var chunk = new byte[Math.Min(chunkSize, data.Length)];
        for (var offset = 0; offset < data.Length; offset += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(chunkSize, data.Length - offset);
            data.Slice(offset, count).CopyTo(chunk);
            _port.Write(chunk, 0, count);
        }
        return Task.CompletedTask;
    }

    public Task SetControlLinesAsync(bool dtrEnable, bool rtsEnable, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (!_port.IsOpen) throw new InvalidOperationException("Serial port is not open.");
        if (_port.Handshake is Handshake.RequestToSend or Handshake.RequestToSendXOnXOff)
            throw new InvalidOperationException("请先禁用硬件流控，再使用 Bootloader 复位控制。");
        _port.DtrEnable = dtrEnable;
        _port.RtsEnable = rtsEnable;
        UpdateSettings();
        return Task.CompletedTask;
    }

    public Task SetBaudRateAsync(int baudRate, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (!_port.IsOpen) throw new InvalidOperationException("Serial port is not open.");
        if (baudRate is < 300 or > 4_000_000) throw new ArgumentOutOfRangeException(nameof(baudRate));
        _port.BaudRate = baudRate;
        UpdateSettings();
        return Task.CompletedTask;
    }

    private void UpdateSettings() => Settings = new SerialPortSettings
    {
        PortName = Settings.PortName, BaudRate = _port.BaudRate,
        DataBits = Settings.DataBits, Parity = Settings.Parity, StopBits = Settings.StopBits,
        Handshake = Settings.Handshake, DtrEnable = _port.DtrEnable, RtsEnable = _port.RtsEnable,
        ReadBufferSize = Settings.ReadBufferSize
    };

    public Task ConfigureAsync(SerialPortSettings settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (!_port.IsOpen) throw new IOException("串口未打开。");
        if (settings.BaudRate is < 300 or > 4_000_000 || settings.DataBits is < 5 or > 8
            || !Enum.IsDefined(settings.Parity) || !Enum.IsDefined(settings.StopBits) || settings.StopBits == StopBits.None
            || !Enum.IsDefined(settings.Handshake)) throw new ArgumentException("串口参数无效。");
        _port.Handshake = Handshake.None;
        _port.BaudRate = settings.BaudRate;
        _port.DataBits = settings.DataBits;
        _port.Parity = settings.Parity;
        _port.StopBits = settings.StopBits;
        _port.DtrEnable = settings.DtrEnable;
        _port.RtsEnable = settings.RtsEnable;
        _port.Handshake = settings.Handshake;
        Settings = new SerialPortSettings
        {
            PortName = Settings.PortName, BaudRate = settings.BaudRate, DataBits = settings.DataBits,
            Parity = settings.Parity, StopBits = settings.StopBits, Handshake = settings.Handshake,
            DtrEnable = settings.DtrEnable, RtsEnable = settings.RtsEnable, ReadBufferSize = Settings.ReadBufferSize
        };
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await CloseAsync();
        _port.Dispose();
    }

    private void OnDataReceived(object? sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            var count = _port.BytesToRead;
            if (count <= 0) return;
            var buffer = new byte[count];
            var read = _port.Read(buffer, 0, buffer.Length);
            if (read > 0) DataReceived?.Invoke(this, buffer.AsMemory(0, read));
        }
        catch (InvalidOperationException)
        {
            // Port can be closed concurrently with a DataReceived callback.
        }
        catch (IOException)
        {
            // The bridge owns lifecycle; transient unplug errors are surfaced on the next operation.
        }
    }
}
