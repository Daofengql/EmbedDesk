namespace EmbedDesk.App;

internal interface IDebugToolWindow
{
    bool HasActiveWork { get; }
    bool HasUnsavedChanges { get; }
    Task ShutdownAsync();
}
