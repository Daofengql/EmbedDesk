using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EmbedDesk.Infrastructure.Networking;

namespace EmbedDesk.App;
public partial class LanFileShareWindow : Window
{
    private readonly LanFileService _service = new();
    private CancellationTokenSource? _operationCancellation;
    private Task _operation = Task.CompletedTask;
    private Task? _shutdown;
    private string _path = string.Empty, _lastAddress = string.Empty;
    private bool _busy, _closing, _cleaned, _transferring, _backgroundService, _choosingClose;
    private readonly SemaphoreSlim _uploadSlots = new(3, 3);
    private readonly ObservableCollection<TransferItem> _transferItems = [];
    internal bool HasActiveWork => _busy || _service.IsRunning || _transferItems.Any(i => i.Status != "已完成");
    internal bool IsBackgroundService => _backgroundService && !_cleaned;
    private sealed record EntryView(string Name, bool IsDirectory, long Length)
    {
        public string Type => IsDirectory ? "目录" : "文件";
        public string Size => IsDirectory ? "" : Length.ToString("N0") + " B";
    }
    private sealed class TransferItem : INotifyPropertyChanged
    {
        private string _status = "等待中", _detail = "";
        internal string SpeedText = "--", RemainingText = "--:--";
        private double _progress;
        internal long LastBytes = -1, LastTimestamp;
        internal readonly string SourcePath, RemotePath, Address;
        internal string AccessCode;
        internal readonly bool Overwrite;
        internal CancellationTokenSource Cancellation = new();
        internal bool Running;
        internal Task Work = Task.CompletedTask;
        public string Name { get; }
        public string Status { get => _status; set { if (_status != value) { _status = value; OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(ActionText)); OnPropertyChanged(nameof(TransferStats)); } } }
        public double Progress { get => _progress; set { if (Math.Abs(_progress - value) > 0.1) { _progress = value; OnPropertyChanged(nameof(Progress)); } } }
        public string Detail { get => _detail; set { if (_detail != value) { _detail = value; OnPropertyChanged(nameof(Detail)); } } }
        public string TransferStats => Status switch
        {
            "等待中" => "等待可用上传槽位",
            "已暂停" => "已暂停 · 可继续",
            "失败" => "传输失败 · 可重试",
            "已完成" => "完成",
            _ => $"{SpeedText} · 预计剩余 {RemainingText}"
        };
        public string ActionText => Running ? "暂停" : Status is "已完成" ? "完成" : "继续";
        public event PropertyChangedEventHandler? PropertyChanged;
        public TransferItem(string source, string remote, bool overwrite, string address, string code)
        { SourcePath = source; RemotePath = remote; Overwrite = overwrite; Name = Path.GetFileName(source); Address = address; AccessCode = code; }
        internal void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new(name));
    }
    public LanFileShareWindow()
    {
        InitializeComponent(); WindowTheme.ApplyDarkTitleBar(this); _service.Error += OnServerError; TransferList.ItemsSource = _transferItems; UpdatePathUi();
    }
    private void OnServerError(object? sender, string error) => Dispatcher.BeginInvoke(new Action(() => { if (!_cleaned) ErrorText.Text = error; }));
    private void ChooseRoot_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = Root.Text, Description = "选择局域网共享根目录" };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) Root.Text = dialog.SelectedPath;
    }
    private async void StartServer_Click(object sender, RoutedEventArgs e)
    {
        if (_service.IsRunning && !Confirm("重新启动文件服务", "重新启动会结束当前文件传输并更换访问码，是否继续？")) return;
        await RunAsync(async token =>
        {
            try { await _service.StartServerAsync(Root.Text, token: token); }
            finally { UpdateServerDetails(); }
        });
    }
    private async void StopServer_Click(object sender, RoutedEventArgs e)
    {
        if (_service.IsRunning && !Confirm("停止文件服务", "停止监听并取消当前文件传输？")) return;
        _operationCancellation?.Cancel(); await _operation;
        await RunAsync(async _ => { try { await _service.StopServerAsync(); } finally { UpdateServerDetails(); } });
    }
    private void UpdateServerDetails()
    {
        ServerStatus.Text = _service.IsRunning ? string.Join(Environment.NewLine, _service.EndpointAddresses) : "服务已停止。";
        ServerCode.Text = _service.IsRunning ? _service.AccessCode : "";
        CopyConnectionButton.IsEnabled = _service.IsRunning; ServerShareStatus.Text = "";
    }
    private void CopyConnection_Click(object sender, RoutedEventArgs e)
    {
        if (!_service.IsRunning || _closing) return;
        try
        {
            Clipboard.SetText(LanFileConnectionInfo.Encode(_service.EndpointAddresses, _service.AccessCode));
            ErrorText.Text = "";
            ServerShareStatus.Text = "已复制连接信息（含访问码），可发给对方导入；重新启动服务后失效。";
        }
        catch (COMException) { ErrorText.Text = "剪贴板正被占用，请重试。"; }
        catch (FormatException ex) { ErrorText.Text = ex.Message; }
    }
    private async void ImportConnection_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        if (_busy) { ErrorText.Text = "请等待当前操作完成，或先取消操作，再导入连接信息。"; return; }
        try
        {
            var info = LanFileConnectionInfo.Parse(Clipboard.GetText());
            var servers = info.Addresses.Select(address => { var uri = new Uri(address); return new LanFileServerInfo("导入的文件服务", uri.Host, uri.Port); }).ToArray();
            Server.ItemsSource = servers; Server.SelectedIndex = 0; Server.Text = servers[0].Endpoint; Code.Password = info.AccessCode;
            _path = _lastAddress = ""; UpdatePathUi(); Entries.ItemsSource = null; ErrorText.Text = "";
            ClientStatus.Text = servers.Length > 1 ? $"已导入 {servers.Length} 个候选地址与访问码，正在连接…" : "已导入地址与访问码，正在连接…";
            await BrowseAsync(string.Empty);
        }
        catch (COMException) { ErrorText.Text = "剪贴板正被占用，请重试。"; }
        catch (FormatException ex) { ErrorText.Text = ex.Message; }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e) => await BrowseAsync(string.Empty);
    private async void Discover_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        var servers = await _service.DiscoverAsync(TimeSpan.FromSeconds(3), token); Server.ItemsSource = servers;
        if (servers.Count > 0) Server.SelectedIndex = 0;
        ClientStatus.Text = servers.Count == 0 ? "未发现服务。确认两台电脑位于同一局域网且允许 UDP 39127。" : $"发现 {servers.Count} 个文件服务。输入访问码后点击刷新。";
    });
    private void Server_Changed(object sender, SelectionChangedEventArgs e) { _path = ""; UpdatePathUi(); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await BrowseAsync(_path);
    private async void Parent_Click(object sender, RoutedEventArgs e)
    {
        if (_path.Length == 0) return;
        var folder = _path.TrimEnd('/'); var split = folder.LastIndexOf('/'); await BrowseAsync(split < 0 ? "" : folder[..(split + 1)]);
    }
    private async void Entries_DoubleClick(object sender, MouseButtonEventArgs e)
    { if (Entries.SelectedItem is EntryView { IsDirectory: true } item) await BrowseAsync(_path + item.Name + "/"); }
    private Task BrowseAsync(string folder) => RunAsync(async token =>
    {
        if (_lastAddress != Server.Text) folder = "";
        await RefreshContentsAsync(folder, token);
    });
    private async Task RefreshContentsAsync(string folder, CancellationToken token)
    {
        var address = Server.Text; var entries = await _service.ListRemoteAsync(address, Code.Password, folder, token);
        Entries.ItemsSource = entries.Select(x => new EntryView(x.Name, x.IsDirectory, x.Length)).ToArray();
        _path = folder; _lastAddress = address; UpdatePathUi();
        ClientStatus.Text = $"{entries.Count} 项";
    }
    private void UpdatePathUi()
    {
        if (CurrentPath is null) return;
        CurrentPath.Text = "/" + _path; ParentButton.IsEnabled = _path.Length > 0;
    }
    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || Entries.SelectedItem is not EntryView { IsDirectory: false } item) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = item.Name, OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return;
        var address = Server.Text; var code = Code.Password; var remote = _path + item.Name;
        if (!TryGetRemotePath(remote, out remote, out var pathError)) { ErrorText.Text = pathError; return; }
        await RunAsync(async token =>
        {
            _transferring = true;
            Progress.Maximum = Math.Max(1, item.Length); Progress.Value = 0;
            await _service.DownloadAsync(address, code, remote, dialog.FileName, TransferProgress(token), token, TransferStatus(token));
            ClientStatus.Text = "下载完成，文件版本校验通过：" + item.Name;
        });
    }
    private void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { CheckFileExists = true, Multiselect = true }; if (dialog.ShowDialog(this) != true) return;
        var files = dialog.FileNames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var conflicts = files.Select(Path.GetFileName).Where(name => Entries.Items.OfType<EntryView>().Any(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))).ToArray();
        var overwrite = conflicts.Length > 0 && Confirm("覆盖远端文件", $"有 {conflicts.Length} 个文件已存在，是否覆盖这些文件？");
        if (conflicts.Length > 0 && !overwrite) files = files.Where(file => !conflicts.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)).ToArray();
        foreach (var file in files) QueueUpload(file, _path + Path.GetFileName(file), overwrite);
    }
    internal void QueueUpload(string source, string remote, bool overwrite)
    {
        if (_closing) return;
        if (!TryGetRemotePath(remote, out remote, out var pathError)) { ErrorText.Text = pathError; return; }
        if (_transferItems.Any(i => i.Address.Equals(Server.Text, StringComparison.OrdinalIgnoreCase) && i.RemotePath.Equals(remote, StringComparison.OrdinalIgnoreCase) && i.Status != "已完成"))
        { ErrorText.Text = "此目标已有上传任务，请先在传输面板继续该任务。"; return; }
        if (_transferItems.Count >= 200) { ErrorText.Text = "任务列表已满，请先清除已完成任务。"; return; }
        var item = new TransferItem(source, remote, overwrite, Server.Text, Code.Password); _transferItems.Add(item);
        TransferPanel.Visibility = Visibility.Collapsed; StartUpload(item); UpdateTransferCount();
    }

    private static bool TryGetRemotePath(string value, out string normalized, out string error)
    {
        normalized = value.Replace('\\', '/'); error = "";
        if (normalized.Length is 0 or > 2048 || normalized.StartsWith('/') || normalized.Contains(':') || normalized.Split('/').Any(part => part is "" or "." or ".."))
        {
            normalized = ""; error = "远端目标必须是共享根目录内的相对路径，请先进入目标目录后再上传。"; return false;
        }
        return true;
    }
    private void StartUpload(TransferItem item)
    {
        if (_closing || item.Running) return;
        item.Cancellation.Dispose(); item.Cancellation = new CancellationTokenSource(); item.Running = true; item.Status = "等待中"; item.Progress = 0;
        item.LastBytes = -1; item.LastTimestamp = 0; item.SpeedText = "--"; item.RemainingText = "--:--"; item.OnPropertyChanged(nameof(TransferItem.TransferStats));
        item.OnPropertyChanged(nameof(TransferItem.ActionText));
        if (Server.Text.Equals(item.Address, StringComparison.OrdinalIgnoreCase)) item.AccessCode = Code.Password;
        item.Work = UploadItemAsync(item, item.Address, item.AccessCode);
    }
    private async Task UploadItemAsync(TransferItem item, string address, string code)
    {
        var acquired = false; var token = item.Cancellation.Token;
        try
        {
            await _uploadSlots.WaitAsync(token); acquired = true;
            item.Status = "上传中"; item.Detail = "正在读取文件版本…";
            var length = new FileInfo(item.SourcePath).Length;
            var progress = new Progress<long>(value =>
            {
                if (!item.Running || token.IsCancellationRequested || _closing || item.Cancellation.Token != token) return;
                var percentage = Math.Min(100, value * 100d / Math.Max(1, length));
                if (percentage < item.Progress) return;
                var timestamp = Stopwatch.GetTimestamp();
                if (item.LastBytes < 0)
                {
                    item.LastBytes = value;
                    item.LastTimestamp = timestamp;
                }
                else if (value >= item.LastBytes && timestamp > item.LastTimestamp)
                {
                    var elapsed = (timestamp - item.LastTimestamp) / (double)Stopwatch.Frequency;
                    // Keep a stable sampling window: resetting on every chunk callback
                    // prevents fast transfers from ever reaching the sampling interval.
                    if (elapsed >= 0.25)
                    {
                        var rate = (value - item.LastBytes) / elapsed;
                        if (rate > 0)
                        {
                            item.SpeedText = FormatRate(rate);
                            var seconds = Math.Max(0, (length - value) / rate);
                            var remaining = !double.IsFinite(seconds) || seconds >= TimeSpan.MaxValue.TotalSeconds
                                ? TimeSpan.MaxValue
                                : TimeSpan.FromSeconds(seconds);
                            item.RemainingText = FormatDuration(remaining);
                        }
                        else
                        {
                            item.SpeedText = FormatRate(0);
                            item.RemainingText = "--:--";
                        }
                        item.OnPropertyChanged(nameof(TransferItem.TransferStats));
                        item.LastBytes = value;
                        item.LastTimestamp = timestamp;
                    }
                }
                item.Progress = percentage; item.Detail = $"{value:N0} / {length:N0} 字节";
            });
            var status = new Progress<string>(value => { if (item.Running && !token.IsCancellationRequested && !_closing && item.Cancellation.Token == token) item.Detail = value; });
            await _service.UploadParallelAsync(address, code, item.RemotePath, item.SourcePath, item.Overwrite, progress, token, status);
            item.Progress = 100; item.Status = "已完成"; item.Detail = "所有分块已校验并提交";
            var slash = item.RemotePath.LastIndexOf('/'); var folder = slash < 0 ? "" : item.RemotePath[..(slash + 1)];
            if (!_closing && !_busy && Server.Text.Equals(address, StringComparison.OrdinalIgnoreCase) && _path == folder)
                await BrowseAsync(folder);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { item.Status = "已暂停"; item.Detail = "进度已保留，可继续上传"; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or HttpRequestException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or TimeoutException or System.Net.Sockets.SocketException or System.Text.Json.JsonException)
        { item.Status = "失败"; item.Detail = DescribeError(ex); }
        finally
        {
            if (acquired) _uploadSlots.Release();
            item.Running = false; item.OnPropertyChanged(nameof(TransferItem.ActionText));
            UpdateTransferCount();
        }
    }
    private void TransferAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not TransferItem item) return;
        if (item.Running) { item.Detail = "正在暂停…"; item.Cancellation.Cancel(); }
        else if (item.Status is "已暂停" or "失败") StartUpload(item);
    }
    private void TransferPanel_Click(object sender, RoutedEventArgs e)
    {
        var expanded = TransferPanel.Visibility != Visibility.Visible;
        TransferPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        TransferButton.Visibility = expanded ? Visibility.Collapsed : (_transferItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed);
    }
    private void ClearTransfers_Click(object sender, RoutedEventArgs e)
    { foreach (var item in _transferItems.Where(i => i.Status == "已完成").ToArray()) { item.Cancellation.Dispose(); _transferItems.Remove(item); } UpdateTransferCount(); }
    private void UpdateTransferCount()
    {
        var active = _transferItems.Count(i => i.Status != "已完成");
        TransferCountText.Text = active == 0 ? "传输" : $"传输 ({active})";
        if (TransferPanel.Visibility != Visibility.Visible) TransferButton.Visibility = _transferItems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    private static string FormatRate(double bytesPerSecond)
    {
        var units = new[] { "B/s", "KiB/s", "MiB/s", "GiB/s" }; var value = bytesPerSecond; var index = 0;
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return $"{value:0.0} {units[index]}";
    }
    private static string FormatDuration(TimeSpan value)
    {
        if (value <= TimeSpan.Zero) return "00:00";
        if (value.TotalDays >= 1) return $"{(int)value.TotalDays}天 {value.Hours:00}:{value.Minutes:00}";
        return value.TotalHours >= 1 ? $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}" : $"{value.Minutes:00}:{value.Seconds:00}";
    }
    private IProgress<long> TransferProgress(CancellationToken token) => new Progress<long>(value =>
    { if (!token.IsCancellationRequested && !_closing && _operationCancellation?.Token == token) { Progress.IsIndeterminate = false; Progress.Value = value; ClientStatus.Text = $"已传输 {value:N0} / {Progress.Maximum:N0} 字节"; } });
    private IProgress<string> TransferStatus(CancellationToken token) => new Progress<string>(message =>
    { if (!token.IsCancellationRequested && !_closing && _operationCancellation?.Token == token) { Progress.IsIndeterminate = true; ClientStatus.Text = message; } });
    private bool Confirm(string title, string message) => new ConfirmationWindow(title, message, "继续") { Owner = this }.ShowDialog() == true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => _operationCancellation?.Cancel();
    private Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_busy || _closing) return Task.CompletedTask;
        return _operation = RunCoreAsync(action);
    }
    private async Task RunCoreAsync(Func<CancellationToken, Task> action)
    {
        _busy = true; ErrorText.Text = "";
        ImportConnectionButton.IsEnabled = DiscoverButton.IsEnabled = Server.IsEnabled = Code.IsEnabled = false;
        using var cancellation = new CancellationTokenSource(); _operationCancellation = cancellation;
        Progress.Visibility = Visibility.Visible; Progress.IsIndeterminate = true;
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { ClientStatus.Text = _transferring ? "传输已暂停，再次选择同一文件和目标即可续传。" : "操作已取消。"; }
        catch (Exception ex) when (ex is FormatException or TimeoutException or InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException or ArgumentException or System.Net.Sockets.SocketException or System.Net.HttpListenerException or System.Text.Json.JsonException)
        {
            ErrorText.Text = ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized } ? "访问码无效，请从服务端窗口获取当前访问码。" : DescribeError(ex);
            if (_transferring) ClientStatus.Text = "未完成传输已保留进度，排除错误后再次选择同一文件和目标可续传。";
        }
        finally
        {
            _operationCancellation = null; _busy = _transferring = false; Progress.IsIndeterminate = false; Progress.Visibility = Visibility.Collapsed;
            ImportConnectionButton.IsEnabled = DiscoverButton.IsEnabled = Server.IsEnabled = Code.IsEnabled = true;
        }
    }
    private static string DescribeError(Exception error)
    {
        var cause = error; while (cause.InnerException is not null) cause = cause.InnerException;
        return error is HttpRequestException && error.InnerException is not null ? "文件传输连接失败：" + cause.Message : error.Message;
    }
    internal Task ShutdownAsync() => _shutdown ??= ShutdownCoreAsync();
    private async Task ShutdownCoreAsync()
    {
        if (_cleaned) return; _closing = true; _operationCancellation?.Cancel();
        foreach (var item in _transferItems.Where(i => i.Running)) item.Cancellation.Cancel();
        await _operation; await Task.WhenAll(_transferItems.Select(i => i.Work));
        foreach (var item in _transferItems) item.Cancellation.Dispose();
        _service.Error -= OnServerError; await _service.DisposeAsync(); _uploadSlots.Dispose(); _cleaned = true;
    }
    protected override async void OnClosing(CancelEventArgs e)
    {
        if (!_cleaned)
        {
            e.Cancel = true; base.OnClosing(e); if (_closing || _choosingClose) return;
            if (HasActiveWork)
            {
                var choice = new LanFileCloseChoiceWindow { Owner = this }; bool accepted;
                _choosingClose = true;
                try { accepted = choice.ShowDialog() == true; }
                finally { _choosingClose = false; }
                if (!accepted) return;
                if (choice.KeepService) { KeepInBackground(); return; }
            }
            await ShutdownAsync(); _ = Dispatcher.BeginInvoke(new Action(Close)); return;
        }
        base.OnClosing(e);
    }
    internal void KeepInBackground() { _backgroundService = true; Hide(); }
    internal void RestoreFromBackground() { _backgroundService = false; Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); }
}
