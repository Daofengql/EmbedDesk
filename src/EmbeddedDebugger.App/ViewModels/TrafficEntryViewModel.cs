namespace EmbeddedDebugger.App.ViewModels;

public sealed class TrafficEntryViewModel
{
    public TrafficEntryViewModel(DateTimeOffset timestamp, bool isReceived, ReadOnlyMemory<byte> data)
    {
        Timestamp = timestamp;
        IsReceived = isReceived;
        Data = data.ToArray();
    }

    public DateTimeOffset Timestamp { get; }
    public bool IsReceived { get; }
    public byte[] Data { get; }
    public string DisplayTime => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
    public string Direction => IsReceived ? "RX" : "TX";
    public string Size => $"{Data.Length} B";
    public string DisplayText => System.Text.Encoding.UTF8.GetString(Data).TrimEnd('\r', '\n');
    public string DisplayHex => string.Join(" ", Data.Select(value => value.ToString("X2")));
}
