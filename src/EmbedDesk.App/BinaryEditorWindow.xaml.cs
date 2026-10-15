using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EmbedDesk.Infrastructure.Diagnostics;
using Microsoft.Win32;

namespace EmbedDesk.App;

public partial class BinaryEditorWindow : Window, IDebugToolWindow
{
    private BinaryFileDocument? _document;
    private long _offset, _selection;
    private CancellationTokenSource? _operation;
    private Task? _work;
    private bool _closing, _closed;
    private readonly DispatcherTimer _resize;
    private int _lastVisibleRows;
    public bool HasActiveWork => _operation is not null;
    public bool HasUnsavedChanges => _document?.ModifiedBytes > 0;

    public BinaryEditorWindow()
    {
        InitializeComponent(); WindowTheme.ApplyDarkTitleBar(this); Closing += OnClosing;
        HexView.SelectionChanged += SelectionChanged;
        HexView.ScrollRequested += async offset => await RunAsync(async token => { _offset = Math.Clamp(offset / 16 * 16, 0, LastOffset); await RefreshAsync(token); });
        HexView.ByteEdited += EditByte;
        HexView.UndoRequested += () => Undo_Click(this, new RoutedEventArgs());
        HexView.RedoRequested += () => Redo_Click(this, new RoutedEventArgs());
        HexView.FindRequested += () => SearchBox.Focus();
        HexView.SaveRequested += () => Save_Click(this, new RoutedEventArgs());
        _resize = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _resize.Tick += async (_, _) =>
        {
            _resize.Stop();
            if (_document is not null && HexView.VisibleRows != _lastVisibleRows)
                await RunAsync(RefreshAsync);
        };
        HexView.SizeChanged += (_, e) =>
        {
            // Width changes only repaint the same rows. Re-reading the file for them
            // creates a layout/read/layout feedback loop while a file is opened.
            if (!e.HeightChanged || HexView.VisibleRows == _lastVisibleRows) return;
            _resize.Stop(); _resize.Start();
        };
    }

    private long LastOffset => _document is null ? 0 : Math.Max(0, ((_document.Length + 15) / 16 - HexView.VisibleRows) * 16);
    private bool CanDiscard() => !HasUnsavedChanges || new ConfirmationWindow("未保存的修改", "放弃未保存的二进制修改？", "放弃修改") { Owner = this }.ShowDialog() == true;

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!CanDiscard()) return;
        var picker = new OpenFileDialog { Filter = "二进制文件|*.bin;*.dat;*.img;*.hex|所有文件|*.*" };
        if (picker.ShowDialog(this) == true) await LoadFileAsync(picker.FileName);
    }

    internal Task LoadFileAsync(string path) => RunAsync(async token =>
    {
        var next = new BinaryFileDocument(path);
        if (_document is not null) await _document.DisposeAsync();
        _document = next; _offset = _selection = 0; await RefreshAsync(token); HexView.SetSelection(0, false); HexView.Focus();
        StatusText.Text = "覆盖模式 · HEX 文件也按原始文件字节编辑，不解析 Intel HEX 记录。";
    });

    private async Task RefreshAsync(CancellationToken token)
    {
        if (_document is null) return;
        _offset = Math.Clamp(_offset, 0, LastOffset);
        var bytes = await _document.ReadAsync(_offset, HexView.VisibleRows * 16, token);
        _lastVisibleRows = HexView.VisibleRows;
        HexView.SetData(_offset, bytes, _document.Length, _document.GetModifiedOffsets(_offset, bytes.Length));
        HexView.SetSelection(_selection, false);
        FileScroll.Maximum = Math.Max(0, (_document.Length + 15) / 16 - HexView.VisibleRows);
        FileScroll.ViewportSize = HexView.VisibleRows; FileScroll.LargeChange = HexView.VisibleRows; FileScroll.Value = _offset / 16;
        FileText.Text = Path.GetFileName(_document.FilePath); FileText.ToolTip = _document.FilePath;
        UpdateMetadata(); UpdateEnabled();
    }

    private void UpdateMetadata()
    {
        if (_document is null) return;
        Title = $"{(_document.ModifiedBytes > 0 ? "* " : "")}{Path.GetFileName(_document.FilePath)} — 二进制编辑器";
        LengthText.Text = $"{_document.Length:N0} 字节 · 修改 {_document.ModifiedBytes:N0} 字节";
        var index = _selection - _offset;
        if (index < 0 || index >= HexView.Bytes.Length) { SelectionText.Text = $"偏移 0x{_selection:X}"; return; }
        var bytes = HexView.Bytes.AsSpan((int)index);
        SelectionText.Text = $"偏移 0x{_selection:X8}  |  Byte {bytes[0]:X2} / {bytes[0]}"
            + (bytes.Length >= 2 ? $"  |  UInt16 LE {BinaryPrimitives.ReadUInt16LittleEndian(bytes)}" : "")
            + (bytes.Length >= 4 ? $"  |  Int32 LE {BinaryPrimitives.ReadInt32LittleEndian(bytes)}" : "");
    }

    private async void SelectionChanged(long offset)
    {
        _selection = offset;
        if (offset < _offset || offset >= _offset + HexView.VisibleRows * 16)
        {
            await RunAsync(async token =>
            {
                _offset = offset < _offset ? offset / 16 * 16 : (offset / 16 - HexView.VisibleRows + 1) * 16;
                await RefreshAsync(token);
            });
        }
        else UpdateMetadata();
    }

    private void EditByte(long offset, byte value)
    {
        if (_document is null || _operation is not null || _closing) return;
        try
        {
            _document.ReplaceCachedByte(offset, value);
            HexView.MarkEdited(offset, value, _document.GetModifiedOffsets(offset, 1).Length > 0);
            UpdateMetadata(); UpdateEnabled(); StatusText.Text = "覆盖修改已暂存 · Ctrl+S 保存，Ctrl+Z 撤销。";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { StatusText.Text = ex.Message; }
    }

    private async void Scroll_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_document is null || _operation is not null || _closing) return;
        await RunAsync(async token => { _offset = (long)FileScroll.Value * 16; await RefreshAsync(token); });
    }

    private static long ParseOffset(string text) => long.Parse(text.Replace("0x", "", StringComparison.OrdinalIgnoreCase).Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    private async void Goto_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        if (_document is null) return;
        var address = ParseOffset(GotoBox.Text);
        if (address < 0 || address >= _document.Length) throw new FormatException("地址不在文件范围内。");
        _selection = address; _offset = address / 16 * 16; await RefreshAsync(token); HexView.Focus(); StatusText.Text = $"已定位 0x{address:X}。";
    });

    private async void Undo_Click(object sender, RoutedEventArgs e) => await RunAsync(async token => { _document?.Undo(); await RefreshAsync(token); StatusText.Text = "已撤销。"; });
    private async void Redo_Click(object sender, RoutedEventArgs e) => await RunAsync(async token => { _document?.Redo(); await RefreshAsync(token); StatusText.Text = "已重做。"; });

    private async void Find_Click(object sender, RoutedEventArgs e)
    {
        var text = SearchBox.Text; var utf8 = SearchModeBox.SelectedIndex == 1;
        await RunAsync(async token =>
        {
            if (_document is null) return;
            var bytes = utf8 ? Encoding.UTF8.GetBytes(text) : ByteUtilities.Decode(text, ByteFormat.Hex);
            var start = Math.Min(_selection + 1, _document.Length);
            var found = await Task.Run(() => _document.FindAsync(bytes, start, token), token); var wrapped = false;
            if (found < 0 && start > 0) { found = await Task.Run(() => _document.FindAsync(bytes, 0, token), token); wrapped = true; }
            if (found < 0) { StatusText.Text = "未找到匹配字节。"; return; }
            _selection = found; _offset = found / 16 * 16; await RefreshAsync(token); HexView.Focus();
            StatusText.Text = $"找到 0x{found:X}" + (wrapped ? " · 已从文件开头继续搜索。" : "。");
        });
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null || !HasUnsavedChanges || new ConfirmationWindow("保存二进制文件", "将修改保存到原文件？完成后会替换原文件。", "保存") { Owner = this }.ShowDialog() != true) return;
        await SaveAsync(_document.FilePath);
    }
    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        var picker = new SaveFileDialog { Filter = "所有文件|*.*", FileName = Path.GetFileNameWithoutExtension(_document.FilePath) + ".edited" + Path.GetExtension(_document.FilePath) };
        if (picker.ShowDialog(this) == true) await SaveAsync(picker.FileName);
    }
    private Task SaveAsync(string path) => RunAsync(async token => { if (_document is null) return; await Task.Run(() => _document.SaveAsync(path, token), token); await RefreshAsync(token); StatusText.Text = "保存成功 · 完整写入后已替换目标文件。"; });

    private Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_operation is not null || _closing) return Task.CompletedTask;
        var cancel = _operation = new CancellationTokenSource(); UpdateEnabled(); CancelButton.Visibility = Visibility.Visible;
        return _work = ExecuteAsync(action, cancel);
    }
    private async Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationTokenSource cancel)
    {
        try { await action(cancel.Token); }
        catch (OperationCanceledException) { if (!_closing) StatusText.Text = "操作已取消，未发布未完成的保存。"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FormatException or OverflowException or NotSupportedException)
        { if (!_closing) StatusText.Text = "操作失败：" + ex.Message; }
        finally { _operation = null; cancel.Dispose(); if (!_closing) { CancelButton.Visibility = Visibility.Collapsed; UpdateEnabled(); } }
    }
    private void UpdateEnabled()
    {
        var idle = _operation is null; Toolbar.IsEnabled = FileScroll.IsEnabled = HexView.IsEnabled = idle;
        SearchPanel.IsEnabled = idle && _document is not null;
        SaveButton.IsEnabled = HasUnsavedChanges; SaveAsButton.IsEnabled = _document is not null;
        UndoButton.IsEnabled = _document?.CanUndo == true; RedoButton.IsEnabled = _document?.CanRedo == true;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closed) return; e.Cancel = true; if (_closing || !CanDiscard()) return;
        await ShutdownAsync(); _ = Dispatcher.BeginInvoke(new Action(Close));
    }
    public async Task ShutdownAsync()
    {
        if (_closed) return;
        _closing = true; _resize.Stop(); _operation?.Cancel(); if (_work is { } work) await work;
        if (_document is not null) await _document.DisposeAsync(); _closed = true;
    }
}
