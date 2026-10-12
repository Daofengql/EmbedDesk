using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace EmbedDesk.App.ViewModels;

public sealed class RemoteFileDocumentViewModel : ObservableObject, IDisposable
{
    private readonly Func<string, string, Task> _upload;
    private Encoding _encoding = new UTF8Encoding(false, true);
    private byte[] _preamble = [];
    private string _text = string.Empty;
    private string _savedText = string.Empty;
    private string _status = string.Empty;
    private bool _saving;

    private RemoteFileDocumentViewModel(RemoteWorkspaceViewModel owner, string remotePath, string cachePath, Func<string, string, Task> upload)
    {
        Owner = owner;
        RemotePath = remotePath;
        CachePath = cachePath;
        Name = remotePath.Split('/').Last();
        _upload = upload;
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsImage && IsDirty && !_saving && Owner.IsConnected);
        Owner.PropertyChanged += OwnerChanged;
    }

    public RemoteWorkspaceViewModel Owner { get; }
    public string Name { get; }
    public string RemotePath { get; }
    public string CachePath { get; }
    public string Location => $"{Owner.Username}@{Owner.Host}:{RemotePath}";
    public string Title => Name + (IsDirty ? " •" : string.Empty);
    public bool IsImage => Image is not null;
    public bool IsText => !IsImage;
    public ImageSource? Image { get; private set; }
    public string Text
    {
        get => _text;
        set { if (SetProperty(ref _text, value)) { OnPropertyChanged(nameof(IsDirty)); OnPropertyChanged(nameof(Title)); SaveCommand.NotifyCanExecuteChanged(); } }
    }
    public bool IsDirty => _text != _savedText;
    public bool IsSaving => _saving;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public IAsyncRelayCommand SaveCommand { get; }

    public static async Task<RemoteFileDocumentViewModel> LoadAsync(RemoteWorkspaceViewModel owner, string remotePath, string cachePath, Func<string, string, Task> upload)
    {
        var document = new RemoteFileDocumentViewModel(owner, remotePath, cachePath, upload);
        try
        {
            var extension = Path.GetExtension(remotePath).ToLowerInvariant();
            if (new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".tif", ".tiff" }.Contains(extension))
            {
                using var stream = File.OpenRead(cachePath);
                // Bound decoded image memory even for compressed images with huge dimensions.
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                if ((long)decoder.Frames[0].PixelWidth * decoder.Frames[0].PixelHeight > 40_000_000)
                    throw new InvalidDataException("图片分辨率过大，请使用右键菜单下载后查看。");
                stream.Position = 0;
                var image = new BitmapImage();
                image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
                document.Image = image;
                document.Status = $"图片预览 · {image.PixelWidth} × {image.PixelHeight}";
            }
            else
            {
                var bytes = await File.ReadAllBytesAsync(cachePath);
                if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 })) document._encoding = new UTF32Encoding(false, true, true);
                else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF })) document._encoding = new UTF32Encoding(true, true, true);
                else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) document._encoding = new UnicodeEncoding(false, true, true);
                else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) document._encoding = new UnicodeEncoding(true, true, true);
                else if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) document._encoding = new UTF8Encoding(true, true);
                document._preamble = document._encoding.GetPreamble();
                document._text = document._encoding.GetString(bytes, document._preamble.Length, bytes.Length - document._preamble.Length);
                if (document._text.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t' and not '\f'))
                    throw new InvalidDataException("该文件不是可编辑文本，请使用右键菜单下载。");
                document._savedText = document._text;
                document.Status = $"{document._encoding.WebName} · 修改后可保存到远程";
            }
            return document;
        }
        catch { document.Dispose(); throw; }
    }

    private async Task SaveAsync()
    {
        _saving = true;
        OnPropertyChanged(nameof(IsSaving));
        SaveCommand.NotifyCanExecuteChanged();
        var snapshot = Text;
        try
        {
            Status = "正在保存到远程…";
            var bytes = _preamble.Concat(_encoding.GetBytes(snapshot)).ToArray();
            await File.WriteAllBytesAsync(CachePath, bytes);
            await _upload(CachePath, RemotePath);
            _savedText = snapshot;
            OnPropertyChanged(nameof(IsDirty)); OnPropertyChanged(nameof(Title));
            Status = "已保存到远程";
        }
        catch (Exception ex) { Status = $"保存失败：{ex.Message}"; }
        finally { _saving = false; OnPropertyChanged(nameof(IsSaving)); SaveCommand.NotifyCanExecuteChanged(); }
    }

    private void OwnerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Owner.IsConnected)) SaveCommand.NotifyCanExecuteChanged();
    }
    public void Dispose()
    {
        Owner.PropertyChanged -= OwnerChanged;
        try { File.Delete(CachePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
