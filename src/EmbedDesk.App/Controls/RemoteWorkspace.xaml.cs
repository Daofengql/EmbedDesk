using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Globalization;
using System.Windows.Data;
using EmbedDesk.App.ViewModels;
using EmbedDesk.Core.Models;
using Microsoft.Win32;
using System.ComponentModel;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace EmbedDesk.App.Controls;

public sealed class InverseBooleanVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is bool flag && !flag ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class RemoteProtocolLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is RemoteProtocol protocol ? protocol switch { RemoteProtocol.Ssh => "SSH", RemoteProtocol.Rdp => "RDP", RemoteProtocol.Vnc => "VNC", _ => "Telnet" } : value?.ToString() ?? string.Empty;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public partial class RemoteWorkspace : UserControl
{
    private RemoteWorkspaceViewModel? _viewModel;
    private Point? _dragStart;
    private bool _localLoaded;

    public RemoteWorkspace()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null) _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _viewModel = DataContext as RemoteWorkspaceViewModel;
            _localLoaded = false;
            if (_viewModel is not null) _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            ApplyDockLayout();
        };
        Loaded += (_, _) => { if (_viewModel is not null) { _viewModel.PropertyChanged -= ViewModel_PropertyChanged; _viewModel.PropertyChanged += ViewModel_PropertyChanged; } ApplyDockLayout(); };
        Unloaded += (_, _) => { if (_viewModel is not null) _viewModel.PropertyChanged -= ViewModel_PropertyChanged; DockFilePane(); };
    }

    private async void Terminal_InputReceived(object? sender, string text)
    {
        if (_viewModel is not null) await _viewModel.SendTerminalInputAsync(text);
    }

    private async void Terminal_ResizeReceived(object? sender, TerminalSize size)
    {
        if (_viewModel is not null) await _viewModel.ResizeTerminalAsync(size.Columns, size.Rows);
    }

    private void ToggleFullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        if (!_viewModel.IsWorkspaceReady && !_viewModel.IsTerminalFullscreen) return;
        _viewModel.IsTerminalFullscreen = !_viewModel.IsTerminalFullscreen;
    }

    private async void Directory_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is not null && EntryAtPointer(RemoteFiles, e.OriginalSource) is { } entry) { e.Handled = true; await _viewModel.OpenEntryAsync(entry); }
    }

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        var dialog = new OpenFileDialog { Filter = "所有文件|*.*", Multiselect = false };
        if (dialog.ShowDialog(DialogOwner) == true && ConfirmUpload(dialog.FileName)) await _viewModel.UploadAsync(dialog.FileName);
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedEntry is not { IsDirectory: false } entry) return;
        var dialog = new SaveFileDialog { FileName = entry.Name, Filter = "所有文件|*.*" };
        if (dialog.ShowDialog(DialogOwner) == true) await _viewModel.DownloadAsync(entry, dialog.FileName);
    }

    private FilePaneWindow? _fileWindow;
    private Window? DialogOwner => _fileWindow ?? Window.GetWindow(this);

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RemoteWorkspaceViewModel.IsDirectoryPaneExpanded) or nameof(RemoteWorkspaceViewModel.IsTerminalFullscreen)) ApplyDockLayout();
    }
    private void DockLayout_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyDockLayout();
    private void DirectoryPane_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateFileColumns();
    private void ApplyDockLayout()
    {
        if (_viewModel is null || DockLayout is null) return;
        var floating = _fileWindow is not null;
        var expanded = !floating && _viewModel.IsDirectoryPaneExpanded && !_viewModel.IsTerminalFullscreen;
        DockLayout.Margin = new Thickness(_viewModel.IsTerminalFullscreen ? 0 : 8);
        ShellPane.BorderThickness = ShellPane.Padding = new Thickness(_viewModel.IsTerminalFullscreen ? 0 : 1);
        DirectoryPane.Visibility = expanded || floating ? Visibility.Visible : Visibility.Collapsed;
        DirectoryRestore.Visibility = !_viewModel.IsTerminalFullscreen ? Visibility.Visible : Visibility.Collapsed;
        DirectorySplitter.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        DirectoryRow.MinHeight = expanded ? 120 : 0;
        DirectoryRow.MaxHeight = Math.Max(120, DockLayout.ActualHeight * .65);
        DirectoryRow.Height = new GridLength(expanded ? Math.Min(_viewModel.DirectoryPaneHeight, Math.Max(120, DockLayout.ActualHeight * .6)) : 0);
        BottomGap.Height = new GridLength(_viewModel.IsTerminalFullscreen ? 0 : 22);
        FloatingLabel.Visibility = floating ? Visibility.Visible : Visibility.Collapsed;
        DirectoryRestore.Width = floating ? 220 : 72;
        DirectoryRestore.ToolTip = floating ? "显示文件窗口" : expanded ? "收起目录" : "显示目录";
        DirectoryToggleGlyph.Text = expanded ? "\uE70D" : "\uE70E";
        DetachMenuItem.IsEnabled = !floating;
        DockMenuItem.IsEnabled = floating;
        UpdateFileColumns();
    }
    private void UpdateFileColumns()
    {
        if (_viewModel is null || LocalPane is null) return;
        var dual = DirectoryPane.ActualWidth >= 650;
        if ((LocalPane.Visibility == Visibility.Visible) != dual)
        {
            LocalPane.Visibility = LocalSplitter.Visibility = dual ? Visibility.Visible : Visibility.Collapsed;
            LocalColumn.MinWidth = dual ? 180 : 0;
            LocalColumn.Width = dual ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            LocalGap.Width = new GridLength(dual ? 5 : 0);
        }
        if (dual && DirectoryPane.IsVisible && !_localLoaded) { _localLoaded = true; _ = _viewModel.RefreshLocalDirectoryAsync(); }
    }
    private void DirectorySplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.DirectoryPaneHeight = DirectoryRow.ActualHeight;
    }
    private void RestoreFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_fileWindow is not null) { if (_fileWindow.WindowState == WindowState.Minimized) _fileWindow.WindowState = WindowState.Normal; _fileWindow.Activate(); }
        else _viewModel?.ToggleDirectoryPaneCommand.Execute(null);
    }
    private void ConnectionLog_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box && !box.IsKeyboardFocusWithin) box.ScrollToEnd();
    }
    private void ShowConnectionLog_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        var log = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top, FontFamily = new FontFamily("Cascadia Mono,Consolas"), FontSize = 12, Margin = new Thickness(12) };
        log.SetBinding(TextBox.TextProperty, new Binding(nameof(RemoteWorkspaceViewModel.ConnectionLogText)) { Source = _viewModel, Mode = BindingMode.OneWay });
        var window = new Window { Title = $"SSH 连接日志 · {_viewModel.DisplayTitle}", Owner = Window.GetWindow(this), Width = 800, Height = 520, Content = log, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        WindowTheme.ApplyDarkTitleBar(window);
        window.Show();
    }
    private void DetachFiles_Click(object sender, RoutedEventArgs e) => DetachFilePane();
    private void DockFiles_Click(object sender, RoutedEventArgs e) => DockFilePane();

    public void DetachFilePane()
    {
        if (_fileWindow is not null || _viewModel is null) return;
        var owner = Window.GetWindow(this);
        if (owner is null) return;
        var workArea = SystemParameters.WorkArea;
        var window = new FilePaneWindow
        {
            Owner = owner, DataContext = _viewModel,
            Title = $"文件 · {_viewModel.DisplayTitle}",
            Width = Math.Min(940, workArea.Width - 32),
            Height = Math.Min(460, workArea.Height - 32),
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        _fileWindow = window;
        DockLayout.Children.Remove(DirectoryPane);
        DirectoryPane.DataContext = _viewModel;
        window.Content = DirectoryPane;
        window.MoveStarted += (_, _) =>
        {
            if (Ancestor<RemoteDebugWorkspace>(this)?.DataContext is RemoteDebugWorkspaceViewModel manager)
                manager.SelectSessionCommand.Execute(_viewModel);
        };
        window.MoveUpdated += (_, point) => ShowDockHint(point);
        window.MoveCompleted += (_, point) =>
        {
            var merge = IsDockTarget(point);
            DockHint.IsOpen = false;
            if (merge) _ = Dispatcher.BeginInvoke(new Action(DockFilePane));
        };
        window.MoveCancelled += (_, _) => DockHint.IsOpen = false;
        window.Closed += (_, _) =>
        {
            DockHint.IsOpen = false;
            window.Content = null;
            _fileWindow = null;
            if (!DockLayout.Children.Contains(DirectoryPane)) DockLayout.Children.Add(DirectoryPane);
            _viewModel.IsDirectoryPaneExpanded = true;
            ApplyDockLayout();
        };
        ApplyDockLayout();
        window.Show();
    }
    public void DockFilePane() => _fileWindow?.Close();

    public bool IsDockTarget(Point screenPoint)
    {
        if (!IsVisible || _fileWindow is null || Window.GetWindow(this)?.WindowState == WindowState.Minimized) return false;
        var point = DockLayout.PointFromScreen(screenPoint);
        return point.X >= 0 && point.X <= DockLayout.ActualWidth && point.Y >= Math.Max(0, DockLayout.ActualHeight - 110) && point.Y <= DockLayout.ActualHeight;
    }
    private void ShowDockHint(Point point)
    {
        DockHint.Width = Math.Max(0, DockLayout.ActualWidth - 16);
        DockHint.HorizontalOffset = 8;
        DockHint.VerticalOffset = Math.Max(0, DockLayout.ActualHeight - 90);
        DockHint.IsOpen = IsDockTarget(point);
    }
    private void DirectoryDrag_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = Ancestor<Button>(e.OriginalSource as DependencyObject) is null ? e.GetPosition(DirectoryDragHandle) : null;
        if (_dragStart is not null) DirectoryDragHandle.CaptureMouse();
    }
    private void DirectoryDrag_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        if (DirectoryDragHandle.IsMouseCaptured) DirectoryDragHandle.ReleaseMouseCapture();
    }
    private void DirectoryDrag_LostMouseCapture(object sender, MouseEventArgs e) => _dragStart = null;
    private void DirectoryDrag_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(DirectoryDragHandle);
        if (Math.Abs(point.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragStart = null;
        var screenPoint = DirectoryDragHandle.PointToScreen(point);
        var newlyDetached = _fileWindow is null;
        DetachFilePane();
        if (_fileWindow is not { } window) return;
        if (newlyDetached)
        {
            var source = PresentationSource.FromVisual(window);
            var position = source?.CompositionTarget?.TransformFromDevice.Transform(screenPoint) ?? screenPoint;
            window.Left = position.X - Math.Min(start.X, window.Width - 40);
            window.Top = position.Y - 42;
        }
        Mouse.Capture(null);
        if (Mouse.LeftButton == MouseButtonState.Pressed) window.DragMove();
        e.Handled = true;
    }
    private void Terminal_FullscreenRequested(object? sender, EventArgs e) => ToggleFullscreen_Click(this, new RoutedEventArgs());
    private static T? Ancestor<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null) { if (child is T found) return found; child = child is Visual ? VisualTreeHelper.GetParent(child) : LogicalTreeHelper.GetParent(child); }
        return null;
    }
    private static RemoteDirectoryEntryViewModel? EntryAtPointer(DataGrid grid, object source) => source is DependencyObject element && ItemsControl.ContainerFromElement(grid, element) is DataGridRow { Item: RemoteDirectoryEntryViewModel entry } ? entry : null;
    private void FileGrid_RightClick(object sender, MouseButtonEventArgs e) { if (sender is DataGrid grid) grid.SelectedItem = EntryAtPointer(grid, e.OriginalSource); }
    private async void OpenRemote_Click(object sender, RoutedEventArgs e) { if (_viewModel is not null) await _viewModel.OpenEntryAsync(_viewModel.SelectedEntry); }
    private async void LocalDirectory_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is not null && EntryAtPointer(LocalFiles, e.OriginalSource) is { IsDirectory: true } entry) { e.Handled = true; await _viewModel.RefreshLocalDirectoryAsync(entry.FullPath); }
    }
    private async void OpenLocal_Click(object sender, RoutedEventArgs e) { if (_viewModel is not null && LocalFiles.SelectedItem is RemoteDirectoryEntryViewModel { IsDirectory: true } entry) await _viewModel.RefreshLocalDirectoryAsync(entry.FullPath); }
    private async void LocalPath_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && _viewModel is not null) { e.Handled = true; await _viewModel.RefreshLocalDirectoryAsync(LocalPathBox.Text); LocalPathBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget(); } }
    private async void RemotePath_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && _viewModel is not null) { e.Handled = true; await _viewModel.NavigateRemoteAsync(RemotePathBox.Text); RemotePathBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget(); } }
    private async void RefreshLocal_Click(object sender, RoutedEventArgs e) { if (_viewModel is not null) await _viewModel.RefreshLocalDirectoryAsync(); }
    private async void ChooseLocalFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        var dialog = new OpenFolderDialog { InitialDirectory = _viewModel.LocalPath };
        if (dialog.ShowDialog(DialogOwner) == true) await _viewModel.RefreshLocalDirectoryAsync(dialog.FolderName);
    }
    private bool ConfirmUpload(string file)
    {
        var name = System.IO.Path.GetFileName(file);
        return _viewModel?.DirectoryEntries.Any(entry => entry.Name == name) != true || new ConfirmationWindow("覆盖远程文件", $"远程目录已有“{name}”，确定覆盖吗？", "覆盖上传") { Owner = DialogOwner }.ShowDialog() == true;
    }
    private async void UploadLocal_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && LocalFiles.SelectedItem is RemoteDirectoryEntryViewModel { IsDirectory: false } entry && ConfirmUpload(entry.FullPath)) await _viewModel.UploadAsync(entry.FullPath);
    }
    private async void DownloadToLocal_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedEntry is not { IsDirectory: false } entry) return;
        var destination = System.IO.Path.Combine(_viewModel.LocalPath, entry.Name);
        if (System.IO.File.Exists(destination) && new ConfirmationWindow("覆盖本地文件", $"本地目录已有“{entry.Name}”，确定覆盖吗？", "覆盖下载") { Owner = DialogOwner }.ShowDialog() != true) return;
        await _viewModel.DownloadToLocalAsync(entry);
    }
    private async void DeleteRemote_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedEntry is not { IsDirectory: false } entry || !_viewModel.DeleteEntryCommand.CanExecute(null)) return;
        if (new ConfirmationWindow("删除远程文件", $"确定删除“{entry.Name}”吗？此操作无法撤销。", "删除文件") { Owner = DialogOwner }.ShowDialog() == true) await _viewModel.DeleteEntryCommand.ExecuteAsync(null);
    }
    private void CopyRemotePath_Click(object sender, RoutedEventArgs e) { if (_viewModel is not null) Clipboard.SetText(_viewModel.SelectedEntry?.FullPath ?? _viewModel.Path); }
    private void CopyLocalPath_Click(object sender, RoutedEventArgs e) { if (_viewModel is not null) Clipboard.SetText((LocalFiles.SelectedItem as RemoteDirectoryEntryViewModel)?.FullPath ?? _viewModel.LocalPath); }
    private void RemoteMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || _viewModel is null) return;
        var available = _viewModel.IsConnected && _viewModel.SupportsDirectoryManagement && !_viewModel.IsBusy;
        foreach (var item in menu.Items.OfType<MenuItem>())
            if (item.Tag is string tag) item.IsEnabled = available && (tag == "transfer" || tag == "open" && _viewModel.SelectedEntry is not null || tag is "download" or "delete" && _viewModel.SelectedEntry is { IsDirectory: false });
    }
    private void LocalMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        foreach (var item in menu.Items.OfType<MenuItem>())
            if (item.Tag is string tag) item.IsEnabled = tag == "open" ? LocalFiles.SelectedItem is RemoteDirectoryEntryViewModel { IsDirectory: true } : LocalFiles.SelectedItem is RemoteDirectoryEntryViewModel { IsDirectory: false } && _viewModel is { IsConnected: true, SupportsDirectoryManagement: true, IsBusy: false };
    }
}

