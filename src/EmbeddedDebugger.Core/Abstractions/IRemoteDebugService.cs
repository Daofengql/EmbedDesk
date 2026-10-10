using EmbeddedDebugger.Core.Models;

namespace EmbeddedDebugger.Core.Abstractions;

public interface IRemoteDebugService
{
    IRemoteDebugSession CreateSession(RemoteConnectionProfile profile);
}

public interface IRemoteDebugSession : IAsyncDisposable
{
    RemoteConnectionProfile Profile { get; }
    RemoteSessionState State { get; }
    string? HomeDirectory { get; }
    string? HostPlatform { get; }
    bool SupportsDirectoryManagement { get; }
    event EventHandler<string>? TextReceived;
    event EventHandler<string>? ConnectionLogReceived;
    event EventHandler<RemotePerformanceSnapshot>? PerformanceChanged;
    event EventHandler<RemoteSessionState>? StateChanged;

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task SendAsync(string text, CancellationToken cancellationToken = default);
    Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RemoteDirectoryEntry>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default);
    Task DownloadFileAsync(string remotePath, string localPath, CancellationToken cancellationToken = default);
    Task DownloadPreviewAsync(string remotePath, string localPath, long maxBytes, CancellationToken cancellationToken = default);
    Task UploadFileAsync(string localPath, string remotePath, CancellationToken cancellationToken = default);
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);
    Task DeleteAsync(string path, bool directory, CancellationToken cancellationToken = default);
}
