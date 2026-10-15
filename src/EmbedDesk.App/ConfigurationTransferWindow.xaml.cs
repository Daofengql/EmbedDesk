using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using EmbedDesk.App.ViewModels;

namespace EmbedDesk.App;

public partial class ConfigurationTransferWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public ConfigurationTransferWindow(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        _viewModel = viewModel;
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            IEnumerable<EmbedDesk.Core.Models.RemoteConnectionProfile> remote = _viewModel.RemoteDebug?.Profiles is { } profiles
                ? profiles
                : Array.Empty<EmbedDesk.Core.Models.RemoteConnectionProfile>();
            var bytes = ConfigurationTransferService.Export(remote, [],
                _viewModel.GetSerialBridgePresets(), _viewModel.AppSettings.GetStcpTunnels());
            var dialog = new SaveFileDialog
            {
                Title = "导出 EmbedDesk 配置",
                Filter = "EmbedDesk 配置 (*.json)|*.json|所有文件 (*.*)|*.*",
                DefaultExt = ".json",
                AddExtension = true,
                FileName = "EmbedDesk-configuration.json",
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(this) != true) return;
            WriteAtomically(dialog.FileName, bytes);
            StatusText.Text = $"已导出 {bytes.Length / 1024.0:0.##} KiB。文件不包含密码、私钥、S3 密钥或 STCP 共享密钥。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowError("导出配置失败", exception.Message);
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入 EmbedDesk 配置",
            Filter = "EmbedDesk 配置 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var length = new FileInfo(dialog.FileName).Length;
            if (length is <= 0 or > ConfigurationTransferService.MaximumFileBytes)
                throw new InvalidDataException("配置文件为空或超过 1 MiB 上限。");
            var imported = ConfigurationTransferService.Import(File.ReadAllBytes(dialog.FileName));
            var preview = new ConfigurationImportPreviewWindow(imported) { Owner = this };
            if (preview.ShowDialog() != true) return;

            var result = _viewModel.ImportConfiguration(imported);
            StatusText.Text = $"导入完成：新增 {result.Added} 项，跳过 {result.Skipped} 项（同名配置或已达到每类 100 项上限）。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            ShowError("导入配置失败", exception.Message);
        }
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void ShowError(string title, string message) => MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}
