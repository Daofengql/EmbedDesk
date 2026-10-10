namespace EmbeddedDebugger.App.ViewModels;

public sealed class TrafficEntryViewModel
{
    private string? _displayHex;
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
    public string DisplayHex => _displayHex ??= Data.Length == 0 ? string.Empty : string.Create(Data.Length * 3 - 1, Data, (span, bytes) =>
    {
        const string hex = "0123456789ABCDEF";
        for (var i = 0; i < bytes.Length; i++)
        {
            span[i * 3] = hex[bytes[i] >> 4]; span[i * 3 + 1] = hex[bytes[i] & 15];
            if (i + 1 < bytes.Length) span[i * 3 + 2] = ' ';
        }
    });
}
