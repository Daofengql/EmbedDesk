using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EmbeddedDebugger.App.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace EmbeddedDebugger.App.Controls;

public partial class ShellTerminal : UserControl, IDisposable
{
    private bool _ready;
    private bool _initializing;
    private bool _disposed;
    private Window? _ownerWindow;
    private TerminalBufferViewModel? _buffer;
    private int _renderGeneration;
    private readonly SemaphoreSlim _renderLock = new(1, 1);

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
        if (_disposed || _ready || _initializing || !IsVisible) return;
        if (_ownerWindow is null && Window.GetWindow(this) is { } owner)
        {
            _ownerWindow = owner;
            owner.Closed += OwnerWindow_Closed;
        }
        _initializing = true;
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EmbeddedDebugger", "Terminal"));
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
            Browser.NavigationCompleted += async (_, args) => { if (!_disposed && args.IsSuccess) { _ready = true; UpdateFullscreen(); await UpdateViewportAsync(); await ReplayAsync(); } };
            Browser.Source = new Uri("https://terminal.local/index.html");
        }
        catch (Exception ex)
        {
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

    private async Task ReplayAsync()
    {
        if (!_ready || _disposed) return;
        // Snapshot before yielding: later collection additions are queued only once.
        var generation = ++_renderGeneration;
        var entries = _buffer?.Entries.ToArray() ?? [];
        await _renderLock.WaitAsync();
        try
        {
            if (_disposed || generation != _renderGeneration) return;
            await Browser.ExecuteScriptAsync("window.resetTerminal()");
            foreach (var entry in entries)
            {
                if (_disposed || generation != _renderGeneration) return;
                if (entry.IsReceived) await WriteAsync(entry.Data);
            }
        }
        catch (ObjectDisposedException) { }
        finally { _renderLock.Release(); }
    }

    private async void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (!_ready || _disposed) return;
        if (args.Action == NotifyCollectionChangedAction.Reset) { await ReplayAsync(); return; }
        if (args.NewItems is null) return;
        var generation = _renderGeneration;
        await _renderLock.WaitAsync();
        try
        {
            if (_disposed || generation != _renderGeneration) return;
            foreach (TrafficEntryViewModel entry in args.NewItems)
            {
                if (_disposed || generation != _renderGeneration) return;
                if (entry.IsReceived) await WriteAsync(entry.Data);
            }
        }
        catch (ObjectDisposedException) { }
        finally { _renderLock.Release(); }
    }

    private Task<string> WriteAsync(byte[] bytes) => _disposed ? Task.FromResult(string.Empty) : Browser.ExecuteScriptAsync($"window.writeTerminal({JsonSerializer.Serialize(Convert.ToBase64String(bytes))})");

    private void OwnerWindow_Closed(object? sender, EventArgs e) => Dispose();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ready = false;
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
