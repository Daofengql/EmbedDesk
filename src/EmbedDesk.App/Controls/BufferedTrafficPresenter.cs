using System.Buffers;
using System.Collections.Specialized;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EmbedDesk.App.ViewModels;

namespace EmbedDesk.App.Controls;

internal readonly record struct TrafficDisplayOptions(bool Hex, bool Time, bool Sent, bool SeparateMessages = false);

/// <summary>Append new traffic in UI batches; hidden views do no formatting or layout work.</summary>
internal sealed class BufferedTrafficPresenter
{
    private const int MaxCharacters = 1_000_000;
    private readonly TextBox _text;
    private readonly DispatcherTimer _timer;
    private readonly List<TrafficEntryViewModel> _pending = [];
    private readonly Decoder _rx = Encoding.UTF8.GetDecoder(), _tx = Encoding.UTF8.GetDecoder();
    private TerminalBufferViewModel? _buffer;
    private TrafficDisplayOptions _options;
    private bool _replay = true, _attached, _autoScroll;
    private int _characters, _pendingBytes;

    public BufferedTrafficPresenter(TextBox text)
    {
        _text = text;
        _text.IsUndoEnabled = false;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => Flush();
        _text.Loaded += (_, _) => { Attach(); Invalidate(); };
        _text.Unloaded += (_, _) => { Detach(); Invalidate(); };
        _text.IsVisibleChanged += (_, _) => Invalidate();
    }

    public void Bind(TerminalBufferViewModel? buffer)
    {
        if (ReferenceEquals(_buffer, buffer)) return;
        Detach(); _buffer = buffer; Attach(); Invalidate();
    }

    public void Configure(TrafficDisplayOptions options, bool autoScroll)
    {
        _autoScroll = autoScroll;
        if (_options != options) { _options = options; Invalidate(); }
        else if (autoScroll && _text.IsVisible) _text.ScrollToEnd();
    }

    private void Attach()
    {
        if (_attached || !_text.IsLoaded || _buffer is null) return;
        _buffer.Entries.CollectionChanged += Changed; _attached = true;
    }
    private void Detach()
    {
        if (_attached && _buffer is not null) _buffer.Entries.CollectionChanged -= Changed;
        _attached = false; _timer.Stop();
    }
    private void Invalidate()
    {
        _pending.Clear(); _pendingBytes = 0; _replay = true;
        _timer.Stop();
        if (_text.IsLoaded && _text.IsVisible) _timer.Start();
    }
    private void Changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset) { Invalidate(); return; }
        if (e.NewItems is null) return; // The displayed scrollback has its own character limit.
        if (!_text.IsVisible) { Invalidate(); return; }
        if (!_replay)
        {
            foreach (TrafficEntryViewModel entry in e.NewItems) { _pending.Add(entry); _pendingBytes += entry.Data.Length; }
            if (_pendingBytes > TerminalBufferViewModel.MaxHistoryBytes) { Invalidate(); return; }
        }
        if (!_timer.IsEnabled) _timer.Start();
    }

    public void Flush()
    {
        _timer.Stop();
        if (!_text.IsVisible) return;
        var replay = _replay;
        if (replay) { _rx.Reset(); _tx.Reset(); }
        var builder = new StringBuilder();
        IEnumerable<TrafficEntryViewModel> entries = replay ? _buffer?.Entries ?? Enumerable.Empty<TrafficEntryViewModel>() : _pending;
        foreach (var entry in entries)
        {
            if (!entry.IsReceived && !_options.Sent) continue;
            if (_options.Time) builder.Append('[').Append(entry.DisplayTime).Append("] ");
            if (_options.Sent) builder.Append(entry.Direction).Append("  ");
            if (_options.Hex) builder.Append(entry.DisplayHex);
            else
            {
                var characters = ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(entry.Data.Length));
                try
                {
                    var decoder = entry.IsReceived ? _rx : _tx;
                    if (_options.SeparateMessages) decoder.Reset();
                    var count = decoder.GetChars(entry.Data, characters, _options.SeparateMessages);
                    builder.Append(characters, 0, count);
                }
                finally { ArrayPool<char>.Shared.Return(characters); }
            }
            if ((_options.Hex || _options.Time || _options.Sent || _options.SeparateMessages) && (builder.Length == 0 || builder[^1] != '\n')) builder.AppendLine();
            if (builder.Length > MaxCharacters) builder.Remove(0, builder.Length - MaxCharacters);
        }
        var offset = _text.VerticalOffset;
        _text.BeginChange();
        try
        {
            if (replay) { _text.Text = builder.ToString(); _characters = builder.Length; }
            else if (builder.Length > 0) { _text.AppendText(builder.ToString()); _characters += builder.Length; }
            if (_characters > MaxCharacters)
            {
                var remove = _characters - MaxCharacters * 3 / 4;
                var selection = _text.SelectionStart;
                _text.Select(0, remove); _text.SelectedText = string.Empty;
                _text.Select(Math.Max(0, selection - remove), 0);
                _characters -= remove;
            }
        }
        finally { _text.EndChange(); }
        _replay = false; _pending.Clear(); _pendingBytes = 0;
        if (_autoScroll) _text.ScrollToEnd(); else _text.ScrollToVerticalOffset(offset);
    }
}
