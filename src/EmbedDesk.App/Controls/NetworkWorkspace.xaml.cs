using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using System.Net.NetworkInformation;
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
    private readonly bool _singleSessionMode;
    private readonly NetworkSessionViewModel? _fixedSession;
    public NetworkWorkspace(bool singleSessionMode = false)
    {
        _singleSessionMode = singleSessionMode;
        InitializeComponent();
        if (_singleSessionMode) ApplySingleSessionLayout();
        _presenter = new BufferedTrafficPresenter(ReceiveText);
        Loaded += (_, _) => BindViewModel();
        Unloaded += (_, _) => Unbind();
        DataContextChanged += (_, _) => { if (IsLoaded) BindViewModel(); };
        ReceiveText.SizeChanged += (_, _) => { if (_session?.AutoScroll == true) Dispatcher.BeginInvoke(new Action(ReceiveText.ScrollToEnd), DispatcherPriority.Background); };
        Loaded += (_, _) => NetworkChange.NetworkAddressChanged += NetworkAddressChanged;
        Unloaded += (_, _) => NetworkChange.NetworkAddressChanged -= NetworkAddressChanged;
    }
    public NetworkWorkspace(NetworkSessionViewModel session) : this(true)
    {
        _fixedSession = session ?? throw new ArgumentNullException(nameof(session));
    }
    private void ApplySingleSessionLayout()
    {
        WorkspaceHeader.Visibility = Visibility.Collapsed;
        SessionPane.Visibility = SessionDivider.Visibility = SessionSplitter.Visibility = SessionToggle.Visibility = Visibility.Collapsed;
        EmptyNewSessionButton.Visibility = Visibility.Collapsed;
        SessionColumn.MinWidth = 0; SessionColumn.Width = new GridLength(0);
        SpacerColumn.Width = new GridLength(0);
    }
    private void BindViewModel()
    {
        Unbind(); _vm = DataContext as MainWindowViewModel;
        if (_vm is not null) _vm.PropertyChanged += MainChanged;
        if (_vm is not null)
        {
            var settings = _vm.AppSettings.Snapshot();
            _paneWidth = Math.Clamp(settings.NetworkSessionPaneWidth, 180, 280);
            _expanded = settings.NetworkSessionPaneExpanded;
            ApplySessionPaneLayout();
        }
        BindSession();
    }
    private void NetworkAddressChanged(object? sender, EventArgs e)
    {
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_vm is not null)
                foreach (var session in _vm.Sessions) session.RefreshServerAddressHints();
        }));
    }
    private void Unbind()
    {
        if (_vm is not null) _vm.PropertyChanged -= MainChanged;
        if (_session is not null) { _session.PropertyChanged -= SessionChanged; }
        _session = null; _vm = null;
        _presenter.Bind(null);
    }
    private void MainChanged(object? sender, PropertyChangedEventArgs e) { if (_fixedSession is null && e.PropertyName == nameof(MainWindowViewModel.SelectedSession)) BindSession(); }
    private void BindSession()
    {
        if (_session is not null) { _session.PropertyChanged -= SessionChanged; }
        _session = _fixedSession ?? _vm?.SelectedSession;
        if (_fixedSession is not null) SessionContent.DataContext = _fixedSession;
        if (_session is not null) { _session.PropertyChanged += SessionChanged; }
        SessionContent.Visibility = _session is null ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = _session is null ? Visibility.Visible : Visibility.Collapsed;
        RefreshReceive();
    }
    private void SessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NetworkSessionViewModel.HexDisplay) or nameof(NetworkSessionViewModel.ShowTime) or nameof(NetworkSessionViewModel.ShowSent) or nameof(NetworkSessionViewModel.AutoScroll) or nameof(NetworkSessionViewModel.SeparateMessages) or nameof(NetworkSessionViewModel.IsDatagram)) RefreshReceive();
    }
    private void RefreshReceive()
    {
        _presenter.Bind(_session?.Terminal);
        if (_session is not null) _presenter.Configure(new(_session.HexDisplay, _session.ShowTime, _session.ShowSent, _session.IsDatagram && _session.SeparateMessages), _session.AutoScroll);
    }
    private async void NewSession_Click(object sender, RoutedEventArgs e)
    {
        if (_singleSessionMode || _vm is null) return;
        var picker = new NetworkTypeSelectionWindow { Owner = Window.GetWindow(this) };
        if (picker.ShowDialog() != true || picker.Protocol is not { } protocol) return;
        var dialog = new NetworkConnectionWindow(protocol, _vm) { Owner = Window.GetWindow(this) };
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
        ApplySessionPaneLayout();
        SaveSessionPanePreferences();
    }
    private void ApplySessionPaneLayout()
    {
        if (_singleSessionMode) { ApplySingleSessionLayout(); return; }
        SessionColumn.MinWidth = _expanded ? 180 : 0;
        SessionColumn.Width = new GridLength(_expanded ? Math.Clamp(_paneWidth, 180, 280) : 0);
        SessionPane.Visibility = SessionSplitter.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        SessionToggleGlyph.Text = _expanded ? "\uE76B" : "\uE76C";
        SessionToggle.ToolTip = _expanded ? "收起会话列表" : "显示会话列表";
    }
    internal void DisposeView() => Unbind();
    private void SessionSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!_expanded || _vm is null) return;
        _paneWidth = Math.Clamp(SessionColumn.ActualWidth, 180, 280);
        SaveSessionPanePreferences();
    }
    private void SaveSessionPanePreferences()
    {
        _vm?.AppSettings.Update(settings =>
        {
            settings.NetworkSessionPaneWidth = Math.Clamp(_paneWidth, 180, 280);
            settings.NetworkSessionPaneExpanded = _expanded;
        });
    }
    private void CopyServerAddresses_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string addresses && !string.IsNullOrWhiteSpace(addresses))
        {
            try { Clipboard.SetText(addresses); }
            catch (System.Runtime.InteropServices.ExternalException) { }
        }
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
