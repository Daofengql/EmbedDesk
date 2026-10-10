using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using EmbeddedDebugger.App.ViewModels;
using Microsoft.Win32;

namespace EmbeddedDebugger.App.Controls;

public partial class NetworkWorkspace : UserControl
{
    private MainWindowViewModel? _vm;
    private NetworkSessionViewModel? _session;
    private readonly DispatcherTimer _render = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private double _paneWidth = 210;
    private bool _expanded = true;
    public NetworkWorkspace()
    {
        InitializeComponent();
        _render.Tick += (_, _) => { _render.Stop(); RefreshReceive(); };
        Loaded += (_, _) => BindViewModel();
        Unloaded += (_, _) => { _render.Stop(); Unbind(); };
        DataContextChanged += (_, _) => { if (IsLoaded) BindViewModel(); };
        ReceiveText.SizeChanged += (_, _) => { if (_session?.AutoScroll == true) Dispatcher.BeginInvoke(new Action(ReceiveText.ScrollToEnd), DispatcherPriority.Background); };
    }
    private void BindViewModel()
    {
        Unbind(); _vm = DataContext as MainWindowViewModel;
        if (_vm is not null) _vm.PropertyChanged += MainChanged;
        BindSession();
    }
    private void Unbind()
    {
        if (_vm is not null) _vm.PropertyChanged -= MainChanged;
        if (_session is not null) { _session.PropertyChanged -= SessionChanged; _session.Terminal.Entries.CollectionChanged -= EntriesChanged; }
        _session = null; _vm = null;
    }
    private void MainChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(MainWindowViewModel.SelectedSession)) BindSession(); }
    private void BindSession()
    {
        if (_session is not null) { _session.PropertyChanged -= SessionChanged; _session.Terminal.Entries.CollectionChanged -= EntriesChanged; }
        _session = _vm?.SelectedSession;
        if (_session is not null) { _session.PropertyChanged += SessionChanged; _session.Terminal.Entries.CollectionChanged += EntriesChanged; }
        SessionContent.Visibility = _session is null ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = _session is null ? Visibility.Visible : Visibility.Collapsed;
        RefreshReceive();
    }
    private void SessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NetworkSessionViewModel.HexDisplay) or nameof(NetworkSessionViewModel.ShowTime) or nameof(NetworkSessionViewModel.ShowSent) or nameof(NetworkSessionViewModel.AutoScroll)) RefreshReceive();
    }
    private void EntriesChanged(object? sender, NotifyCollectionChangedEventArgs e) { if (!_render.IsEnabled) _render.Start(); }
    private void RefreshReceive()
    {
        if (_session is null) { ReceiveText.Clear(); return; }
        var text = new StringBuilder();
        var rx = Encoding.UTF8.GetDecoder(); var tx = Encoding.UTF8.GetDecoder();
        foreach (var entry in _session.Terminal.Entries)
        {
            if (!entry.IsReceived && !_session.ShowSent) continue;
            if (_session.ShowTime) text.Append('[').Append(entry.DisplayTime).Append("] ");
            if (_session.ShowSent) text.Append(entry.Direction).Append("  ");
            if (_session.HexDisplay) text.Append(entry.DisplayHex);
            else
            {
                var decoder = entry.IsReceived ? rx : tx;
                var chars = new char[Encoding.UTF8.GetMaxCharCount(entry.Data.Length)];
                var count = decoder.GetChars(entry.Data, chars, false);
                text.Append(chars, 0, count);
            }
            if ((_session.HexDisplay || _session.ShowTime || _session.ShowSent) && (text.Length == 0 || text[^1] != '\n')) text.AppendLine();
            if (text.Length > 1_000_000) text.Remove(0, text.Length - 1_000_000);
        }
        var offset = ReceiveText.VerticalOffset;
        ReceiveText.Text = text.ToString();
        if (_session.AutoScroll) ReceiveText.ScrollToEnd(); else ReceiveText.ScrollToVerticalOffset(offset);
    }
    private async void NewSession_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var dialog = new NetworkConnectionWindow { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.Options is { } options) await _vm.CreateNetworkSessionAsync(options);
    }
    private async void CloseSession_Click(object sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedSession is not { } session) return;
        if (new ConfirmationWindow("关闭网络会话", $"确定关闭“{session.Detail}”吗？连接及发送任务将停止。", "关闭会话") { Owner = Window.GetWindow(this) }.ShowDialog() == true)
        {
            if (_vm.SelectedSession == session) await _vm.RemoveSelectedNetworkCommand.ExecuteAsync(null);
        }
    }
    private void ToggleSessions_Click(object sender, RoutedEventArgs e)
    {
        if (_expanded) _paneWidth = SessionColumn.ActualWidth;
        _expanded = !_expanded;
        SessionColumn.MinWidth = _expanded ? 160 : 0;
        SessionColumn.Width = new GridLength(_expanded ? _paneWidth : 0);
        SessionPane.Visibility = SessionSplitter.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        SessionToggleGlyph.Text = _expanded ? "\uE76B" : "\uE76C";
        SessionToggle.ToolTip = _expanded ? "收起会话列表" : "显示会话列表";
    }
    private async void SaveReceive_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "文本文件|*.txt", FileName = $"network-{DateTime.Now:yyyyMMdd-HHmmss}.txt" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try { await File.WriteAllTextAsync(dialog.FileName, ReceiveText.Text); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { new ConfirmationWindow("保存失败", ex.Message, "确定") { Owner = Window.GetWindow(this) }.ShowDialog(); }
    }
    private async void Send_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && _session?.SendCommand.CanExecute(null) == true)
        { e.Handled = true; await _session.SendCommand.ExecuteAsync(null); }
    }
}
