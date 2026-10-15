using System.Windows.Threading;

namespace EmbedDesk.App;

/// <summary>One scheduled UI callback per session, with bounded retained traffic.</summary>
internal sealed class BoundedDispatcherQueue<T>(Dispatcher? dispatcher, Action<T> consume, Action overflow) : IDisposable
{
    internal const int MaximumBytes = 4 * 1024 * 1024;
    internal const int MaximumItems = 1024;
    private readonly object _sync = new();
    private readonly Queue<(T Item, int Size)> _items = new();
    private int _bytes;
    private bool _scheduled, _overflowed, _disposed;

    internal void Post(Func<T> capture, int size)
    {
        lock (_sync)
        {
            if (_disposed || _overflowed) return;
            if (size > MaximumBytes - _bytes || _items.Count == MaximumItems) _overflowed = true;
            else { _items.Enqueue((capture(), size)); _bytes += size; }
            if (_scheduled) return;
            _scheduled = true;
        }
        Schedule();
    }

    private void Schedule()
    {
        if (dispatcher is null) Drain();
        else if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(Drain, DispatcherPriority.Background);
        else Dispose();
    }

    private void Drain()
    {
        var count = 0;
        var bytes = 0;
        while (count < 64 && bytes < 256 * 1024)
        {
            (T Item, int Size) next;
            lock (_sync)
            {
                if (_disposed || !_items.TryDequeue(out next)) break;
                _bytes -= next.Size;
            }
            consume(next.Item); bytes += next.Size; count++;
        }
        bool fault;
        lock (_sync)
        {
            if (_disposed) return;
            if (_items.Count != 0) { Schedule(); return; }
            _scheduled = false; fault = _overflowed;
        }
        if (fault) overflow();
    }

    public void Dispose()
    {
        lock (_sync) { _disposed = true; _items.Clear(); _bytes = 0; }
    }
}
