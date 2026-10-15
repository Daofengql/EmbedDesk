using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using EmbedDesk.Core.Models;

namespace EmbedDesk.App.Controls;

public sealed record PidSample(double Seconds, int Target, double Actual);
public sealed record PidSeries(int Channel, PidSample[] Samples);

public sealed class PidPlot : FrameworkElement
{
    private static readonly Brush[] Colors = [new SolidColorBrush(Color.FromRgb(76, 201, 176)), new SolidColorBrush(Color.FromRgb(229, 199, 119)), new SolidColorBrush(Color.FromRgb(232, 136, 122)), new SolidColorBrush(Color.FromRgb(107, 183, 220)), new SolidColorBrush(Color.FromRgb(188, 148, 217))];
    private static readonly double[] TimeWindows = [.05, .1, .25, .5, 1, 2, 5, 10, 30, 60, 120, 600];
    private sealed record PlotPoint(Point Position, int Channel, PidSample Raw, double Filtered);
    private readonly List<PlotPoint> _points = [];
    private Point? _pointer;
    public event Action<double>? TimeWindowChanged;
    public event Action<string>? InspectionChanged;
    public IReadOnlyList<PidSeries> Series { get; set; } = [];
    public bool ShowTarget { get; set; } = true;
    public bool ShowPoints { get; set; }
    public double WindowSeconds { get; set; } = 10;
    public SignalSmoothingMode SmoothingMode { get; set; }
    public int SmoothingWindow { get; set; } = 5;
    public double SmoothingAlpha { get; set; } = .3;

    public PidPlot() { ClipToBounds = true; MouseWheel += Zoom; }
    private void Zoom(object sender, MouseWheelEventArgs e)
    {
        WindowSeconds = e.Delta > 0 ? TimeWindows.LastOrDefault(value => value < WindowSeconds - .00001, .05) : TimeWindows.FirstOrDefault(value => value > WindowSeconds + .00001, 600);
        TimeWindowChanged?.Invoke(WindowSeconds); InvalidateVisual(); e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); _pointer = e.GetPosition(this); InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e); _pointer = null; InspectionChanged?.Invoke("移动鼠标查看时间、原始值与显示值 · 滚轮缩放"); InvalidateVisual();
    }

    internal string InspectAt(Point point)
    {
        var nearest = _points.MinBy(item => (item.Position - point).LengthSquared);
        return nearest is null ? "等待采样" : FormattableString.Invariant($"CH{nearest.Channel + 1} · {nearest.Raw.Seconds:F6}s · 原始 {nearest.Raw.Actual:G10} · 显示 {nearest.Filtered:G10} · 目标 {nearest.Raw.Target}");
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x26, 0x2C)), null, new Rect(RenderSize));
        var area = new Rect(65, 16, Math.Max(1, ActualWidth - 81), Math.Max(1, ActualHeight - 52));
        var end = Math.Max(WindowSeconds, Series.SelectMany(s => s.Samples).Select(p => p.Seconds).DefaultIfEmpty().Max());
        var start = end - WindowSeconds;
        var displayed = Series.Select(series => new PidSeries(series.Channel, ApplySmoothing(series.Samples, SmoothingMode, SmoothingWindow, SmoothingAlpha))).ToArray();
        var samples = displayed.SelectMany(s => s.Samples).Where(p => p.Seconds >= start && p.Seconds <= end).ToArray();
        var min = samples.Length == 0 ? 0d : samples.Min(p => ShowTarget ? Math.Min(p.Target, p.Actual) : p.Actual);
        var max = samples.Length == 0 ? 1d : samples.Max(p => ShowTarget ? Math.Max(p.Target, p.Actual) : p.Actual);
        var pad = Math.Max(1, (max - min) * .08); min -= pad; max += pad;
        var grid = new Pen(new SolidColorBrush(Color.FromRgb(0x34, 0x3C, 0x44)), 1);
        for (var i = 0; i <= 10; i++)
        {
            var x = area.Left + area.Width * i / 10; var y = area.Top + area.Height * i / 10;
            dc.DrawLine(grid, new(x, area.Top), new(x, area.Bottom)); dc.DrawLine(grid, new(area.Left, y), new(area.Right, y));
            if (i % 2 == 0)
            {
                Label(dc, (max - (max - min) * i / 10).ToString("G5", CultureInfo.InvariantCulture), new(3, y - 7));
                var value = start + WindowSeconds * i / 10; var format = WindowSeconds < 1 ? "F3" : WindowSeconds < 10 ? "F2" : "F1";
                Label(dc, value.ToString(format, CultureInfo.InvariantCulture) + "s", new(Math.Min(x, area.Right - 42), area.Bottom + 8));
            }
        }
        _points.Clear(); dc.PushClip(new RectangleGeometry(area));
        for (var channelIndex = 0; channelIndex < displayed.Length; channelIndex++)
        {
            var series = displayed[channelIndex]; var ordered = series.Samples;
            DrawSeries(false); if (ShowTarget) DrawSeries(true);

            void DrawSeries(bool target)
            {
                var geometry = new StreamGeometry();
                using (var path = geometry.Open())
                {
                    var first = true;
                    for (var index = 0; index < ordered.Length; index++)
                    {
                        var sample = ordered[index]; if (sample.Seconds < start || sample.Seconds > end) continue;
                        var number = target ? sample.Target : sample.Actual;
                        var point = new Point(area.Left + (sample.Seconds - start) / WindowSeconds * area.Width, area.Bottom - (number - min) / (max - min) * area.Height);
                        if (!target) _points.Add(new(point, series.Channel, Series[channelIndex].Samples[index], sample.Actual));
                        if (first) { path.BeginFigure(point, false, false); first = false; } else path.LineTo(point, true, false);
                        if (!target && ShowPoints) dc.DrawEllipse(Colors[series.Channel], null, point, 2.2, 2.2);
                    }
                }
                geometry.Freeze(); var pen = new Pen(Colors[series.Channel], target ? 1 : 1.6); if (target) pen.DashStyle = DashStyles.Dash;
                dc.DrawGeometry(null, pen, geometry);
            }
        }
        if (_pointer is { } pointer && area.Contains(pointer) && _points.Count != 0)
        {
            var nearest = _points.MinBy(item => (item.Position - pointer).LengthSquared)!;
            dc.DrawLine(new Pen(Brushes.SlateGray, 1) { DashStyle = DashStyles.Dot }, new(nearest.Position.X, area.Top), new(nearest.Position.X, area.Bottom));
            dc.DrawEllipse(Colors[nearest.Channel], new Pen(Brushes.White, 1), nearest.Position, 4, 4);
            InspectionChanged?.Invoke(InspectAt(pointer));
        }
        dc.Pop();
        if (samples.Length == 0) Label(dc, "等待开发板上报实际值 · 滚轮缩放时间轴", new(area.Left + 16, area.Top + 18));
    }

    private void Label(DrawingContext dc, string text, Point origin) => dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture,
        FlowDirection.LeftToRight, new Typeface("Consolas"), 11, Brushes.LightSlateGray, VisualTreeHelper.GetDpi(this).PixelsPerDip), origin);

    public static PidSample[] ApplySmoothing(IEnumerable<PidSample> source, SignalSmoothingMode mode, int window, double alpha = .3)
    {
        // Receive queues and export snapshots are already in timestamp order.
        var samples = source.ToArray(); var values = SignalSmoothing.Apply(samples.Select(p => p.Actual).ToArray(), mode, window, alpha);
        return samples.Select((sample, index) => sample with { Actual = values[index] }).ToArray();
    }
}
