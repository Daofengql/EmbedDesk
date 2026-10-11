using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Windows;
using EmbeddedDebugger.Infrastructure.Firmware;
using Microsoft.Win32;

namespace EmbeddedDebugger.App;

public partial class ImageWriterWindow : Window
{
    private RemovableMediaInfo? _disk;
    private FileInfo? _image;
    private bool _busy;
    private bool _closed;

    public ImageWriterWindow()
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => _closed = true;
    }

    private async void Choose_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new OpenFileDialog { Title = "选择 ISO / IMG 镜像", Filter = "磁盘镜像|*.iso;*.img;*.bin;*.raw|所有文件|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        _image = new FileInfo(dialog.FileName); ImagePathBox.Text = _image.FullName;
        await using var stream = new FileStream(_image.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
        DetailsBox.Text = $"文件大小：{_image.Length:N0} 字节\nSHA-256：{hash}\n目标磁盘：选择后显示。";
        UpdateState();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async Task RefreshAsync()
    {
        if (_busy) return;
        try
        {
            DiskBox.ItemsSource = await RemovableMediaService.EnumerateAsync();
            DiskBox.SelectedIndex = 0;
            Status.Text = DiskBox.Items.Count == 0 ? "未发现 USB 或可移动磁盘。" : "请选择目标磁盘。";
            UpdateState();
        }
        catch (Exception ex) { Status.Text = "读取磁盘列表失败：" + ex.Message; }
    }

    private void Disk_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) { _disk = DiskBox.SelectedItem as RemovableMediaInfo; UpdateState(); }
    private async void Write_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _image is null || _disk is null) return;
        var confirm = new ConfirmationWindow("覆盖物理磁盘", $"即将把 {_image.Name} 写入 {_disk.DisplayName}。目标磁盘上的分区和文件将全部丢失，确定继续吗？", "确认擦除并写入") { Owner = this };
        if (confirm.ShowDialog() != true) return;
        _busy = true; WriteButton.IsEnabled = false; Progress.Value = 0; Status.Text = "正在写入…";
        try
        {
            var progress = new Progress<MediaProgress>(value => Progress.Value = value.Percent);
            await RemovableMediaService.WriteImageAsync(_image.FullName, _disk, progress);
            Progress.Value = 100; Status.Text = "镜像写入完成。请安全弹出设备后再拔出。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { Status.Text = "写入失败：" + ex.Message; }
        finally { _busy = false; UpdateState(); }
    }

    private async void Format_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _disk is null) return;
        var confirm = new ConfirmationWindow("格式化 FAT32", $"即将格式化 {_disk.DisplayName}，目标磁盘上的全部数据将丢失。确定继续吗？", "确认格式化") { Owner = this };
        if (confirm.ShowDialog() != true) return;
        _busy = true; WriteButton.IsEnabled = false; FormatButton.IsEnabled = false; Progress.Value = 0; Status.Text = "正在格式化 FAT32…";
        try
        {
            var progress = new Progress<MediaProgress>(value => Progress.Value = value.Percent);
            await RemovableMediaService.FormatFat32Async(_disk, LabelBox.Text.Trim(), progress);
            Progress.Value = 100; Status.Text = "FAT32 格式化完成，已回读校验关键元数据。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        { Status.Text = "格式化失败：" + ex.Message; }
        finally { _busy = false; UpdateState(); }
    }

    private void UpdateState()
    {
        if (_closed || WriteButton is null) return;
        WriteButton.IsEnabled = !_busy && _image is { Exists: true } && _disk is not null && _image.Length <= _disk.Size;
        FormatButton.IsEnabled = !_busy && _disk is not null;
        if (_image is { Exists: true } && _disk is { } disk && _image.Length > disk.Size) Status.Text = "镜像大于目标磁盘，无法写入。";
    }
}
