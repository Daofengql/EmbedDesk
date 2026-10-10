using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace EmbeddedDebugger.App.ViewModels;

public sealed class TerminalBufferViewModel : ObservableObject
{
    private long _receivedBytes;
    private long _sentBytes;

    public TerminalBufferViewModel() => ClearCommand = new RelayCommand(Clear);

    public ObservableCollection<TrafficEntryViewModel> Entries { get; } = new();
    public long ReceivedBytes { get => _receivedBytes; private set => SetProperty(ref _receivedBytes, value); }
    public long SentBytes { get => _sentBytes; private set => SetProperty(ref _sentBytes, value); }
    public IRelayCommand ClearCommand { get; }

    public void Add(DateTimeOffset timestamp, bool isReceived, ReadOnlyMemory<byte> data)
    {
        if (isReceived) ReceivedBytes += data.Length;
        else SentBytes += data.Length;
        Entries.Add(new TrafficEntryViewModel(timestamp, isReceived, data));
        while (Entries.Count > 1000) Entries.RemoveAt(0);
    }

    public void Clear()
    {
        Entries.Clear();
        ReceivedBytes = 0;
        SentBytes = 0;
    }
}
