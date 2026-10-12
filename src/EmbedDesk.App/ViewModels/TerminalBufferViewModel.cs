using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace EmbedDesk.App.ViewModels;

public sealed class TerminalBufferViewModel : ObservableObject
{
    private long _receivedBytes;
    private long _sentBytes;
    private int _historyBytes;
    public const int MaxHistoryBytes = 4 * 1024 * 1024;

    public TerminalBufferViewModel() => ClearCommand = new RelayCommand(Clear);

    public ObservableCollection<TrafficEntryViewModel> Entries { get; } = new();
    public long ReceivedBytes { get => _receivedBytes; private set => SetProperty(ref _receivedBytes, value); }
    public long SentBytes { get => _sentBytes; private set => SetProperty(ref _sentBytes, value); }
    public IRelayCommand ClearCommand { get; }
    public event Action<DateTimeOffset, bool, ReadOnlyMemory<byte>>? TrafficCaptured;

    public void Add(DateTimeOffset timestamp, bool isReceived, ReadOnlyMemory<byte> data)
    {
        TrafficCaptured?.Invoke(timestamp, isReceived, data);
        if (isReceived) ReceivedBytes += data.Length;
        else SentBytes += data.Length;
        // Keep byte counters exact while bounding retained data for every session.
        if (data.Length > MaxHistoryBytes) data = data[^MaxHistoryBytes..];
        Entries.Add(new TrafficEntryViewModel(timestamp, isReceived, data));
        _historyBytes += data.Length;
        while (Entries.Count > 1000 || _historyBytes > MaxHistoryBytes)
        {
            _historyBytes -= Entries[0].Data.Length;
            Entries.RemoveAt(0);
        }
    }

    public void Clear()
    {
        Entries.Clear();
        _historyBytes = 0;
        ReceivedBytes = 0;
        SentBytes = 0;
    }
}
