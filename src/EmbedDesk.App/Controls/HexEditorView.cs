using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace EmbedDesk.App.Controls;

public sealed class HexEditorView : FrameworkElement
{
    private const double HeaderHeight = 32, RowHeight = 23, HexStart = 132;
    private static readonly Typeface Typeface = new("Consolas");
    private static readonly Brush BackgroundBrush = new SolidColorBrush(Color.FromRgb(0x20, 0x26, 0x2C));
    private static readonly Brush HeaderBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x2B, 0x31));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xED, 0xF0));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0xA5, 0xAF));
    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xC9, 0xB0));
    private static readonly Brush SelectionBrush = new SolidColorBrush(Color.FromRgb(0x14, 0x3B, 0x3C));
    private HashSet<long> _modified = [];
    private double _charWidth = 8;
    private bool _lowNibble, _ascii;
    public byte[] Bytes { get; private set; } = [];
    public long Offset { get; private set; }
    public long FileLength { get; private set; }
    public long Selection { get; private set; }
    public int VisibleRows => Math.Clamp((int)((ActualHeight - HeaderHeight) / RowHeight), 1, 256);
    public event Action<long>? SelectionChanged;
    public event Action<long>? ScrollRequested;
    public event Action<long, byte>? ByteEdited;
    public event Action? UndoRequested;
    public event Action? RedoRequested;
    public event Action? FindRequested;
    public event Action? SaveRequested;

    public HexEditorView() { Focusable = true; FocusVisualStyle = null; ClipToBounds = true; Cursor = Cursors.IBeam; }
    private double CellX(int column) => HexStart + column * 3 * _charWidth + (column >= 8 ? _charWidth : 0);
    private double AsciiStart => CellX(16) + 28;

    public void SetData(long offset, byte[] bytes, long length, long[] modified)
    {
        Offset = offset; Bytes = bytes; FileLength = length; _modified = modified.ToHashSet(); InvalidateVisual();
    }
    public void SetSelection(long offset, bool notify = true)
    {
        Selection = Math.Clamp(offset, 0, Math.Max(0, FileLength - 1)); _lowNibble = false;
        if (notify) SelectionChanged?.Invoke(Selection);
        InvalidateVisual();
    }
    public void MarkEdited(long offset, byte value, bool modified)
    {
        if (offset >= Offset && offset - Offset < Bytes.Length) Bytes[(int)(offset - Offset)] = value;
        if (modified) _modified.Add(offset); else _modified.Remove(offset); InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext); var dc = drawingContext;
        _charWidth = Text("0", TextBrush).WidthIncludingTrailingWhitespace;
        dc.DrawRectangle(BackgroundBrush, null, new Rect(RenderSize)); dc.DrawRectangle(HeaderBrush, null, new Rect(0, 0, ActualWidth, HeaderHeight));
        Draw("偏移 (HEX)", 14, 8, MutedBrush);
        for (var column = 0; column < 16; column++) Draw(column.ToString("X2", CultureInfo.InvariantCulture), CellX(column), 8, MutedBrush);
        Draw("ASCII", AsciiStart, 8, MutedBrush);
        var line = new Pen(new SolidColorBrush(Color.FromRgb(0x34, 0x3C, 0x44)), 1);
        dc.DrawLine(line, new(HexStart - 18, 0), new(HexStart - 18, ActualHeight));
        dc.DrawLine(line, new(AsciiStart - 15, 0), new(AsciiStart - 15, ActualHeight));
        for (var row = 0; row * 16 < Bytes.Length && row < VisibleRows; row++)
        {
            var y = HeaderHeight + row * RowHeight; var start = row * 16; var count = Math.Min(16, Bytes.Length - start);
            var selected = Selection >= Offset + start && Selection < Offset + start + count;
            if (selected) dc.DrawRectangle(HeaderBrush, null, new Rect(0, y, ActualWidth, RowHeight));
            Draw((Offset + start).ToString("X12", CultureInfo.InvariantCulture), 14, y + 4, MutedBrush);
            for (var half = 0; half < 2 && half * 8 < count; half++)
            {
                var span = Bytes.AsSpan(start + half * 8, Math.Min(8, count - half * 8));
                Draw(string.Join(" ", span.ToArray().Select(b => b.ToString("X2", CultureInfo.InvariantCulture))), CellX(half * 8), y + 4, TextBrush);
            }
            Draw(new string(Bytes.AsSpan(start, count).ToArray().Select(b => b is >= 32 and <= 126 ? (char)b : '.').ToArray()), AsciiStart, y + 4, MutedBrush);
            for (var column = 0; column < count; column++)
            {
                var address = Offset + start + column;
                if (_modified.Contains(address)) Draw(Bytes[start + column].ToString("X2", CultureInfo.InvariantCulture), CellX(column), y + 4, AccentBrush);
                if (address != Selection) continue;
                var hexRect = new Rect(CellX(column) - 3, y + 1, _charWidth * 2 + 6, RowHeight - 2);
                dc.DrawRoundedRectangle(SelectionBrush, IsKeyboardFocused && !_ascii ? new Pen(AccentBrush, 1) : null, hexRect, 2, 2);
                Draw(Bytes[start + column].ToString("X2", CultureInfo.InvariantCulture), CellX(column), y + 4, AccentBrush);
                dc.DrawRectangle(SelectionBrush, null, new Rect(AsciiStart + column * _charWidth - 1, y + 1, _charWidth + 2, RowHeight - 2));
                Draw(Bytes[start + column] is >= 32 and <= 126 ? ((char)Bytes[start + column]).ToString() : ".", AsciiStart + column * _charWidth, y + 4, AccentBrush);
                if (IsKeyboardFocused && !_ascii) dc.DrawLine(new Pen(AccentBrush, 1), new(CellX(column) + (_lowNibble ? _charWidth : 0), y + RowHeight - 3), new(CellX(column) + (_lowNibble ? 2 : 1) * _charWidth, y + RowHeight - 3));
            }
        }
        if (FileLength == 0) Draw("打开文件后，点选字节并直接输入 HEX", HexStart, HeaderHeight + 18, MutedBrush);
        void Draw(string text, double x, double y, Brush brush) => dc.DrawText(Text(text, brush), new Point(x, y));
    }
    private FormattedText Text(string value, Brush brush) => new(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface, 13, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e); Focus(); var point = e.GetPosition(this); var row = (int)((point.Y - HeaderHeight) / RowHeight);
        if (point.Y < HeaderHeight || row >= VisibleRows) return;
        _ascii = point.X >= AsciiStart - 3;
        var column = _ascii ? (int)Math.Floor((point.X - AsciiStart) / _charWidth) : Enumerable.Range(0, 16).OrderBy(c => Math.Abs(point.X - CellX(c) - _charWidth)).First();
        if (column is >= 0 and < 16 && Offset + row * 16 + column < FileLength) SetSelection(Offset + row * 16 + column);
        e.Handled = true;
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e) { ScrollRequested?.Invoke(Offset + (e.Delta > 0 ? -48 : 48)); e.Handled = true; }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (control && e.Key == Key.Z) UndoRequested?.Invoke();
        else if (control && e.Key == Key.Y) RedoRequested?.Invoke();
        else if (control && e.Key == Key.F) FindRequested?.Invoke();
        else if (control && e.Key == Key.S) SaveRequested?.Invoke();
        else if (e.Key == Key.Tab) { _ascii = !_ascii; _lowNibble = false; InvalidateVisual(); }
        else
        {
            var next = e.Key switch { Key.Left or Key.Back => Selection - 1, Key.Right => Selection + 1, Key.Up => Selection - 16, Key.Down => Selection + 16,
                Key.PageUp => Selection - VisibleRows * 16, Key.PageDown => Selection + VisibleRows * 16, Key.Home => control ? 0 : Selection / 16 * 16,
                Key.End => control ? FileLength - 1 : Selection / 16 * 16 + 15, _ => Selection };
            if (next == Selection) { base.OnKeyDown(e); return; } SetSelection(next);
        }
        e.Handled = true;
    }
    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        if (Selection < Offset || Selection - Offset >= Bytes.Length) return;
        foreach (var character in e.Text)
        {
            var index = Selection - Offset; if (index < 0 || index >= Bytes.Length) break;
            if (_ascii)
            {
                if (character is < ' ' or > '~') continue;
                ByteEdited?.Invoke(Selection, (byte)character); SetSelection(Selection + 1);
            }
            else
            {
                var digit = character is >= '0' and <= '9' ? character - '0' : char.ToUpperInvariant(character) is >= 'A' and <= 'F' ? char.ToUpperInvariant(character) - 'A' + 10 : -1;
                if (digit < 0) continue;
                var value = Bytes[(int)index]; ByteEdited?.Invoke(Selection, (byte)(_lowNibble ? (value & 0xF0) | digit : (digit << 4) | (value & 0xF)));
                if (_lowNibble) SetSelection(Selection + 1); else _lowNibble = true;
            }
        }
        InvalidateVisual(); e.Handled = true;
    }
}
