using System.ComponentModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EmbedDesk.App.ViewModels;

namespace EmbedDesk.App.Controls;

public partial class SerialTabWorkspace : UserControl
{
    private MainWindowViewModel? _viewModel;
    private BridgeWindow? _bridgeWindow;
    private Point? _dragStart;
    private TextBox? _renameEditor;
    private readonly Dictionary<SerialSessionViewModel, SerialWorkspace> _views = [];

    public SerialTabWorkspace()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= SelectionChanged;
                _viewModel.SerialSessions.CollectionChanged -= SessionsChanged;
            }
            foreach (var view in _views.Values) view.DisposeView();
            _views.Clear(); SessionHost.Children.Clear();
            _viewModel = DataContext as MainWindowViewModel;
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged += SelectionChanged;
                _viewModel.SerialSessions.CollectionChanged += SessionsChanged;
            }
            ShowSelectedView();
        };
    }

    private void SelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.SelectedSerialSession)) return;
        FinishRename();
        ShowSelectedView();
        Dispatcher.BeginInvoke(new Action(() => (TabStrip.ItemContainerGenerator.ContainerFromItem(_viewModel!.SelectedSerialSession) as FrameworkElement)?.BringIntoView()));
    }
    private void SessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Header ordering must not detach live WebViews from their native parent.
        if (e.Action == NotifyCollectionChangedAction.Move) return;
        foreach (var session in _views.Keys.Where(s => _viewModel?.SerialSessions.Contains(s) != true).ToArray())
        {
            var view = _views[session]; view.DisposeView();
            SessionHost.Children.Remove(view); _views.Remove(session);
        }
    }
    private void ShowSelectedView()
    {
        var selected = _viewModel?.SelectedSerialSession;
        if (selected is null || selected.IsDisposed) return;
        if (!_views.TryGetValue(selected, out var active))
        {
            active = new SerialWorkspace { DataContext = selected };
            _views.Add(selected, active); SessionHost.Children.Add(active);
        }
        foreach (var pair in _views) pair.Value.Visibility = pair.Key == selected ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SelectTab_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && sender is FrameworkElement { DataContext: SerialSessionViewModel session }) _viewModel.SelectedSerialSession = session;
    }
    private void RenameTab_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is null || sender is not Button { DataContext: SerialSessionViewModel session, Parent: Grid label } || session.IsDisposed) return;
        e.Handled = true; _dragStart = null;
        FinishRename();
        _viewModel.SelectedSerialSession = session;
        var editor = label.Children.OfType<TextBox>().Single();
        _renameEditor = editor;
        editor.Text = session.Title;
        editor.Tag = session.Title;
        editor.Visibility = Visibility.Visible;
        editor.Focus(); editor.SelectAll();
    }
    private void TitleEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape)) return;
        // Enter during IME composition is consumed by the input method first.
        e.Handled = true;
        var label = (sender as FrameworkElement)?.Parent as Grid;
        FinishRename(commit: e.Key == Key.Enter);
        label?.Children.OfType<Button>().FirstOrDefault()?.Focus();
    }
    private void TitleEditor_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => FinishRename();
    private void FinishRename(bool commit = true)
    {
        var editor = _renameEditor;
        _renameEditor = null;
        if (editor is null) return;
        if (commit && editor.Text.Trim() != (string?)editor.Tag && editor.DataContext is SerialSessionViewModel { IsDisposed: false } session) session.CustomTitle = editor.Text;
        editor.Visibility = Visibility.Collapsed;
    }
    private async void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not FrameworkElement { DataContext: SerialSessionViewModel session }) return;
        var message = session.IsOpen || session.IsBusy
            ? $"确定关闭“{session.Title}”吗？将断开串口并停止该标签的发送任务。"
            : $"确定关闭“{session.Title}”吗？该标签的收发记录和发送配置将不再保留。";
        if (new ConfirmationWindow("关闭串口标签", message, "关闭标签") { Owner = Window.GetWindow(this) }.ShowDialog() == true)
            await _viewModel.CloseSerialSessionAsync(session);
    }
    public void ShowBridge()
    {
        if (_bridgeWindow is null)
        {
            _bridgeWindow = new BridgeWindow { Owner = Window.GetWindow(this), DataContext = _viewModel };
            _bridgeWindow.Closed += (_, _) => _bridgeWindow = null;
            _bridgeWindow.Show();
        }
        else _bridgeWindow.Activate();
    }
    private void Tabs_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset - e.Delta); e.Handled = true;
    }
    private void Tab_MouseDown(object sender, MouseButtonEventArgs e) => _dragStart = _renameEditor is null && e.ClickCount == 1 ? e.GetPosition(this) : null;
    private void Tab_MouseMove(object sender, MouseEventArgs e)
    {
        if (_renameEditor is not null || _dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement { DataContext: SerialSessionViewModel session } element) return;
        var delta = e.GetPosition(this) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragStart = null;
        DragDrop.DoDragDrop(element, new DataObject(typeof(SerialSessionViewModel), session), DragDropEffects.Move);
    }
    private void Tab_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetData(typeof(SerialSessionViewModel)) is SerialSessionViewModel session && _viewModel?.SerialSessions.Contains(session) == true ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }
    private void Tab_Drop(object sender, DragEventArgs e)
    {
        if (_viewModel is null || e.Data.GetData(typeof(SerialSessionViewModel)) is not SerialSessionViewModel source || sender is not FrameworkElement { DataContext: SerialSessionViewModel target }) return;
        var from = _viewModel.SerialSessions.IndexOf(source); var to = _viewModel.SerialSessions.IndexOf(target);
        if (from >= 0 && to >= 0 && from != to) _viewModel.SerialSessions.Move(from, to);
        e.Handled = true;
    }
}
