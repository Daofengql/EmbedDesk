using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EmbedDesk.Core.Abstractions;
using EmbedDesk.Core.Models;
using Microsoft.Win32;

namespace EmbedDesk.App.Controls;

public partial class PidDebugView : UserControl
{
    private sealed class ChannelState
    {
        public Queue<PidSample> Samples { get; } = new();
        public int Target;
        public double Actual;
        public int Version;
        public float P = 1, I, D;
        public uint Period = 200;
        public bool? Running;
    }
    private readonly ChannelState[] _channels = Enumerable.Range(0, 5).Select(_ => new ChannelState()).ToArray();
    private readonly object _sync = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private DebugToolPacketReader _packets = new(false);
    private readonly DispatcherTimer _timer;
    private long _samples, _invalid;
    private int _displayVersion = -1;
    private bool _closing;
    private Task? _shutdown;
    private bool _validFilter = true;
    private CancellationTokenSource? _exportCancellation;
    private Task _exportTask = Task.CompletedTask;
    public bool HasActiveWork => Connection.IsActive || !_exportTask.IsCompleted;
    public string ConnectionStatus => Connection.Status;
    public bool HasUnsavedChanges => false;
    public event EventHandler? ActivityChanged;
    internal DebugToolConnectionPanel ConnectionPanel => Connection;
    internal void UseEmbeddedLayout()
    {
        Connection.UseEmbeddedLayout();
        ((Panel)Connection.Parent).Children.Remove(Connection);
        ConnectionHeading.Visibility = ConnectionDivider.Visibility = Visibility.Collapsed;
        ViewHeading.Children.Clear(); ViewHeading.Children.Add(Connection);
        ((Panel)ChannelStatus.Parent).Children.Remove(ChannelStatus);
        ViewHeading.Children.Add(ChannelStatus); Grid.SetColumn(ChannelStatus, 1);
        ChannelStatus.Margin = new Thickness(12, 0, 0, 0);
        ChannelStatus.FontSize = 11; ChannelStatus.TextAlignment = TextAlignment.Right;
        ChannelStatus.VerticalAlignment = VerticalAlignment.Center;
        CoefficientGrid.RowDefinitions.Clear();
        CoefficientGrid.RowDefinitions.Add(new() { Height = GridLength.Auto }); CoefficientGrid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        CoefficientGrid.ColumnDefinitions.Clear();
        for (var column = 0; column < 3; column++)
        {
            CoefficientGrid.ColumnDefinitions.Add(new());
            var label = CoefficientGrid.Children[column * 2]; var editor = (TextBox)CoefficientGrid.Children[column * 2 + 1];
            Grid.SetRow(label, 0); Grid.SetColumn(label, column); Grid.SetRow(editor, 1); Grid.SetColumn(editor, column);
            editor.Margin = new Thickness(0, 4, column < 2 ? 6 : 0, 0);
        }
    }

    public PidDebugView(ISerialPortService serial)
    {
        InitializeComponent(); Connection.Initialize(serial); Connection.UseCompactLayout();
        ChannelBox.ItemsSource = new[] { "CH1", "CH2", "CH3", "CH4", "CH5" }; ChannelBox.SelectedIndex = 0;
        ApplyPlotSettings();
        Plot.TimeWindowChanged += seconds => TimeWindowBox.SelectedIndex = TimeWindowBox.Items.Cast<ComboBoxItem>().ToList().FindIndex(item => double.Parse((string)item.Tag, CultureInfo.InvariantCulture) == seconds);
        Plot.InspectionChanged += text => InspectionText.Text = text;
        Connection.Starting = () => { _packets = new(false); Clear(); };
        Connection.ActivityChanged += (_, _) => { EditorPanel.IsEnabled = Connection.IsActive; ActivityChanged?.Invoke(this, EventArgs.Empty); };
        EditorPanel.IsEnabled = false;
        Connection.ReceiveAsync = bytes => { _packets.Feed(bytes.Span, Accept); return Task.CompletedTask; };
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => RefreshPlot(), Dispatcher);
    }

    private void Accept(DebugToolPacket packet)
    {
        if (packet.Address is < 1 or > 5) { Interlocked.Increment(ref _invalid); return; }
        var bytes = packet.Payload.AsSpan();
        lock (_sync)
        {
            var channel = _channels[packet.Address - 1];
            switch (packet.Command)
            {
                case 1 when bytes.Length == 4: channel.Target = BinaryPrimitives.ReadInt32LittleEndian(bytes); channel.Version++; break;
                case 2 when bytes.Length == 4:
                    channel.Actual = BinaryPrimitives.ReadInt32LittleEndian(bytes);
                    channel.Samples.Enqueue(new(_clock.Elapsed.TotalSeconds, channel.Target, channel.Actual));
                    if (channel.Samples.Count > 2000) channel.Samples.Dequeue();
                    _samples++; break;
                case 3 when bytes.Length == 12:
                    var p = BinaryPrimitives.ReadSingleLittleEndian(bytes); var i = BinaryPrimitives.ReadSingleLittleEndian(bytes[4..]); var d = BinaryPrimitives.ReadSingleLittleEndian(bytes[8..]);
                    if (!float.IsFinite(p) || !float.IsFinite(i) || !float.IsFinite(d)) { _invalid++; break; }
                    channel.P = p; channel.I = i; channel.D = d; channel.Version++; break;
                case 4 when bytes.Length == 0: channel.Running = true; channel.Version++; break;
                case 5 when bytes.Length == 0: channel.Running = false; channel.Version++; break;
                case 6 when bytes.Length == 4: channel.Period = BinaryPrimitives.ReadUInt32LittleEndian(bytes); channel.Version++; break;
                default: _invalid++; break;
            }
        }
    }

    private void RefreshPlot()
    {
        if (!IsVisible) return;
        lock (_sync)
        {
            var channel = _channels[Math.Max(0, ChannelBox.SelectedIndex)];
            ChannelStatus.Text = $"实际值：{channel.Actual:G6}\n设备状态：{(channel.Running is true ? "运行" : channel.Running is false ? "停止" : "未同步")}";
            MetricsText.Text = $"累计采样 {_samples:N0} · 保留 {_channels.Sum(c => c.Samples.Count):N0} · 拒绝 {_invalid + _packets.RejectedPackets:N0}";
            if (_displayVersion != channel.Version && !EditorPanel.IsKeyboardFocusWithin)
            {
                PBox.Text = channel.P.ToString("G9", CultureInfo.InvariantCulture); IBox.Text = channel.I.ToString("G9", CultureInfo.InvariantCulture); DBox.Text = channel.D.ToString("G9", CultureInfo.InvariantCulture);
                TargetBox.Text = channel.Target.ToString(CultureInfo.InvariantCulture); PeriodBox.Text = channel.Period.ToString(CultureInfo.InvariantCulture); _displayVersion = channel.Version;
            }
            if (PauseBox.IsChecked != true)
            {
                Plot.Series = Legend.Children.OfType<CheckBox>().Take(5).Select((box, index) => (box, index)).Where(item => item.box.IsChecked == true)
                    .Select(item => new PidSeries(item.index, _channels[item.index].Samples.ToArray())).ToArray();
                Plot.ShowTarget = ShowTargetBox.IsChecked == true; Plot.InvalidateVisual();
            }
        }
    }

    private void Channel_Changed(object sender, SelectionChangedEventArgs e) { _displayVersion = -1; if (_timer is not null) RefreshPlot(); }

    private void PlotSettings_Changed(object? sender, RoutedEventArgs e) { if (IsInitialized) ApplyPlotSettings(); }
    private void PlotSettings_TextChanged(object sender, TextChangedEventArgs e) { if (IsLoaded) ApplyPlotSettings(); }
    private void TimeWindow_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Plot is not null && TimeWindowBox.SelectedItem is ComboBoxItem time)
        { Plot.WindowSeconds = double.Parse((string)time.Tag, CultureInfo.InvariantCulture); Plot.InvalidateVisual(); }
    }

    private void ApplyPlotSettings()
    {
        if (!IsInitialized) return;
        Plot.SmoothingMode = FilterBox.SelectedIndex switch { 1 => SignalSmoothingMode.MovingAverage, 2 => SignalSmoothingMode.Exponential, _ => SignalSmoothingMode.None };
        AverageSettings.Visibility = Plot.SmoothingMode == SignalSmoothingMode.Exponential ? Visibility.Collapsed : Visibility.Visible;
        AverageSettings.IsEnabled = Plot.SmoothingMode == SignalSmoothingMode.MovingAverage;
        ExponentialSettings.Visibility = Plot.SmoothingMode == SignalSmoothingMode.Exponential ? Visibility.Visible : Visibility.Collapsed;
        var validWindow = int.TryParse(SmoothingWindowBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var window) && window is >= 1 and <= 1000;
        var validAlpha = double.TryParse(SmoothingAlphaBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var alpha) && double.IsFinite(alpha) && alpha is > 0 and <= 1;
        _validFilter = Plot.SmoothingMode switch { SignalSmoothingMode.MovingAverage => validWindow, SignalSmoothingMode.Exponential => validAlpha, _ => true };
        SmoothingWindowBox.BorderBrush = validWindow ? (Brush)FindResource("LineBrush") : Brushes.IndianRed;
        SmoothingAlphaBox.BorderBrush = validAlpha ? (Brush)FindResource("LineBrush") : Brushes.IndianRed;
        if (validWindow) Plot.SmoothingWindow = window;
        if (validAlpha) Plot.SmoothingAlpha = alpha;
        FilterHint.Text = _validFilter ? "滤波仅影响显示，保留原始采样；平滑会带来滞后。" : "滤波参数无效：窗口 N 为 1–1000；α 必须大于 0 且不超过 1。";
        ExportButton.IsEnabled = _validFilter && _exportTask.IsCompleted;
        Plot.ShowPoints = ShowPointsBox.IsChecked == true; Plot.InvalidateVisual();
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var command = byte.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture);
            var payload = command == 16 ? new byte[12] : command is 17 or 21 ? new byte[4] : [];
            if (command == 16)
            {
                var values = new[] { PBox.Text, IBox.Text, DBox.Text }.Select(text => float.Parse(text, CultureInfo.InvariantCulture)).ToArray();
                if (values.Any(value => !float.IsFinite(value))) throw new FormatException("PID 参数必须是有限数值。");
                for (var i = 0; i < 3; i++) BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(i * 4), values[i]);
            }
            else if (command == 17) BinaryPrimitives.WriteInt32LittleEndian(payload, int.Parse(TargetBox.Text, CultureInfo.InvariantCulture));
            else if (command == 21) BinaryPrimitives.WriteUInt32LittleEndian(payload, uint.Parse(PeriodBox.Text, CultureInfo.InvariantCulture));
            await Connection.SendAsync(DebugToolPacketReader.Encode(false, (byte)(ChannelBox.SelectedIndex + 1), command, payload));
            if (!_closing) StatusText.Text = "指令已发送 · 尚未收到开发板状态同步，发送完成不代表执行成功。";
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidOperationException or IOException or OperationCanceledException or System.Net.Sockets.SocketException)
        { if (!_closing) StatusText.Text = "发送失败：" + ex.Message; }
    }

    private void Clear()
    {
        lock (_sync) { foreach (var channel in _channels) channel.Samples.Clear(); _samples = _invalid = 0; _clock.Restart(); }
        Plot.Series = []; Plot.InvalidateVisual();
    }
    private void Clear_Click(object sender, RoutedEventArgs e) { Clear(); StatusText.Text = "曲线已清空，继续接收。"; }
    private void ResetZoom_Click(object sender, RoutedEventArgs e) { TimeWindowBox.SelectedIndex = 7; Plot.WindowSeconds = 10; Plot.InvalidateVisual(); }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!_exportTask.IsCompleted || !_validFilter || _closing) return;
        var picker = new SaveFileDialog { Filter = "CSV 数据|*.csv", FileName = "pid.csv" }; if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        await ExportCsvAsync(picker.FileName, ExportFilteredBox.IsChecked == true);
    }

    internal async Task ExportCsvAsync(string path, bool includeFiltered, CancellationToken cancellationToken = default)
    {
        if (!_exportTask.IsCompleted || _closing) throw new InvalidOperationException("当前不能导出。");
        PidSeries[] series;
        lock (_sync) series = _channels.Select((channel, index) => new PidSeries(index, channel.Samples.ToArray())).ToArray();
        var mode = Plot.SmoothingMode; var window = Plot.SmoothingWindow; var alpha = Plot.SmoothingAlpha;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); _exportCancellation = cancel; ExportButton.IsEnabled = false;
        CancelExportButton.Visibility = Visibility.Visible;
        try
        {
            _exportTask = Task.Run(() => WriteCsvAsync(path, series, includeFiltered, mode, window, alpha, cancel.Token), cancel.Token);
            await _exportTask;
            if (!_closing) StatusText.Text = $"已导出 {series.Sum(s => s.Samples.Length):N0} 个保留采样，包含原始值{(includeFiltered ? "与滤波值" : "")}。";
        }
        catch (OperationCanceledException) { if (!_closing) StatusText.Text = "导出已取消。"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { if (!_closing) StatusText.Text = "导出失败：" + ex.Message; }
        finally { _exportCancellation = null; CancelExportButton.Visibility = Visibility.Collapsed; if (!_closing) ExportButton.IsEnabled = _validFilter; }
    }

    private void CancelExport_Click(object sender, RoutedEventArgs e) => _exportCancellation?.Cancel();

    private static async Task WriteCsvAsync(string path, PidSeries[] series, bool includeFiltered, SignalSmoothingMode mode, int window, double alpha, CancellationToken cancel)
    {
        var target = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, ".pid-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var rows = series.SelectMany(s =>
            {
                var filtered = includeFiltered ? PidPlot.ApplySmoothing(s.Samples, mode, window, alpha) : s.Samples;
                return s.Samples.Select((sample, index) => (s.Channel, Raw: sample, Filtered: filtered[index].Actual));
            }).OrderBy(row => row.Raw.Seconds).ToArray();
            await using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(true)))
            {
                await writer.WriteLineAsync((includeFiltered ? "seconds,channel,target,actual,filtered_actual" : "seconds,channel,target,actual").AsMemory(), cancel);
                foreach (var row in rows)
                {
                    var line = FormattableString.Invariant($"{row.Raw.Seconds:F6},{row.Channel + 1},{row.Raw.Target},{row.Raw.Actual:G17}");
                    await writer.WriteLineAsync((includeFiltered ? line + "," + row.Filtered.ToString("G17", CultureInfo.InvariantCulture) : line).AsMemory(), cancel);
                }
                await writer.FlushAsync(cancel);
            }
            cancel.ThrowIfCancellationRequested();
            if (File.Exists(target)) File.Replace(temporary, target, null); else File.Move(temporary, target);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public Task ShutdownAsync() => _shutdown ??= ShutdownCoreAsync();
    private async Task ShutdownCoreAsync()
    {
        _closing = true; _timer.Stop(); _exportCancellation?.Cancel();
        IsEnabled = false;
        await Connection.StopAsync();
        try { await _exportTask; } catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException) { }
    }
}
