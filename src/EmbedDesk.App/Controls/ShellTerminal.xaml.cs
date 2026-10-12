using System.Collections.Specialized;
using System.IO;
using System.Buffers;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EmbedDesk.App.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace EmbedDesk.App.Controls;

public partial class ShellTerminal : UserControl, IDisposable
{
    private bool _ready;
    private bool _initializing;
    private bool _navigationPending;
    private bool _disposed;
    private Window? _ownerWindow;
    private TerminalBufferViewModel? _buffer;
    private readonly Queue<byte[]> _pendingWrites = new();
    private readonly DispatcherTimer _flushTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(16) };
    private int _writeOffset;
    private bool _writing, _resetPending;

    public static readonly DependencyProperty BufferProperty = DependencyProperty.Register(nameof(Buffer), typeof(TerminalBufferViewModel), typeof(ShellTerminal), new PropertyMetadata(null, OnBufferChanged));
    public TerminalBufferViewModel? Buffer { get => (TerminalBufferViewModel?)GetValue(BufferProperty); set => SetValue(BufferProperty, value); }
    public static readonly DependencyProperty IsOutputVisibleProperty = DependencyProperty.Register(nameof(IsOutputVisible), typeof(bool), typeof(ShellTerminal),
        new PropertyMetadata(true, (sender, args) => ((ShellTerminal)sender).Browser.Visibility = (bool)args.NewValue ? Visibility.Visible : Visibility.Hidden));
    public bool IsOutputVisible { get => (bool)GetValue(IsOutputVisibleProperty); set => SetValue(IsOutputVisibleProperty, value); }
    public static readonly DependencyProperty IsFullscreenProperty = DependencyProperty.Register(nameof(IsFullscreen), typeof(bool), typeof(ShellTerminal),
        new PropertyMetadata(false, (sender, _) => ((ShellTerminal)sender).UpdateFullscreen()));
    public bool IsFullscreen { get => (bool)GetValue(IsFullscreenProperty); set => SetValue(IsFullscreenProperty, value); }
    public event EventHandler<string>? InputReceived;
    public event EventHandler<TerminalSize>? ResizeReceived;
    public event EventHandler? FullscreenRequested;

    public ShellTerminal()
    {
        InitializeComponent();
        _flushTimer.Tick += async (_, _) => { _flushTimer.Stop(); await FlushWritesAsync(); };
        Loaded += InitializeAsync;
        Browser.SizeChanged += async (_, _) => await UpdateViewportAsync();
        Browser.PreviewKeyDown += (_, e) =>
        {
            // WebView2 forwards native accelerator keys to WPF before the DOM.
            // Handle F11 once here; the page handler covers DOM-generated keys.
            if (e.Key != Key.F11) return;
            e.Handled = true;
            if (!e.IsRepeat)
                Dispatcher.BeginInvoke(new Action(() => { if (!_disposed) FullscreenRequested?.Invoke(this, EventArgs.Empty); }));
        };
        IsVisibleChanged += (_, _) => { if (IsVisible) InitializeAsync(this, new RoutedEventArgs()); };
    }

    private async void InitializeAsync(object sender, RoutedEventArgs e)
    {
        if (_disposed || _ready || _initializing || _navigationPending || !IsVisible) return;
        if (_ownerWindow is null && Window.GetWindow(this) is { } owner)
        {
            _ownerWindow = owner;
            owner.Closed += OwnerWindow_Closed;
        }
        _initializing = true;
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, AppDataPaths.TerminalDirectory);
            if (_disposed) return;
            await Browser.EnsureCoreWebView2Async(environment);
            if (_disposed) return;
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            // Ctrl +/- belongs to xterm. Browser zoom changes CSS/DIP geometry
            // independently and leaves the native PTY with a different viewport.
            Browser.CoreWebView2.Settings.IsZoomControlEnabled = false;
            Browser.CoreWebView2.Settings.IsPinchZoomEnabled = false;
            Browser.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
            Browser.ZoomFactor = 1;
            Browser.CoreWebView2.SetVirtualHostNameToFolderMapping("terminal.local", TerminalAssets.DirectoryPath, CoreWebView2HostResourceAccessKind.DenyCors);
            Browser.CoreWebView2.WebMessageReceived += (_, args) =>
            {
                var message = args.TryGetWebMessageAsString();
                try
                {
                    var node = JsonNode.Parse(message);
                    if (node is JsonObject obj && obj["type"]?.GetValue<string>() == "resize")
                    {
                        var size = new TerminalSize(node["cols"]?.GetValue<int>() ?? 120, node["rows"]?.GetValue<int>() ?? 40);
                        ResizeReceived?.Invoke(this, size);
                        if (DataContext is LocalShellViewModel localShell) _ = localShell.ResizeAsync(size.Columns, size.Rows);
                        return;
                    }
                    if (node is JsonObject context && context["type"]?.GetValue<string>() == "context")
                    {
                        HandleContextCommand(context["action"]?.GetValue<string>(), context["text"]?.GetValue<string>());
                        return;
                    }
                }
                catch (JsonException) { }
                catch (InvalidOperationException) { }
                InputReceived?.Invoke(this, message);
            };
            Browser.NavigationCompleted += async (_, args) => { _navigationPending = false; if (!_disposed && args.IsSuccess) { _ready = true; UpdateFullscreen(); await UpdateViewportAsync(); await ReplayAsync(); } };
            // A tab can be hidden and restored between Source assignment and NavigationCompleted.
            // Keep initialization reserved until navigation ends so restoring never reloads a live TUI.
            _navigationPending = true;
            Browser.Source = new Uri("https://terminal.local/index.html");
        }
        catch (Exception ex)
        {
            _navigationPending = false;
            if (_disposed) return;
            ErrorText.Text = $"终端初始化失败：{ex.Message}";
            ErrorText.Visibility = Visibility.Visible;
        }
        finally { _initializing = false; }
    }

    private async Task UpdateViewportAsync()
    {
        if (!_ready || _disposed || Browser.ActualWidth <= 0 || Browser.ActualHeight <= 0) return;
        try
        {
            // A hidden WebView can retain a default native viewport until shown.
            // WPF layout is available earlier, so use it for the initial SSH PTY.
            await Browser.ExecuteScriptAsync($"window.setTerminalViewport({JsonSerializer.Serialize(Browser.ActualWidth)}, {JsonSerializer.Serialize(Browser.ActualHeight)})");
        }
        catch (ObjectDisposedException) { }
    }

    private async void UpdateFullscreen()
    {
        if (!_ready || _disposed) return;
        try
        {
            await Browser.ExecuteScriptAsync($"window.setTerminalFullscreen({(IsFullscreen ? "true" : "false")})");
            if (!_disposed && IsVisible && IsOutputVisible) Browser.Focus();
        }
        catch (ObjectDisposedException) { }
    }

    private static void OnBufferChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (ShellTerminal)sender;
        if (control._buffer is not null) control._buffer.Entries.CollectionChanged -= control.OnEntriesChanged;
        control._buffer = args.NewValue as TerminalBufferViewModel;
        if (control._buffer is not null) control._buffer.Entries.CollectionChanged += control.OnEntriesChanged;
        _ = control.ReplayAsync();
    }

    private Task ReplayAsync()
    {
        if (!_ready || _disposed) return Task.CompletedTask;
        // Reset and replay share one queue with live writes, preserving stream order.
        _pendingWrites.Clear(); _writeOffset = 0; _resetPending = true;
        if (_buffer is not null)
            foreach (var entry in _buffer.Entries) if (entry.IsReceived) _pendingWrites.Enqueue(entry.Data);
        return FlushWritesAsync();
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (!_ready || _disposed) return;
        if (args.Action == NotifyCollectionChangedAction.Reset) { _ = ReplayAsync(); return; }
        if (args.NewItems is null) return;
        foreach (TrafficEntryViewModel entry in args.NewItems) if (entry.IsReceived && entry.Data.Length > 0) _pendingWrites.Enqueue(entry.Data);
        if (!_writing && !_flushTimer.IsEnabled) _flushTimer.Start();
    }

    private async Task FlushWritesAsync()
    {
        if (_writing || !_ready || _disposed) return;
        _writing = true;
        var batch = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (!_disposed && (_resetPending || _pendingWrites.Count > 0))
            {
                if (_resetPending)
                {
                    _resetPending = false;
                    await Browser.ExecuteScriptAsync("window.resetTerminal()");
                    continue;
                }
                var count = 0;
                while (count < 64 * 1024 && _pendingWrites.TryPeek(out var data))
                {
                    var copy = Math.Min(data.Length - _writeOffset, 64 * 1024 - count);
                    data.AsSpan(_writeOffset, copy).CopyTo(batch.AsSpan(count));
                    count += copy; _writeOffset += copy;
                    if (_writeOffset == data.Length) { _pendingWrites.Dequeue(); _writeOffset = 0; }
                }
                if (count > 0)
                    await Browser.ExecuteScriptAsync($"window.writeTerminal({JsonSerializer.Serialize(Convert.ToBase64String(batch, 0, count))})");
            }
        }
        catch (ObjectDisposedException) { }
        finally { ArrayPool<byte>.Shared.Return(batch); _writing = false; }
    }
    private void OwnerWindow_Closed(object? sender, EventArgs e) => Dispose();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ready = false;
        _flushTimer.Stop(); _pendingWrites.Clear();
        if (_buffer is not null) _buffer.Entries.CollectionChanged -= OnEntriesChanged;
        if (_ownerWindow is not null) _ownerWindow.Closed -= OwnerWindow_Closed;
        Browser.Dispose();
    }

    private void HandleContextCommand(string? action, string? selectedText)
    {
        try
        {
            switch (action)
            {
                case "copy" when !string.IsNullOrEmpty(selectedText):
                    Clipboard.SetText(selectedText);
                    break;
                case "paste":
                    var text = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
                    if (!string.IsNullOrEmpty(text)) InputReceived?.Invoke(this, text);
                    break;
                case "selectAll":
                    _ = Browser.ExecuteScriptAsync("window.selectAllTerminal()");
                    break;
                case "clear":
                    _buffer?.Clear();
                    break;
                case "fullscreen":
                    FullscreenRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        catch (ExternalException)
        {
            // Clipboard providers can briefly reject access while another process
            // owns the clipboard. The terminal remains usable in that case.
        }
    }
}

public sealed record TerminalSize(int Columns, int Rows);
