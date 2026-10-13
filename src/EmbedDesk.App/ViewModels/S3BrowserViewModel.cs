using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using EmbedDesk.Core.Models;
using EmbedDesk.Infrastructure.Remote;
using Microsoft.Win32;

namespace EmbedDesk.App.ViewModels;

public sealed class S3BrowserViewModel : ObservableObject, IAsyncDisposable, IRemoteTabViewModel
{
    private readonly RemoteConnectionProfile _profile;
    private S3ObjectStorageClient? _client;
    private CancellationTokenSource? _transferCancellation;
    private S3BucketInfo? _selectedBucket;
    private S3ObjectInfo? _selectedObject;
    private string _prefix = string.Empty;
    private string _status = "正在连接对象存储…";
    private string _transferStatus = string.Empty;
    private string? _continuationToken;
    private Task? _activeTransferTask;
    private int _transferProgress;
    private bool _isBusy;
    private bool _isTransferActive;
    private bool _disposed;
    private string _customTitle = string.Empty;

    public S3BrowserViewModel(RemoteConnectionProfile profile)
    {
        _profile = profile;
        DisplayTitle = profile.Name;
        ConnectionDetail = string.IsNullOrWhiteSpace(profile.Host) ? $"S3 · {profile.S3Region}" : $"S3 · {profile.Host}";
    }

    public ObservableCollection<S3BucketInfo> Buckets { get; } = [];
    public ObservableCollection<S3ObjectInfo> Objects { get; } = [];
    public RemoteConnectionProfile Profile => _profile;
    public string DisplayTitle { get; }
    public string CustomTitle
    {
        get => _customTitle;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (!SetProperty(ref _customTitle, normalized)) return;
            OnPropertyChanged(nameof(TabTitle));
        }
    }
    public string TabTitle => string.IsNullOrWhiteSpace(CustomTitle) ? DisplayTitle : CustomTitle;
    public string ConnectionDetail { get; }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string TransferStatus { get => _transferStatus; private set => SetProperty(ref _transferStatus, value); }
    public int TransferProgress { get => _transferProgress; private set => SetProperty(ref _transferProgress, value); }
    public bool IsBusy { get => _isBusy; private set { if (!SetProperty(ref _isBusy, value)) return; OnPropertyChanged(nameof(CanChangeBucket)); OnPropertyChanged(nameof(CanUseBucket)); OnPropertyChanged(nameof(CanDownloadSelected)); } }
    public bool IsTransferActive { get => _isTransferActive; private set => SetProperty(ref _isTransferActive, value); }
    public string Prefix { get => _prefix; set => SetProperty(ref _prefix, value); }
    public S3BucketInfo? SelectedBucket
    {
        get => _selectedBucket;
        set
        {
            if (!SetProperty(ref _selectedBucket, value)) return;
            OnPropertyChanged(nameof(CanUseBucket));
            Prefix = string.Empty;
            _continuationToken = null;
            OnPropertyChanged(nameof(HasMoreObjects));
            SelectedObject = null;
            Objects.Clear();
            if (value is not null) _ = RefreshAsync();
        }
    }
    public S3ObjectInfo? SelectedObject { get => _selectedObject; set { if (!SetProperty(ref _selectedObject, value)) return; OnPropertyChanged(nameof(CanDownloadSelected)); } }
    public bool CanDownloadSelected => SelectedObject is { IsDirectory: false };
    public bool CanUseBucket => SelectedBucket is not null;
    public bool CanChangeBucket => !IsBusy;
    public bool HasMoreObjects => !string.IsNullOrEmpty(_continuationToken);
    public event EventHandler? StateChanged;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        StoredS3Credential? credential;
        try
        {
            credential = !string.IsNullOrEmpty(_profile.S3SecretAccessKey)
                ? new StoredS3Credential(_profile.S3AccessKeyId, _profile.S3SecretAccessKey)
                : string.IsNullOrWhiteSpace(_profile.S3CredentialTarget) ? null : S3CredentialStore.Read(_profile.S3CredentialTarget);
        }
        catch (System.ComponentModel.Win32Exception ex) { Status = $"读取凭据失败：{ex.Message}"; return; }
        if (credential is null)
        {
            Status = "未找到 S3 凭据，请编辑连接并输入 Access Key ID 和 Secret Key。";
            return;
        }
        try
        {
            _client?.Dispose();
            _client = new S3ObjectStorageClient(_profile.Host, _profile.S3Region,
                new S3Credentials(credential.AccessKeyId, credential.SecretAccessKey));
            await LoadBucketsAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is Amazon.Runtime.AmazonClientException or ArgumentException or InvalidOperationException)
        { Status = $"连接失败：{ex.Message}"; }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_client is null || SelectedBucket is null || IsBusy) return;
        _continuationToken = null;
        OnPropertyChanged(nameof(HasMoreObjects));
        IsBusy = true;
        try
        {
            var page = await _client.ListObjectsAsync(SelectedBucket.Name, Prefix, cancellationToken: cancellationToken);
            _continuationToken = page.NextToken;
            Objects.Clear();
            foreach (var item in page.Entries) Objects.Add(item);
            OnPropertyChanged(nameof(HasMoreObjects));
            Status = $"{SelectedBucket.Name}/{Prefix} · 显示 {Objects.Count} 项";
        }
        catch (Exception ex) when (ex is Amazon.Runtime.AmazonServiceException or IOException or OperationCanceledException)
        {
            if (ex is not OperationCanceledException) Status = $"读取对象失败：{ex.Message}";
        }
        finally { IsBusy = false; }
    }

    public async Task LoadMoreAsync()
    {
        if (_client is null || SelectedBucket is null || string.IsNullOrEmpty(_continuationToken) || IsBusy) return;
        IsBusy = true;
        try
        {
            var page = await _client.ListObjectsAsync(SelectedBucket.Name, Prefix, _continuationToken);
            _continuationToken = page.NextToken;
            foreach (var item in page.Entries) Objects.Add(item);
            OnPropertyChanged(nameof(HasMoreObjects));
            Status = $"{SelectedBucket.Name}/{Prefix} · 显示 {Objects.Count} 项";
        }
        catch (Exception ex) when (ex is Amazon.Runtime.AmazonServiceException or IOException) { Status = $"读取更多对象失败：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    public async Task OpenObjectAsync(S3ObjectInfo? item)
    {
        if (item is null || SelectedBucket is null) return;
        if (item.IsDirectory) { Prefix = item.Key; _continuationToken = null; await RefreshAsync(); }
        else await DownloadAsAsync(item);
    }

    public async Task GoUpAsync()
    {
        if (SelectedBucket is null || Prefix.Length == 0) return;
        var trimmed = Prefix.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        Prefix = slash < 0 ? string.Empty : trimmed[..(slash + 1)];
        _continuationToken = null;
        await RefreshAsync();
    }

    public async Task UploadAsync()
    {
        if (_client is null || SelectedBucket is null) return;
        var dialog = new OpenFileDialog { Filter = "所有文件|*.*", Multiselect = true };
        if (dialog.ShowDialog(Application.Current.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)) != true) return;
        await RunTransferAsync(async (progress, token) =>
        {
            foreach (var file in dialog.FileNames)
                await _client.UploadAsync(SelectedBucket.Name, Prefix + Path.GetFileName(file), file, progress, token);
        });
        await RefreshAsync();
    }

    public async Task DownloadSelectedAsync()
    {
        if (SelectedObject is { IsDirectory: false } item) await DownloadAsAsync(item);
    }

    private async Task DownloadAsAsync(S3ObjectInfo item)
    {
        if (_client is null || SelectedBucket is null) return;
        var dialog = new SaveFileDialog { FileName = Path.GetFileName(item.Key), Filter = "所有文件|*.*" };
        if (dialog.ShowDialog(Application.Current.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)) != true) return;
        await RunTransferAsync((progress, token) => _client.DownloadAsync(SelectedBucket.Name, item.Key, dialog.FileName, progress, token));
    }

    public async Task DeleteSelectedAsync()
    {
        if (_client is null || SelectedBucket is null || SelectedObject is not { IsDirectory: false } item) return;
        var prompt = new ConfirmationWindow("删除 S3 对象", $"确定从桶“{SelectedBucket.Name}”删除对象“{item.Key}”吗？此操作无法撤销。", "删除对象")
        { Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive) };
        if (prompt.ShowDialog() != true) return;
        try { await _client.DeleteAsync(SelectedBucket.Name, item.Key); Status = "对象已删除"; await RefreshAsync(); }
        catch (Exception ex) when (ex is Amazon.Runtime.AmazonServiceException or IOException) { Status = $"删除失败：{ex.Message}"; }
    }

    public void CancelTransfer() => _transferCancellation?.Cancel();

    private async Task RunTransferAsync(Func<IProgress<S3TransferProgress>, CancellationToken, Task> operation)
    {
        if (IsBusy) return;
        _transferCancellation?.Dispose();
        _transferCancellation = new CancellationTokenSource();
        IsBusy = true;
        IsTransferActive = true;
        TransferProgress = 0;
        var progress = new Progress<S3TransferProgress>(value =>
        {
            TransferProgress = value.Percent;
            TransferStatus = value.TotalBytes > 0
                ? $"{value.Operation} · {FormatBytes(value.BytesTransferred)} / {FormatBytes(value.TotalBytes)}"
                : $"{value.Operation} · {FormatBytes(value.BytesTransferred)}";
        });
        try
        {
            _activeTransferTask = operation(progress, _transferCancellation.Token);
            await _activeTransferTask;
            TransferStatus += " · 完成";
        }
        catch (OperationCanceledException) { TransferStatus += " · 已取消"; }
        catch (Exception ex) when (ex is Amazon.Runtime.AmazonServiceException or IOException or UnauthorizedAccessException)
        { TransferStatus = $"传输失败：{ex.Message}"; }
        catch (Exception ex) { TransferStatus = $"传输失败：{ex.Message}"; }
        finally { _activeTransferTask = null; IsTransferActive = false; IsBusy = false; StateChanged?.Invoke(this, EventArgs.Empty); }
    }

    private async Task LoadBucketsAsync(CancellationToken cancellationToken)
    {
        if (_client is null) return;
        IsBusy = true;
        S3BucketInfo? initialBucket = null;
        try
        {
            IReadOnlyList<S3BucketInfo> buckets = string.IsNullOrWhiteSpace(_profile.S3Bucket)
                ? await _client.ListBucketsAsync(cancellationToken)
                : [new S3BucketInfo(_profile.S3Bucket, null)];
            Buckets.Clear();
            foreach (var bucket in buckets) Buckets.Add(bucket);
            initialBucket = buckets.FirstOrDefault(bucket => string.Equals(bucket.Name, _profile.S3Bucket, StringComparison.Ordinal));
            Status = initialBucket is null
                ? buckets.Count == 0 ? "账户已连接，但没有可访问的桶。" : "已连接，请选择桶。"
                : $"已连接桶 {initialBucket.Name}";
        }
        catch (Exception ex) when (ex is Amazon.Runtime.AmazonServiceException or IOException or OperationCanceledException)
        { if (ex is not OperationCanceledException) Status = $"读取桶失败：{ex.Message}"; }
        finally { IsBusy = false; }
        if (initialBucket is not null) SelectedBucket = initialBucket;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):N1} GiB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):N1} MiB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):N1} KiB",
        _ => $"{bytes:N0} B"
    };

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _transferCancellation?.Cancel();
        if (_activeTransferTask is not null)
        {
            try { await _activeTransferTask; }
            catch (OperationCanceledException) { }
            catch (Amazon.Runtime.AmazonServiceException) { }
            catch (IOException) { }
            catch (Exception) { }
        }
        _transferCancellation?.Dispose();
        _client?.Dispose();
        _profile.S3SecretAccessKey = string.Empty;
    }
}
