using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EmbeddedDebugger.App.ViewModels;
using EmbeddedDebugger.Core.Models;
using EmbeddedDebugger.Infrastructure.Remote;

namespace EmbeddedDebugger.App.Controls;

public partial class DesktopWorkspace : UserControl, IAsyncDisposable
{
    private DesktopSessionViewModel? _vm;
    private RdpClientHost? _rdp;
    private WindowsFormsHost? _host;
    private VncClient? _vnc;
    private Image? _image;
    private WriteableBitmap? _bitmap;
    private readonly DispatcherTimer _resize = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly HashSet<uint> _pressedKeys = [];
    private byte _buttons;
    private int _generation;
    private bool _connecting;
    private bool _started;
    public DesktopWorkspace()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        _resize.Tick += (_, _) => { _resize.Stop(); ResizeRdp(); };
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_started || DataContext is not DesktopSessionViewModel vm || vm.IsDisposed) return;
        _started = true; _vm = vm;
        vm.ConnectRequested += ConnectRequested;
        vm.DisconnectRequested += DisconnectRequested;
        vm.Disposed += SessionDisposed;
        vm.PropertyChanged += SessionChanged;
        PasteButton.Visibility = vm.Profile.Protocol == RemoteProtocol.Vnc ? Visibility.Visible : Visibility.Collapsed;
        await ConnectAsync();
    }
    private void SessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DesktopSessionViewModel.IsConnected)) return;
        var connected = _vm?.IsConnected == true;
        Placeholder.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        // Keep the native RDP surface present during login so Windows can show its
        // credential UI, but hide it after failure to expose our retry/error panel.
        if (_host is not null) _host.Visibility = connected || _vm?.IsConnecting == true ? Visibility.Visible : Visibility.Collapsed;
        if (_image is not null) _image.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void ConnectRequested(object? sender, EventArgs e) => await ConnectAsync();
    private async void DisconnectRequested(object? sender, EventArgs e)
    {
        await StopAsync();
        _vm?.SetState("已断开", "连接已断开，可点击重新连接。");
    }
    private async Task ConnectAsync()
    {
        if (_vm is null || _vm.IsDisposed || _connecting) return;
        _connecting = true;
        var generation = -1;
        try
        {
            await StopAsync();
            generation = _generation;
            if (_vm.IsDisposed) return;
            var profile = _vm.Profile;
            _vm.SetState("连接中", $"正在连接 {profile.Host}:{profile.Port}…");
            if (profile.Protocol == RemoteProtocol.Rdp)
            {
                _rdp = new RdpClientHost { Dock = System.Windows.Forms.DockStyle.Fill };
                ((ISupportInitialize)_rdp).BeginInit();
                _host = new WindowsFormsHost { Child = _rdp };
                DesktopSurface.Children.Add(_host);
                ((ISupportInitialize)_rdp).EndInit();
                _host.UpdateLayout();
                _rdp.CreateControl();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                if (generation != _generation || _vm.IsDisposed || _rdp is null) return;
                _rdp.StateChanged += (state, text) => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (generation != _generation || _vm.IsDisposed) return;
                    _vm.SetState(state, text);
                    if (state == "已连接") ResizeRdp();
                }));
                dynamic client = _rdp.Client;
                client.Server = profile.Host;
                var username = profile.Username;
                var separator = username.IndexOf('\\');
                if (separator > 0) { client.Domain = username[..separator]; username = username[(separator + 1)..]; }
                client.UserName = username;
                client.ColorDepth = 32;
                client.DesktopWidth = Math.Max(320, (int)DesktopSurface.ActualWidth);
                client.DesktopHeight = Math.Max(200, (int)DesktopSurface.ActualHeight);
                dynamic advanced = client.AdvancedSettings9;
                advanced.RDPPort = profile.Port;
                advanced.EnableCredSspSupport = true;
                advanced.AuthenticationLevel = 2u; // Preserve Windows certificate warnings.
                advanced.SmartSizing = true;
                advanced.RedirectClipboard = true;
                advanced.RedirectDrives = false;
                advanced.RedirectPrinters = false;
                advanced.EnableAutoReconnect = true;
                advanced.overallConnectionTimeout = 20;
                if (!string.IsNullOrEmpty(profile.Password)) advanced.ClearTextPassword = profile.Password;
                client.Connect();
            }
            else
            {
                var vnc = new VncClient(); _vnc = vnc;
                _image = new Image { Stretch = Stretch.Uniform, Focusable = true, Cursor = Cursors.Arrow };
                RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.LowQuality);
                _image.MouseDown += VncMouseDown; _image.MouseUp += VncMouseUp;
                _image.MouseMove += VncMouseMove; _image.MouseWheel += VncMouseWheel;
                _image.PreviewKeyDown += VncKeyDown; _image.PreviewKeyUp += VncKeyUp;
                _image.PreviewTextInput += VncTextInput; _image.LostKeyboardFocus += VncLostFocus;
                DesktopSurface.Children.Add(_image);
                vnc.FrameReceived = frame => Dispatcher.InvokeAsync(() =>
                {
                    if (generation != _generation || _image is null) return;
                    if (_bitmap?.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
                        _image.Source = _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null);
                    _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Width * 4, 0);
                }).Task;
                vnc.Disconnected += text => Dispatcher.BeginInvoke(new Action(() => { if (generation == _generation) _vm.SetState("已断开", text); }));
                await vnc.ConnectAsync(profile.Host, profile.Port, profile.Password);
                if (generation == _generation && !_vm.IsDisposed)
                    _vm.SetState("已连接", $"{vnc.DesktopName} · VNC 桌面 · 按窗口比例缩放");
            }
        }
        catch (Exception ex)
        {
            if (generation != _generation || _vm?.IsDisposed == true) return;
            await StopAsync();
            if (_vm?.IsDisposed != true) _vm?.SetState("连接失败", ex.GetBaseException().Message);
        }
        finally { _connecting = false; }
    }
    private async Task StopAsync()
    {
        _generation++; _resize.Stop(); _pressedKeys.Clear(); _buttons = 0;
        var vnc = _vnc; _vnc = null;
        if (_rdp is not null)
        {
            try { if (_rdp.IsHandleCreated && _rdp.Client.Connected != 0) _rdp.Client.Disconnect(); }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or System.Windows.Forms.AxHost.InvalidActiveXStateException) { }
            if (_host is not null) { DesktopSurface.Children.Remove(_host); _host.Dispose(); }
            _rdp.Dispose();
            _rdp = null; _host = null;
        }
        if (_image is not null) { DesktopSurface.Children.Remove(_image); _image = null; _bitmap = null; }
        if (vnc is not null) await vnc.DisposeAsync();
    }
    private async void SessionDisposed(object? sender, EventArgs e)
        => await DisposeAsync();
    public async ValueTask DisposeAsync()
    {
        if (_vm is not null)
        {
            _vm.ConnectRequested -= ConnectRequested; _vm.DisconnectRequested -= DisconnectRequested;
            _vm.Disposed -= SessionDisposed; _vm.PropertyChanged -= SessionChanged;
        }
        await StopAsync();
        GC.SuppressFinalize(this);
    }
    private void Surface_SizeChanged(object sender, SizeChangedEventArgs e) { _resize.Stop(); _resize.Start(); }
    private void ResizeRdp()
    {
        if (_rdp is null || _vm?.IsConnected != true || !IsVisible) return;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var width = (uint)Math.Clamp(DesktopSurface.ActualWidth * dpi.DpiScaleX, 320, 8192);
            var height = (uint)Math.Clamp(DesktopSurface.ActualHeight * dpi.DpiScaleY, 200, 8192);
            _rdp.Client.UpdateSessionDisplaySettings(width, height, width, height, 0u, 100u, 100u);
        }
        catch (COMException) { /* Older servers retain their size; SmartSizing still fits the viewport. */ }
    }
    private void Fullscreen_Click(object sender, RoutedEventArgs e) { if (_vm is not null) _vm.IsFullscreen = !_vm.IsFullscreen; }
    private async void Paste_Click(object sender, RoutedEventArgs e)
    {
        await InputAsync(async client => { if (Clipboard.ContainsText()) await client.SendClipboardAsync(Clipboard.GetText()); });
    }
    private async Task InputAsync(Func<VncClient, Task> action)
    {
        if (_vnc is not { } client || _vm?.IsConnected != true) return;
        try { await action(client); }
        catch (Exception ex) when (ex is System.IO.IOException or System.Net.Sockets.SocketException or ObjectDisposedException or OperationCanceledException or NotSupportedException or ArgumentException or ExternalException)
        { if (_vm?.IsConnected == true) _vm.SetState("已连接", ex.Message); }
    }
    private (int X, int Y) Pointer(MouseEventArgs e)
    {
        if (_image is null || _bitmap is null) return (0, 0);
        var point = e.GetPosition(_image);
        var scale = Math.Min(_image.ActualWidth / _bitmap.PixelWidth, _image.ActualHeight / _bitmap.PixelHeight);
        if (scale <= 0) return (0, 0);
        return ((int)((point.X - (_image.ActualWidth - _bitmap.PixelWidth * scale) / 2) / scale),
            (int)((point.Y - (_image.ActualHeight - _bitmap.PixelHeight * scale) / 2) / scale));
    }
    private async void VncMouseDown(object sender, MouseButtonEventArgs e)
    {
        _image?.Focus(); _image?.CaptureMouse();
        _buttons |= ButtonMask(e.ChangedButton); e.Handled = true;
        var p = Pointer(e); var buttons = _buttons; await InputAsync(c => c.SendPointerAsync(p.X, p.Y, buttons));
    }
    private async void VncMouseUp(object sender, MouseButtonEventArgs e)
    {
        _buttons &= (byte)~ButtonMask(e.ChangedButton); if (_buttons == 0) _image?.ReleaseMouseCapture();
        e.Handled = true; var p = Pointer(e); var buttons = _buttons; await InputAsync(c => c.SendPointerAsync(p.X, p.Y, buttons));
    }
    private async void VncMouseMove(object sender, MouseEventArgs e) { var p = Pointer(e); var buttons = _buttons; await InputAsync(c => c.SendPointerAsync(p.X, p.Y, buttons)); }
    private async void VncMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true; var p = Pointer(e); var buttons = _buttons;
        await InputAsync(async c => { await c.SendPointerAsync(p.X, p.Y, (byte)(buttons | (e.Delta > 0 ? 8 : 16))); await c.SendPointerAsync(p.X, p.Y, buttons); });
    }
    private static byte ButtonMask(MouseButton button) => button switch { MouseButton.Left => 1, MouseButton.Middle => 2, MouseButton.Right => 4, _ => 0 };
    private static uint SpecialKey(Key key) => key switch
    {
        Key.Back => 0xff08, Key.Tab => 0xff09, Key.Return => 0xff0d, Key.Escape => 0xff1b,
        Key.Home => 0xff50, Key.Left => 0xff51, Key.Up => 0xff52, Key.Right => 0xff53, Key.Down => 0xff54,
        Key.PageUp => 0xff55, Key.PageDown => 0xff56, Key.End => 0xff57, Key.Insert => 0xff63, Key.Delete => 0xffff,
        Key.LeftShift => 0xffe1, Key.RightShift => 0xffe2, Key.LeftCtrl => 0xffe3, Key.RightCtrl => 0xffe4,
        Key.LeftAlt => 0xffe9, Key.RightAlt => 0xffea, Key.LWin => 0xffeb, Key.RWin => 0xffec,
        Key.CapsLock => 0xffe5, Key.NumLock => 0xff7f,
        >= Key.F1 and <= Key.F24 => (uint)(0xffbe + key - Key.F1), _ => 0
    };
    private async void VncKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.F11) return;
        var symbol = SpecialKey(key);
        if (symbol == 0 && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0)
            symbol = key is >= Key.A and <= Key.Z ? (uint)('a' + key - Key.A) : key is >= Key.D0 and <= Key.D9 ? (uint)('0' + key - Key.D0) : 0;
        if (symbol == 0) return;
        e.Handled = true; _pressedKeys.Add(symbol); await InputAsync(c => c.SendKeyAsync(symbol, true));
    }
    private async void VncKeyUp(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var symbol = SpecialKey(key);
        if (symbol == 0) symbol = key is >= Key.A and <= Key.Z ? (uint)('a' + key - Key.A) : key is >= Key.D0 and <= Key.D9 ? (uint)('0' + key - Key.D0) : 0;
        if (!_pressedKeys.Remove(symbol)) return;
        e.Handled = true; await InputAsync(c => c.SendKeyAsync(symbol, false));
    }
    private async void VncTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = true;
        foreach (var rune in e.Text.EnumerateRunes())
        {
            var symbol = rune.Value <= 255 ? (uint)rune.Value : (uint)(0x01000000 | rune.Value);
            await InputAsync(async c => { await c.SendKeyAsync(symbol, true); await c.SendKeyAsync(symbol, false); });
        }
    }
    private async void VncLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var keys = _pressedKeys.ToArray(); _pressedKeys.Clear(); _buttons = 0;
        await InputAsync(async c => { foreach (var key in keys) await c.SendKeyAsync(key, false); await c.SendPointerAsync(0, 0, 0); });
    }
}
