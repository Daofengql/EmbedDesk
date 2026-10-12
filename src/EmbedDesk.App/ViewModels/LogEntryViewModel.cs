using CommunityToolkit.Mvvm.ComponentModel;
using EmbedDesk.Core.Models;

namespace EmbedDesk.App.ViewModels;

public sealed class LogEntryViewModel : ObservableObject
{
    public LogEntryViewModel(DateTimeOffset timestamp, string category, string message, TransportEventKind kind)
    {
        Timestamp = timestamp;
        Category = category;
        Message = message;
        Kind = kind;
    }

    public DateTimeOffset Timestamp { get; }
    public string Category { get; }
    public string Message { get; }
    public TransportEventKind Kind { get; }
    public string DisplayTime => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
}
