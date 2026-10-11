using System.ComponentModel;
using System.Security.Cryptography;
using System.IO;
using System.Windows;
using EmbeddedDebugger.Infrastructure.Firmware;
using Microsoft.Win32;

namespace EmbeddedDebugger.App;

public partial class ImageWriterWindow : Window
{
    private RemovableMediaInfo? _disk;
    private FileInfo? _image;
    private CancellationTokenSource? _operation;
    private bool _closeWhenIdle, _destructive;
    private bool Busy => _operation is not null;

    public ImageWriterWindow()
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        Loaded += async (_, _) => await RefreshAsync();
        Closing += OnClosing;
    }

    private async void Choose_Click(object sender, RoutedEventArgs e)
    {
        if (Busy) return;
        var dialog = new OpenFileDialog { Title = "选择原始磁盘镜像", Filter = "磁盘镜像|*.iso;*.img;*.bin;*.raw|所有文件|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        _image = null;
        ImagePathBox.Text = dialog.FileName;
        await RunAsync("计算镜像 SHA-256", false, async token =>
        {
            var image = new FileInfo(dialog.FileName);
            await using var stream = new FileStream(image.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
            if (stream.Length == 0) throw new IOException("镜像为空。");
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
            token.ThrowIfCancellationRequested();
            _image = image;
            DetailsBox.Text = $"文件：{image.FullName}\n大小：{stream.Length:N0} 字节\nSHA-256：{hash}";
            Status.Text = "镜像检查完成，请选择目标磁盘。";
        });
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private Task RefreshAsync() => RunAsync("读取磁盘列表", false, async token =>
    {
        var disks = await RemovableMediaService.EnumerateAsync(token);
        token.ThrowIfCancellationRequested();
        DiskBox.ItemsSource = disks;
        DiskBox.SelectedIndex = disks.Count > 0 ? 0 : -1;
        Status.Text = disks.Count == 0 ? "未发现可写入的 USB / SD / MMC 磁盘。" : "请选择目标磁盘。";
    });

    private void Disk_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _disk = DiskBox.SelectedItem as RemovableMediaInfo;
        UpdateState();
    }

    private async void Write_Click(object sender, RoutedEventArgs e)
    {
        if (Busy || _image is not { } image || _disk is not { } disk) return;
        var confirm = new ConfirmationWindow("覆盖物理磁盘", $"即将把 {image.Name} 写入 {disk.DisplayName}。目标磁盘上的分区和文件将全部丢失，确定继续吗？", "确认擦除并写入") { Owner = this };
        if (confirm.ShowDialog() != true) return;
        await RunAsync("写入镜像", true, async token =>
        {
            await RemovableMediaService.WriteImageAsync(image.FullName, disk, CreateProgress(token), token);
            Progress.Value = 100;
            Status.Text = "镜像写入与 SHA-256 回读校验完成。请安全弹出设备。";
        });
    }

    private async void Format_Click(object sender, RoutedEventArgs e)
    {
        if (Busy || _disk is not { } disk) return;
        var label = LabelBox.Text.Trim();
        try { _ = MediaImageOperations.GetFat32Layout(disk.Size, label); }
        catch (ArgumentException ex) { Status.Text = ex.Message; return; }
        var confirm = new ConfirmationWindow("格式化 FAT32", $"即将格式化 {disk.DisplayName}，目标磁盘上的全部数据将丢失。确定继续吗？", "确认格式化") { Owner = this };
        if (confirm.ShowDialog() != true) return;
        await RunAsync("格式化 FAT32", true, async token =>
        {
            await RemovableMediaService.FormatFat32Async(disk, label, CreateProgress(token), token);
            Progress.Value = 100;
            Status.Text = "FAT32 格式化完成，已回读校验关键元数据。";
        });
    }

    private IProgress<MediaProgress> CreateProgress(CancellationToken token) => new Progress<MediaProgress>(value =>
    {
        if (_operation?.Token != token || token.IsCancellationRequested) return;
        Progress.IsIndeterminate = false;
        Progress.Value = value.Percent;
        Status.Text = $"{value.Phase} · {value.Percent:0.0}% · {value.Completed:N0} / {value.Total:N0} 字节";
    });

    private async Task RunAsync(string description, bool destructive, Func<CancellationToken, Task> action)
    {
        if (Busy || _closeWhenIdle) return;
        using var cancellation = new CancellationTokenSource();
        _operation = cancellation; _destructive = destructive;
        Progress.Value = 0; Progress.IsIndeterminate = !destructive;
        Status.Text = description + "…"; UpdateState();
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { Status.Text = destructive ? "操作已停止；目标盘可能已部分写入，需要重新写入或格式化。" : "已取消。"; }
        catch (Exception ex) { Status.Text = description + "失败：" + ex.Message; }
        finally
        {
            _operation = null; _destructive = false; Progress.IsIndeterminate = false;
            UpdateState();
            if (_closeWhenIdle) Close();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelOperation();
    private void CancelOperation()
    {
        _operation?.Cancel();
        CancelButton.IsEnabled = false;
        Status.Text = _destructive ? "正在停止并释放磁盘；已写入的数据不能自动回滚…" : "正在取消…";
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!Busy) return;
        e.Cancel = true;
        _closeWhenIdle = true;
        CancelOperation();
    }
    private void UpdateState()
    {
        if (WriteButton is null) return;
        Configuration.IsEnabled = !Busy;
        CancelButton.IsEnabled = Busy;
        WriteButton.IsEnabled = !Busy && _image is { Exists: true, Length: > 0 } && _disk is not null && _image.Length <= _disk.Size;
        FormatButton.IsEnabled = !Busy && _disk is { SectorSize: 512 };
        FormatButton.ToolTip = "FAT32 支持 256 MiB–2 TiB、512 字节逻辑扇区的磁盘";
        if (!Busy && _image is { Exists: true } && _disk is { } disk && _image.Length > disk.Size) Status.Text = "镜像大于目标磁盘，无法写入。";
    }
}
