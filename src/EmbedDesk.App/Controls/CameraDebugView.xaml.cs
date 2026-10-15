using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EmbedDesk.Core.Abstractions;
using EmbedDesk.Core.Models;
using Microsoft.Win32;

namespace EmbedDesk.App.Controls;

public partial class CameraDebugView : UserControl
{
    private sealed record ImageFormat(byte Kind, int Width, int Height);
    private sealed record RegisterRequest(byte Address, ushort Sequence, int Width, bool Read, TaskCompletionSource<string> Result);
    private readonly ImageFormat?[] _formats = new ImageFormat?[2];
    private readonly BitmapSource?[] _latest = new BitmapSource?[2];
    private readonly object _imageLock = new();
    private DebugToolPacketReader _packets = new(true);
    private JpegStreamReader _jpeg = new();
    private bool _jpegMode, _bigEndian, _verifyCrc, _closing;
    private Task? _shutdown;
    private Task _registerTask = Task.CompletedTask;
    private long _frames, _errors, _bytes, _lastFrames;
    private readonly Stopwatch _rateWatch = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer;
    private RegisterRequest? _pending;
    private ushort _sequence;
    public bool HasActiveWork => Connection.IsActive;
    public string ConnectionStatus => Connection.Status;
    public bool HasUnsavedChanges => false;
    public event EventHandler? ActivityChanged;
    internal DebugToolConnectionPanel ConnectionPanel => Connection;
    internal void UseEmbeddedLayout() { ViewHeading.Visibility = Visibility.Collapsed; Connection.UseEmbeddedLayout(); }

    public CameraDebugView(ISerialPortService serial)
    {
        InitializeComponent(); Connection.Initialize(serial);
        Connection.Starting = () =>
        {
            _jpegMode = ModeBox.SelectedIndex == 1; _bigEndian = EndianBox.SelectedIndex == 1;
            _verifyCrc = CrcBox.IsChecked == true;
            _packets = new(true, false); _jpeg.Dispose(); _jpeg = new(); Array.Clear(_formats);
            _frames = _errors = _bytes = _lastFrames = 0; _rateWatch.Restart();
            lock (_imageLock) Array.Clear(_latest);
            Preview0.Source = Preview1.Source = null;
        };
        Connection.ActivityChanged += (_, _) =>
        {
            if (!Connection.IsActive) _pending?.Result.TrySetCanceled();
            FormatSettings.IsEnabled = !Connection.IsActive;
            RegisterPanel.IsEnabled = Connection.IsActive && !_jpegMode && _pending is null;
            ActivityChanged?.Invoke(this, EventArgs.Empty);
        };
        Connection.ReceiveAsync = ReceiveAsync; RegisterPanel.IsEnabled = false;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) => RefreshImages(), Dispatcher);
    }

    private async Task ReceiveAsync(ReadOnlyMemory<byte> bytes)
    {
        Interlocked.Add(ref _bytes, bytes.Length);
        if (_jpegMode)
        {
            _jpeg.Feed(bytes.Span, image => AcceptImage(0, new(1, 0, 0), image));
            return;
        }
        var replies = new List<byte[]>();
        _packets.Feed(bytes.Span, packet =>
        {
            if (packet.Address > 1) return;
            if (_verifyCrc && !packet.ChecksumValid)
            {
                Interlocked.Increment(ref _errors);
                if (packet.Command is 1 or 2) replies.Add(DebugToolPacketReader.Encode(true, packet.Address, 0, [1]));
                return;
            }
            var data = packet.Payload.AsSpan();
            if (packet.Command is 0 or 0x11)
            {
                var pending = Volatile.Read(ref _pending);
                if (pending is null || pending.Address != packet.Address) return;
                if (pending.Read && packet.Command == 0x11 && data.Length >= 3 && BinaryPrimitives.ReadUInt16LittleEndian(data[1..]) == pending.Sequence)
                {
                    if (data[0] == 0 && data.Length == 3 + pending.Width)
                        pending.Result.TrySetResult("读取成功 · " + Convert.ToHexString(data[3..].ToArray().Reverse().ToArray()));
                    else pending.Result.TrySetException(new IOException($"读取失败：确认码 0x{data[0]:X2} 或返回长度无效。"));
                }
                else if (!pending.Read && packet.Command == 0 && data.Length == 1)
                {
                    if (data[0] == 0) pending.Result.TrySetResult("写入成功 · 开发板已确认");
                    else pending.Result.TrySetException(new IOException($"开发板拒绝写入：确认码 0x{data[0]:X2}。"));
                }
                return;
            }
            byte result = 1;
            if (packet.Command == 1 && data.Length == 5)
            {
                var format = new ImageFormat(data[0], BinaryPrimitives.ReadUInt16LittleEndian(data[1..]), BinaryPrimitives.ReadUInt16LittleEndian(data[3..]));
                if (format.Kind is >= 1 and <= 5 && ValidSize(format.Width, format.Height)) { _formats[packet.Address] = format; result = 0; }
            }
            else if (packet.Command == 2 && _formats[packet.Address] is { } format)
                result = AcceptImage(packet.Address, format, packet.Payload) ? (byte)0 : (byte)1;
            if (result != 0) Interlocked.Increment(ref _errors);
            replies.Add(DebugToolPacketReader.Encode(true, packet.Address, 0, [result]));
        });
        foreach (var reply in replies) await Connection.SendAsync(reply);
    }

    private bool AcceptImage(byte address, ImageFormat format, byte[] bytes)
    {
        try
        {
            var image = DecodeImage(format, bytes, _bigEndian);
            lock (_imageLock) _latest[address] = image;
            Interlocked.Increment(ref _frames); return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        { if (_jpegMode) Interlocked.Increment(ref _errors); return false; }
    }

    private static bool ValidSize(int width, int height) => width is > 0 and <= 4096 && height is > 0 and <= 4096 && (long)width * height <= 4194304;

    private static BitmapSource DecodeImage(ImageFormat format, byte[] bytes, bool bigEndian)
    {
        if (format.Kind <= 3)
        {
            using var stream = new MemoryStream(bytes, false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
            if (decoder.Frames.Count != 1 || (format.Kind == 1 && decoder is not JpegBitmapDecoder) || (format.Kind == 2 && decoder is not BmpBitmapDecoder) || (format.Kind == 3 && decoder is not PngBitmapDecoder)) throw new IOException("图像文件格式不符。");
            var frame = decoder.Frames[0];
            if (!ValidSize(frame.PixelWidth, frame.PixelHeight)) throw new IOException("图像尺寸超出限制。");
            if (format.Width > 0 && (frame.PixelWidth != format.Width || frame.PixelHeight != format.Height)) throw new IOException("图像尺寸与格式包不符。");
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgr24, null, 0);
            var pixels = new byte[frame.PixelWidth * frame.PixelHeight * 3]; converted.CopyPixels(pixels, frame.PixelWidth * 3, 0);
            var image = BitmapSource.Create(frame.PixelWidth, frame.PixelHeight, 96, 96, PixelFormats.Bgr24, null, pixels, frame.PixelWidth * 3); image.Freeze(); return image;
        }
        var count = checked(format.Width * format.Height);
        if (!ValidSize(format.Width, format.Height) || bytes.Length != count * (format.Kind == 4 ? 2 : 3)) throw new IOException("原始像素长度不符。");
        var output = new byte[count * 3];
        for (var index = 0; index < count; index++)
        {
            if (format.Kind == 4)
            {
                var pixel = bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(index * 2)) : BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(index * 2));
                output[index * 3] = (byte)((pixel & 31) * 255 / 31); output[index * 3 + 1] = (byte)(((pixel >> 5) & 63) * 255 / 63); output[index * 3 + 2] = (byte)((pixel >> 11) * 255 / 31);
            }
            else { output[index * 3] = bytes[index * 3 + 2]; output[index * 3 + 1] = bytes[index * 3 + 1]; output[index * 3 + 2] = bytes[index * 3]; }
        }
        var raw = BitmapSource.Create(format.Width, format.Height, 96, 96, PixelFormats.Bgr24, null, output, format.Width * 3); raw.Freeze(); return raw;
    }

    private void RefreshImages()
    {
        if (!IsVisible) return;
        if (PauseBox.IsChecked != true)
        {
            lock (_imageLock)
            {
                if (_latest[0] is { } first) { Preview0.Source = first; ImageLabel0.Text = $"设备 0 · {first.PixelWidth} × {first.PixelHeight}"; }
                if (_latest[1] is { } second) { Preview1.Source = second; ImageLabel1.Text = $"设备 1 · {second.PixelWidth} × {second.PixelHeight}"; }
            }
        }
        if (_rateWatch.ElapsedMilliseconds < 1000) return;
        var frames = Interlocked.Read(ref _frames);
        StatusText.Text = $"有效帧 {frames:N0} · {(frames - _lastFrames) / _rateWatch.Elapsed.TotalSeconds:F1} FPS · RX {Interlocked.Read(ref _bytes):N0} 字节 · 拒绝 {_errors + _packets.RejectedPackets + _jpeg.RejectedFrames:N0}";
        _lastFrames = frames; _rateWatch.Restart();
    }

    private void Devices_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SecondColumn is null) return;
        var dual = DeviceCountBox.SelectedIndex == 1;
        SecondColumn.Width = dual ? new GridLength(1, GridUnitType.Star) : new GridLength(0); SecondImagePanel.Visibility = dual ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Snapshot_Click(object sender, RoutedEventArgs e)
    {
        var source = (AddressBox.SelectedIndex == 1 ? Preview1.Source : Preview0.Source) as BitmapSource;
        if (source is null) { RegisterStatus.Text = "选中设备尚无画面。"; return; }
        var picker = new SaveFileDialog { Filter = "PNG 图像|*.png", FileName = "camera.png" };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        try { using var file = File.Create(picker.FileName); new PngBitmapEncoder { Frames = { BitmapFrame.Create(source) } }.Save(file); RegisterStatus.Text = "画面已保存。"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { RegisterStatus.Text = ex.Message; }
    }

    private async void Register_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || !_registerTask.IsCompleted) return;
        _registerTask = RegisterAsync(Equals((sender as Button)?.Tag, "Read"));
        await _registerTask;
    }

    private async Task RegisterAsync(bool read)
    {
        try
        {
            var addressWidth = AddressWidthBox.SelectedIndex + 1; var width = ValueWidthBox.SelectedIndex + 1;
            var address = uint.Parse(RegisterAddressBox.Text.Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var value = read ? 0u : uint.Parse(RegisterValueBox.Text.Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if ((addressWidth < 4 && address >= (1u << (addressWidth * 8))) || (width < 4 && value >= (1u << (width * 8)))) throw new FormatException("地址或数值超出所选字节数。");
            var pending = new RegisterRequest((byte)AddressBox.SelectedIndex, ++_sequence, width, read, new(TaskCreationOptions.RunContinuationsAsynchronously));
            var payload = new byte[read ? 2 + addressWidth : addressWidth + width];
            if (read) BinaryPrimitives.WriteUInt16LittleEndian(payload, pending.Sequence);
            for (var i = 0; i < addressWidth; i++) payload[(read ? 2 : 0) + i] = (byte)(address >> (8 * i));
            if (!read) for (var i = 0; i < width; i++) payload[addressWidth + i] = (byte)(value >> (8 * i));
            _pending = pending; RegisterPanel.IsEnabled = false;
            await Connection.SendAsync(DebugToolPacketReader.Encode(true, pending.Address, read ? (byte)0x11 : (byte)0x10, payload));
            if (!_closing) RegisterStatus.Text = "已发送，等待开发板确认…";
            var result = await pending.Result.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (!_closing) RegisterStatus.Text = result;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or FormatException or OverflowException or TimeoutException or OperationCanceledException or System.Net.Sockets.SocketException)
        { if (!_closing) RegisterStatus.Text = ex is TimeoutException ? "开发板应答超时，未确认成功。" : ex.Message; }
        finally { _pending = null; if (!_closing) RegisterPanel.IsEnabled = Connection.IsActive && !_jpegMode; }
    }

    public Task ShutdownAsync() => _shutdown ??= ShutdownCoreAsync();
    private async Task ShutdownCoreAsync()
    {
        _closing = true; _timer.Stop(); _pending?.Result.TrySetCanceled();
        IsEnabled = false;
        await Connection.StopAsync(); await _registerTask; _jpeg.Dispose();
    }
}
