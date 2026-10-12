using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using EmbedDesk.App.ViewModels;
using EmbedDesk.Infrastructure.Diagnostics;
using Microsoft.Win32;

namespace EmbedDesk.App;

public partial class TrafficRecordingWindow : Window
{
    private readonly MainWindowViewModel _owner;
    private readonly ObservableCollection<RecordRow> _preview = [];
    private TrafficRecording? _recording;
    private TrafficRecordingWriter? _writer;
    private TerminalBufferViewModel? _captureSource;
    private CancellationTokenSource? _operation;
    private bool _closeWhenIdle;
    private long _capturedBytes;
    private bool Busy => _writer is not null || _operation is not null;
    private SourceChoice? Source => SourceBox.SelectedItem as SourceChoice;

    public TrafficRecordingWindow(MainWindowViewModel owner)
    {
        _owner = owner;
        InitializeComponent(); WindowTheme.ApplyDarkTitleBar(this);
        RecordsGrid.ItemsSource = _preview;
        RefreshSources();
        Closing += OnClosing;
    }

    private void RefreshSources()
    {
        var selected = Source?.Buffer;
        var items = _owner.SerialSessions.Select(s => new SourceChoice($"串口 · {s.Title}", s.Terminal, s))
            .Concat(_owner.Sessions.Select(s => new SourceChoice($"{s.Protocol} {s.Mode} · {s.Endpoint}", s.Terminal, null))).ToArray();
        SourceBox.ItemsSource = items;
        SourceBox.SelectedItem = items.FirstOrDefault(s => s.Buffer == selected) ?? items.FirstOrDefault();
        UpdateState();
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) { if (!Busy) RefreshSources(); }
    private void Source_Changed(object sender, SelectionChangedEventArgs e) { if (PlayButton is not null) UpdateState(); }

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (Busy || Source is not { } source) return;
        var dialog = SaveDialog("录制原始收发数据");
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _capturedBytes = 0;
            var writer = new TrafficRecordingWriter(new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write, FileShare.Read, 65536, true), source.Label);
            _writer = writer; _captureSource = source.Buffer;
            _captureSource.TrafficCaptured += Capture;
            Status.Text = $"正在录制 {source.Label} → {dialog.FileName}";
            UpdateState();
            _ = ObserveWriterAsync(writer);
        }
        catch (Exception ex) { Status.Text = "无法开始录制：" + ex.Message; }
    }
    private void Capture(DateTimeOffset timestamp, bool received, ReadOnlyMemory<byte> data)
    {
        if (_writer?.TryAppend(timestamp, received, data) == true) _capturedBytes += data.Length;
    }
    private void Unsubscribe()
    {
        if (_captureSource is not null) _captureSource.TrafficCaptured -= Capture;
        _captureSource = null;
    }
    private async Task ObserveWriterAsync(TrafficRecordingWriter writer)
    {
        try
        {
            await writer.Completion;
            Status.Text = writer.Failure is { } error ? "录制停止：" + error.Message : $"录制已保存 · {_capturedBytes:N0} 字节。可打开文件回放。";
        }
        catch (Exception ex) { Status.Text = "保存录制失败：" + ex.Message; }
        finally
        {
            Unsubscribe(); _writer = null; UpdateState();
            if (_closeWhenIdle) Close();
        }
    }
    private async Task StopAsync()
    {
        _operation?.Cancel();
        if (_writer is { } writer)
        {
            Unsubscribe(); StopButton.IsEnabled = false; Status.Text = "正在保存录制…";
            try { await writer.DisposeAsync(); } catch (Exception ex) { Status.Text = "录制保存失败：" + ex.Message; }
        }
    }
    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopAsync();

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (Busy || Source is not { } source) return;
        var dialog = SaveDialog("导出当前会话保留的历史");
        if (dialog.ShowDialog(this) != true) return;
        var recording = new TrafficRecording(source.Label, source.Buffer.Entries.Select(e => new TrafficRecord(e.Timestamp, e.IsReceived, e.Data.ToArray())).ToArray());
        await RunAsync(async token =>
        {
            await using var file = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
            await TrafficRecordingFile.SaveAsync(file, recording, token);
            _recording = recording; ShowPreview();
            Status.Text = $"已导出 {recording.Records.Count:N0} 条记录（会话最多保留 4 MiB / 1,000 条历史）。";
        });
    }
    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Busy) return;
        var dialog = new OpenFileDialog { Title = "打开原始流量录制", Filter = "EDBG 流量录制|*.edbgtrace" };
        if (dialog.ShowDialog(this) != true) return;
        await RunAsync(async token =>
        {
            Status.Text = "读取并检查录制完整性…";
            var recording = await Task.Run(async () =>
            {
                await using var file = new FileStream(dialog.FileName, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                return await TrafficRecordingFile.LoadAsync(file, token);
            }, token);
            _recording = recording; ShowPreview();
            Status.Text = $"{recording.Source} · {recording.Records.Count:N0} 条 · {recording.Records.Sum(r => (long)r.Data.Length):N0} 字节；列表显示最后 1,000 条。";
        });
    }
    private void ShowPreview()
    {
        _preview.Clear(); RawBox.Clear();
        foreach (var record in _recording!.Records.TakeLast(1000)) _preview.Add(new(record));
    }
    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (Busy || _recording is not { Records.Count: > 0 } recording) return;
        var speed = double.Parse((string)((ComboBoxItem)SpeedBox.SelectedItem).Tag, CultureInfo.InvariantCulture);
        var target = SendCheck.IsChecked == true ? Source?.Serial : null;
        if (SendCheck.IsChecked == true && target is null) { Status.Text = "重发目标必须是串口会话。"; return; }
        if (target is not null)
        {
            var count = recording.Records.Where(r => !r.IsReceived).Sum(r => (long)r.Data.Length);
            if (count == 0) { Status.Text = "录制没有 TX 数据。"; return; }
            var confirm = new ConfirmationWindow("向串口重发录制", $"将以 {speed}× 速度向 {target.Title}（{target.SelectedSerialPort?.PortName}）重发 {count:N0} 字节 TX 数据。接收到的 RX 不会发出，确认继续？", "确认重发") { Owner = this };
            if (confirm.ShowDialog() != true) return;
        }
        await RunAsync(async token =>
        {
            if (target is not null)
            {
                Status.Text = $"向 {target.Title} 回放 TX…";
                await target.ReplayRecordingAsync(recording.Records, speed, token);
            }
            else
            {
                _preview.Clear(); Status.Text = "按原始时间间隔回放…";
                var count = 0;
                await TrafficRecordingFile.ReplayAsync(recording.Records, speed, async (record, ct) =>
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        ct.ThrowIfCancellationRequested();
                        _preview.Add(new(record));
                        if (_preview.Count > 1000) _preview.RemoveAt(0);
                        Status.Text = $"回放 {++count:N0} / {recording.Records.Count:N0} 条 · {speed}×";
                    });
                }, token);
            }
            Status.Text = "回放完成。";
        });
    }
    private void Record_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (RecordsGrid.SelectedItem is not RecordRow row) return;
        var data = row.Record.Data;
        var shown = data.AsSpan(0, Math.Min(4096, data.Length));
        RawBox.Text = $"{row.Record.Timestamp:O} · {row.Direction} · {data.Length:N0} 字节\n{Convert.ToHexString(shown)}" + (data.Length > shown.Length ? "\n… HEX 预览仅显示前 4 KiB，录制保留完整字节。" : "");
    }
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (Busy || _closeWhenIdle) return;
        using var cancellation = new CancellationTokenSource(); _operation = cancellation; UpdateState();
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { Status.Text = "已停止。"; }
        catch (Exception ex) { Status.Text = "操作失败：" + ex.Message; }
        finally { _operation = null; UpdateState(); if (_closeWhenIdle) Close(); }
    }
    private void UpdateState()
    {
        SourceBox.IsEnabled = RefreshButton.IsEnabled = OpenButton.IsEnabled = SpeedBox.IsEnabled = !Busy;
        RecordButton.IsEnabled = ExportButton.IsEnabled = !Busy && Source is not null;
        SendCheck.IsEnabled = !Busy && Source?.Serial is not null;
        if (Source?.Serial is null) SendCheck.IsChecked = false;
        PlayButton.IsEnabled = !Busy && _recording is { Records.Count: > 0 };
        StopButton.IsEnabled = Busy;
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!Busy) return;
        e.Cancel = true; _closeWhenIdle = true;
        await StopAsync();
    }
    private static SaveFileDialog SaveDialog(string title) => new() { Title = title, Filter = "EDBG 流量录制|*.edbgtrace", DefaultExt = ".edbgtrace", FileName = $"traffic-{DateTime.Now:yyyyMMdd-HHmmss}.edbgtrace" };
    private sealed record SourceChoice(string Label, TerminalBufferViewModel Buffer, SerialSessionViewModel? Serial);
    private sealed record RecordRow(TrafficRecord Record)
    {
        public string Time => Record.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
        public string Direction => Record.IsReceived ? "RX" : "TX";
        public int Count => Record.Data.Length;
        public string Text => Encoding.UTF8.GetString(Record.Data.AsSpan(0, Math.Min(200, Record.Data.Length))).Replace('\r', ' ').Replace('\n', ' ').Replace('\0', '·');
    }
}
