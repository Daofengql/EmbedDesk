using System.Collections.ObjectModel;
using System.IO;
using EmbedDesk.Core.Models;

namespace EmbedDesk.App.ViewModels;

public sealed partial class RemoteWorkspaceViewModel
{
    private readonly List<RemoteFileDocumentViewModel> _documents = [];
    private readonly CancellationTokenSource _fileOperations = new();
    private readonly SemaphoreSlim _previewGate = new(1, 1);
    private string _localPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private string _fileStatus = string.Empty;
    private int _localNavigationVersion;

    public event EventHandler<RemoteFileDocumentViewModel>? DocumentOpened;
    public double DirectoryPaneHeight { get; set; } = 250;
    public long PreviewSizeLimitBytes { get; set; } = 5 * 1024 * 1024;
    public ObservableCollection<RemoteDirectoryEntryViewModel> LocalEntries { get; } = [];
    public string LocalPath { get => _localPath; private set => SetProperty(ref _localPath, value); }
    public string FileStatus { get => _fileStatus; private set => SetProperty(ref _fileStatus, value); }

    private async Task OpenFileAsync(RemoteDirectoryEntryViewModel entry)
    {
        if (_session is null || !SupportsDirectoryManagement) return;
        if (entry.Size > PreviewSizeLimitBytes)
        {
            FileStatus = $"文件超过 {PreviewSizeLimitBytes / (1024 * 1024)} MB 预览上限，请右键下载。";
            return;
        }
        await _previewGate.WaitAsync();
        string? cachePath = null;
        try
        {
            if (_fileOperations.IsCancellationRequested) return;
            var existing = _documents.FirstOrDefault(document => document.RemotePath == entry.FullPath);
            if (existing is not null) { DocumentOpened?.Invoke(this, existing); return; }
            FileStatus = $"正在打开 {entry.Name}…";
            var cacheDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "EmbedDesk", "previews");
            Directory.CreateDirectory(cacheDirectory);
            cachePath = System.IO.Path.Combine(cacheDirectory, Guid.NewGuid().ToString("N"));
            await _session.DownloadPreviewAsync(entry.FullPath, cachePath, PreviewSizeLimitBytes, _fileOperations.Token);
            _fileOperations.Token.ThrowIfCancellationRequested();
            var document = await RemoteFileDocumentViewModel.LoadAsync(this, entry.FullPath, cachePath, SaveRemoteFileAsync);
            if (_fileOperations.IsCancellationRequested) { document.Dispose(); return; }
            _documents.Add(document);
            cachePath = null;
            DocumentOpened?.Invoke(this, document);
            FileStatus = $"已打开 {entry.Name}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { FileStatus = $"无法预览：{ex.Message}"; }
        finally
        {
            if (cachePath is not null) { try { File.Delete(cachePath); } catch (IOException) { } }
            _previewGate.Release();
        }
    }

    private async Task SaveRemoteFileAsync(string localPath, string remotePath)
    {
        if (_session is null || !IsConnected) throw new IOException("连接已断开，请重新连接后保存。");
        await _session.UploadFileAsync(localPath, remotePath, _fileOperations.Token);
    }

    public void CloseDocument(RemoteFileDocumentViewModel document)
    {
        if (_documents.Remove(document)) document.Dispose();
    }

    public async Task RefreshLocalDirectoryAsync(string? path = null)
    {
        var version = ++_localNavigationVersion;
        try
        {
            var target = System.IO.Path.GetFullPath(path ?? LocalPath);
            var entries = await Task.Run(() =>
            {
                var folder = new DirectoryInfo(target);
                var result = new List<RemoteDirectoryEntryViewModel>();
                if (folder.Parent is { } parent) result.Add(new(new("..", parent.FullName, true, 0, null, string.Empty)));
                foreach (var item in folder.EnumerateFileSystemInfos().OrderByDescending(item => (item.Attributes & FileAttributes.Directory) != 0).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
                    result.Add(new(new(item.Name, item.FullName, item is DirectoryInfo, item is FileInfo file ? file.Length : 0, item.LastWriteTimeUtc, string.Empty)));
                return result;
            });
            if (version != _localNavigationVersion) return;
            LocalPath = target;
            LocalEntries.Clear();
            foreach (var entry in entries) LocalEntries.Add(entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { FileStatus = $"无法打开本地目录：{ex.Message}"; }
    }

    public async Task NavigateRemoteAsync(string path)
    {
        if (_session is null || !IsConnected || IsBusy || string.IsNullOrWhiteSpace(path)) return;
        var previous = Path;
        await RunBusyAsync(async () =>
        {
            Path = path;
            try { await RefreshDirectoryCoreAsync(); }
            catch { Path = previous; throw; }
        });
    }

    public async Task DownloadToLocalAsync(RemoteDirectoryEntryViewModel entry)
    {
        if (entry.IsDirectory || entry.Name != System.IO.Path.GetFileName(entry.Name)) return;
        await DownloadAsync(entry, System.IO.Path.Combine(LocalPath, entry.Name));
        await RefreshLocalDirectoryAsync();
    }
}
