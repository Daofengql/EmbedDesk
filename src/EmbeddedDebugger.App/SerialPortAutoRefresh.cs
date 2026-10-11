using System.Windows.Threading;
using EmbeddedDebugger.Core.Abstractions;

namespace EmbeddedDebugger.App;

/// <summary>Turns optional serial device notifications into debounced UI scans.</summary>
internal sealed class SerialPortAutoRefresh : IDisposable
{
    private readonly ISerialPortChangeNotifier? _notifier;
    private readonly Dispatcher _dispatcher;
    private readonly Func<Task> _refresh;
    private readonly object _gate = new();
    private CancellationTokenSource? _pending;
    private bool _disposed;

    public SerialPortAutoRefresh(ISerialPortService service, Dispatcher dispatcher, Func<Task> refresh)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(refresh);
        _notifier = service as ISerialPortChangeNotifier;
        _dispatcher = dispatcher;
        _refresh = refresh;
        if (_notifier is not null) _notifier.PortsChanged += OnPortsChanged;
    }

    private void OnPortsChanged(object? sender, EventArgs e)
    {
        CancellationToken token;
        lock (_gate)
        {
            if (_disposed) return;
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = new CancellationTokenSource();
            token = _pending.Token;
        }
        _ = DebouncedRefreshAsync(token);
    }

    private async Task DebouncedRefreshAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(250, token);
            lock (_gate) if (_disposed) return;
            if (_dispatcher.CheckAccess())
            {
                await _refresh();
                return;
            }

            var operation = _dispatcher.InvokeAsync(_refresh, DispatcherPriority.Background);
            await operation.Task.Unwrap();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (InvalidOperationException) { /* Dispatcher is shutting down. */ }
        catch (TaskCanceledException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = null;
        }
        if (_notifier is not null) _notifier.PortsChanged -= OnPortsChanged;
    }
}
