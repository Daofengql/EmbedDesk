using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using EmbedDesk.App.ViewModels;
using Microsoft.Win32;

namespace EmbedDesk.App.Controls;

public partial class NetworkWorkspace : UserControl
{
    private MainWindowViewModel? _vm;
    private NetworkSessionViewModel? _session;
    private readonly BufferedTrafficPresenter _presenter;
    private double _paneWidth = 210;
    private bool _expanded = true;
    public NetworkWorkspace()
    {
        InitializeComponent();
        _presenter = new BufferedTrafficPresenter(ReceiveText);
        Loaded += (_, _) => BindViewModel();
        Unloaded += (_, _) => Unbind();
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
        if (_session is not null) { _session.PropertyChanged -= SessionChanged; }
        _session = null; _vm = null;
        _presenter.Bind(null);
    }
    private void MainChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(MainWindowViewModel.SelectedSession)) BindSession(); }
    private void BindSession()
    {
        if (_session is not null) { _session.PropertyChanged -= SessionChanged; }
        _session = _vm?.SelectedSession;
        if (_session is not null) { _session.PropertyChanged += SessionChanged; }
        SessionContent.Visibility = _session is null ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = _session is null ? Visibility.Visible : Visibility.Collapsed;
        RefreshReceive();
    }
    private void SessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NetworkSessionViewModel.HexDisplay) or nameof(NetworkSessionViewModel.ShowTime) or nameof(NetworkSessionViewModel.ShowSent) or nameof(NetworkSessionViewModel.AutoScroll)) RefreshReceive();
    }
    private void RefreshReceive()
    {
        _presenter.Bind(_session?.Terminal);
        if (_session is not null) _presenter.Configure(new(_session.HexDisplay, _session.ShowTime, _session.ShowSent), _session.AutoScroll);
    }
    private async void NewSession_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var picker = new NetworkTypeSelectionWindow { Owner = Window.GetWindow(this) };
        if (picker.ShowDialog() != true || picker.Protocol is not { } protocol) return;
        var dialog = new NetworkConnectionWindow(protocol) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.Options is { } options) await _vm.CreateNetworkSessionAsync(options);
    }

    private async void CloseSessionItem_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null || (sender as Button)?.Tag is not NetworkSessionViewModel session) return;
        if (new ConfirmationWindow("关闭网络会话", $"确定关闭“{session.Detail}”吗？连接及发送任务将停止。", "关闭会话") { Owner = Window.GetWindow(this) }.ShowDialog() == true)
        {
            await _vm.RemoveNetworkSessionAsync(session);
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
        _presenter.Flush();
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
