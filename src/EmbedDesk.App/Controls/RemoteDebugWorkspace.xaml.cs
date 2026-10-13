using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EmbedDesk.App.ViewModels;
using EmbedDesk.Core.Models;

namespace EmbedDesk.App.Controls;

public partial class RemoteDebugWorkspace : UserControl
{
    private RemoteDebugWorkspaceViewModel? _viewModel;
    private Point? _tabDragStart;
    private object? _draggedTab;
    private Border? _dropIndicator;
    private TextBox? _renameEditor;
    private object? _renameTab;
    public RemoteDebugWorkspace()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            FinishTabRename(false);
            if (_viewModel is not null) _viewModel.PropertyChanged -= Workspace_PropertyChanged;
            _viewModel = DataContext as RemoteDebugWorkspaceViewModel;
            if (_viewModel is not null) _viewModel.PropertyChanged += Workspace_PropertyChanged;
        };
    }
    private void Workspace_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RemoteDebugWorkspaceViewModel.ActiveTab)) return;
        FinishTabRename();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_viewModel?.ActiveTab is { } tab)
                (TabStrip.ItemContainerGenerator.ContainerFromItem(tab) as FrameworkElement)?.BringIntoView();
        }));
    }
    private void NewConnection_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        var profile = _viewModel.CreateProfileDraft();
        new ConnectionProfileWindow(_viewModel, profile, true) { Owner = Window.GetWindow(this) }.ShowDialog();
    }
    private void SelectTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: { } tab }) _viewModel?.SelectTab(tab);
    }
    private async void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: { } tab }) await CloseTabObjectAsync(tab);
    }
    private void NewLocalShell_Click(object sender, RoutedEventArgs e) => _viewModel?.AddLocalShell();
    internal bool CanAcceptDirectoryDrop => IsVisible && IsEnabled && _viewModel is { } vm
        && (vm.IsLocalShellSelected || !vm.HasTabs);

    internal void ShowDirectoryDropError(string message)
    {
        _viewModel?.ReportDirectoryDropStatus(message);
    }

    internal void OpenDroppedDirectories(IEnumerable<string> paths)
    {
        if (!CanAcceptDirectoryDrop) return;
        try
        {
            var directories = new LocalShellStartupOptions(paths).Directories;
            foreach (var directory in directories) _viewModel!.AddLocalShell(directory);
            if (directories.Count > 0) ShowDirectoryDropError("已在拖入的目录打开本地 Shell");
        }
        catch (Exception ex) when (ex is ArgumentException or System.IO.IOException or UnauthorizedAccessException)
        { ShowDirectoryDropError("打开目录失败：" + ex.Message); }
    }

    private void LocalShell_DirectoriesDropped(object? sender, string[] paths) => OpenDroppedDirectories(paths);

    private void Directory_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return; // Leave tab reordering and internal drags intact.
        e.Effects = CanAcceptDirectoryDrop && (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Directory_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Handled = true;
        if (CanAcceptDirectoryDrop && e.Data.GetData(DataFormats.FileDrop) is string[] paths) OpenDroppedDirectories(paths);
    }
    private void TabMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || menu.PlacementTarget is not FrameworkElement { DataContext: { } tab }) return;
        _tabDragStart = null;
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            if (item.Tag is not string tag) continue;
            item.IsEnabled = tag switch
            {
                "duplicate" => tab is not RemoteFileDocumentViewModel,
                "rename" => tab is IRemoteTabViewModel,
                "close" => tab is not RemoteFileDocumentViewModel { IsSaving: true },
                _ => true
            };
        }
    }
    private async void TabMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not MenuItem { Tag: string action } item
            || ItemsControl.ItemsControlFromItemContainer(item) is not ContextMenu { PlacementTarget: FrameworkElement { DataContext: { } tab } }) return;
        if (action == "duplicate") await _viewModel.DuplicateTabAsync(tab);
        else if (action == "rename") Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() => BeginTabRename(tab)));
        else if (action == "close") await CloseTabObjectAsync(tab);
    }
    private async Task CloseTabObjectAsync(object tab)
    {
        if (_viewModel is null || !_viewModel.Tabs.Contains(tab) || tab is RemoteFileDocumentViewModel { IsSaving: true }) return;
        var title = tab is IRemoteTabViewModel remoteTab ? remoteTab.TabTitle : tab.ToString() ?? "标签";
        var detail = tab switch
        {
            LocalShellViewModel => "将结束该本地终端进程。",
            RemoteWorkspaceViewModel => "当前远程连接将断开。",
            DesktopSessionViewModel => "当前远程桌面连接将断开。",
            S3BrowserViewModel => "正在进行的传输将取消。",
            RemoteFileDocumentViewModel document when document.IsDirty => "该文件有未保存的修改，关闭将放弃这些修改。",
            _ => string.Empty
        };
        if (tab is RemoteWorkspaceViewModel session && _viewModel.Documents.Any(document => document.Owner == session && document.IsDirty))
            detail += "\n该会话有未保存的文件修改，关闭将放弃这些修改。";
        if (new ConfirmationWindow("关闭标签", $"确定关闭“{title}”吗？\n{detail}", "关闭标签") { Owner = Window.GetWindow(this) }.ShowDialog() == true)
            await _viewModel.CloseTabAsync(tab);
    }
    private void BeginTabRename(object tab)
    {
        if (_viewModel is null || !_viewModel.Tabs.Contains(tab) || tab is not IRemoteTabViewModel remoteTab) return;
        FinishTabRename();
        var container = TabStrip.ItemContainerGenerator.ContainerFromItem(tab) as DependencyObject;
        var editor = FindVisualChild<TextBox>(container);
        if (editor is null) return;
        _renameTab = tab; _renameEditor = editor;
        editor.Text = !string.IsNullOrEmpty(remoteTab.CustomTitle) ? remoteTab.CustomTitle
            : tab is RemoteFileDocumentViewModel document ? document.Name : remoteTab.TabTitle;
        editor.Tag = editor.Text;
        _tabDragStart = null;
        editor.Visibility = Visibility.Visible;
        editor.BringIntoView();
        editor.Focus(); editor.SelectAll();
    }
    private void TabTitleEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape)) return;
        e.Handled = true;
        FinishTabRename(e.Key == Key.Enter);
    }
    private void TabTitleEditor_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => FinishTabRename();
    private void FinishTabRename(bool commit = true)
    {
        var editor = _renameEditor; var tab = _renameTab;
        _renameEditor = null; _renameTab = null;
        if (editor is null) return;
        if (commit && tab is not null && editor.Text != (string?)editor.Tag && _viewModel is not null) _viewModel.SetTabTitle(tab, editor.Text);
        editor.Visibility = Visibility.Collapsed;
    }
    private static T? FindVisualChild<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null) return null;
        if (root is T match) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindVisualChild<T>(VisualTreeHelper.GetChild(root, i)) is { } child) return child;
        return null;
    }
    private void Tab_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _tabDragStart = null;
        for (var element = e.OriginalSource as DependencyObject; element is not null && element != sender; element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            if (element is Button { Tag: "CloseTab" } or TextBox) return;
        if (sender is FrameworkElement { DataContext: { } tab }) { _tabDragStart = e.GetPosition(this); _draggedTab = tab; }
    }
    private void Tab_MouseMove(object sender, MouseEventArgs e)
    {
        if (_tabDragStart is not { } start || _draggedTab is null || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(this);
        if (Math.Abs(point.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _tabDragStart = null;
        try { DragDrop.DoDragDrop((DependencyObject)sender, new DataObject("EmbedDesk.WorkspaceTab", _draggedTab), DragDropEffects.Move); }
        finally { ClearDropIndicator(); _draggedTab = null; }
    }
    private object? DraggedTab(DragEventArgs e)
    {
        var tab = e.Data.GetData("EmbedDesk.WorkspaceTab");
        return tab is not null && _viewModel?.Tabs.Contains(tab) == true ? tab : null;
    }
    private void Tab_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not Border border || DraggedTab(e) is null) return;
        ClearDropIndicator(); _dropIndicator = border;
        border.BorderBrush = (Brush)FindResource("AccentBrush");
        border.BorderThickness = e.GetPosition(border).X < border.ActualWidth / 2 ? new Thickness(2, 0, 0, 0) : new Thickness(0, 0, 2, 0);
        ScrollTabDrag(e);
        e.Effects = DragDropEffects.Move; e.Handled = true;
    }
    private void Tab_DragLeave(object sender, DragEventArgs e) => ClearDropIndicator();
    private void Tab_Drop(object sender, DragEventArgs e)
    {
        if (sender is Border { DataContext: { } target } border && DraggedTab(e) is { } tab)
        {
            _viewModel!.MoveTab(tab, target, e.GetPosition(border).X >= border.ActualWidth / 2);
            e.Handled = true;
        }
        ClearDropIndicator();
    }
    private void Tabs_DragOver(object sender, DragEventArgs e) { e.Effects = DraggedTab(e) is not null ? DragDropEffects.Move : DragDropEffects.None; ScrollTabDrag(e); e.Handled = true; }
    private void Tabs_Drop(object sender, DragEventArgs e)
    {
        if (DraggedTab(e) is { } tab && _viewModel?.Tabs.LastOrDefault() is { } last) _viewModel.MoveTab(tab, last, true);
        ClearDropIndicator(); e.Handled = true;
    }
    private void ScrollTabDrag(DragEventArgs e)
    {
        var x = e.GetPosition(TabScroller).X;
        if (x < 28) TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset - 16);
        else if (x > TabScroller.ActualWidth - 28) TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset + 16);
    }
    private void ClearDropIndicator()
    {
        if (_dropIndicator is not null) { _dropIndicator.BorderBrush = (Brush)FindResource("LineBrush"); _dropIndicator.BorderThickness = new Thickness(1); _dropIndicator = null; }
    }
    private void Tabs_MouseWheel(object sender, MouseWheelEventArgs e) { TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset - e.Delta); e.Handled = true; }
    private void EditConnection_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedProfile is not { } profile) return;
        new ConnectionProfileWindow(_viewModel, profile, false) { Owner = Window.GetWindow(this) }.ShowDialog();
    }
    private void DeleteConnection_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedProfile is not { } profile || !_viewModel.DeleteProfileCommand.CanExecute(null)) return;
        var confirmation = new ConfirmationWindow(
            "删除连接配置",
            $"确定删除连接配置“{profile.Name}”吗？此操作无法撤销。",
            "删除配置") { Owner = Window.GetWindow(this) };
        if (confirmation.ShowDialog() != true) return;
        if (ReferenceEquals(_viewModel.SelectedProfile, profile)) _viewModel.DeleteProfileCommand.Execute(null);
    }
    private async void Profile_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox list && ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) is ListBoxItem { DataContext: RemoteConnectionProfile profile } && _viewModel is not null)
            await _viewModel.OpenProfileInNewTabAsync(profile);
    }
    private void KeyManager_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        new SshKeyManagerWindow(_viewModel) { Owner = Window.GetWindow(this) }.ShowDialog();
    }
    private async void LocalShell_InputReceived(object? sender, string text)
    {
        if (sender is ShellTerminal { DataContext: LocalShellViewModel shell }) await shell.SendAsync(text);
    }
    private void ToggleLocalFullscreen_Click(object? sender, EventArgs e)
    {
        if (sender is ShellTerminal { DataContext: LocalShellViewModel shell } && _viewModel is not null)
        {
            var fullscreen = !_viewModel.IsLocalShellFullscreen;
            if (!ReferenceEquals(_viewModel.ActiveTab, shell)) _viewModel.SelectTab(shell);
            _viewModel.IsLocalShellFullscreen = fullscreen;
        }
    }
}

public sealed class RemoteAuthenticationLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is RemoteAuthenticationMode mode ? mode == RemoteAuthenticationMode.PrivateKey ? "SSH 私钥" : "密码" : value?.ToString() ?? string.Empty;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class ActiveTabBrushConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => Application.Current.FindResource(values.Length >= 2 && ReferenceEquals(values[0], values[1]) ? "SelectedBrush" : "SurfaceBrush");
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
public sealed class ActiveTabVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => values.Length >= 2 && ReferenceEquals(values[0], values[1]) ? Visibility.Visible : Visibility.Collapsed;
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
public sealed class BooleanToGridLengthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var expanded = value is bool flag && flag;
        if (parameter is string text && double.TryParse(text, out var width))
            return new GridLength(expanded ? width : 0);
        return new GridLength(expanded ? 1 : 0, expanded ? GridUnitType.Star : GridUnitType.Pixel);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBooleanToGridLengthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (string.Equals(parameter?.ToString(), "Auto", StringComparison.OrdinalIgnoreCase))
            return value is bool hidden && hidden ? new GridLength(0) : GridLength.Auto;
        var width = double.TryParse(parameter?.ToString(), out var parsed) ? parsed : 1;
        return new GridLength(value is bool flag && flag ? 0 : width, GridUnitType.Pixel);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class ConnectionStateBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value?.ToString() switch
        {
            "已连接" or "监听中" => "#4CC9B0",
            "连接中" => "#E0B15A",
            "连接失败" or "已断开" => "#E36D78",
            _ => "#7F8992"
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class HostPlatformIconConverter : IValueConverter
{
    private static readonly IReadOnlyDictionary<string, BitmapImage> Icons = new[]
    {
        "ubuntu", "debian", "windows11", "alpine", "archlinux", "fedora", "centos",
        "redhat", "rockylinux", "opensuse", "apple", "freebsd", "linux", "unknown"
    }.ToDictionary(name => name, name =>
    {
        var image = new BitmapImage(new Uri($"pack://application:,,,/EmbedDesk.App;component/Assets/Platforms/{name}.png"));
        image.Freeze();
        return image;
    });

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var platform = value?.ToString()?.Trim().ToLowerInvariant() ?? string.Empty;
        var icon = platform switch
        {
            "" or "unknown" => "unknown",
            "ubuntu" => "ubuntu",
            "debian" => "debian",
            var p when p.Contains("windows", StringComparison.Ordinal) => "windows11",
            "alpine" => "alpine",
            "arch" or "archlinux" => "archlinux",
            "fedora" => "fedora",
            "centos" => "centos",
            "rhel" or "redhat" => "redhat",
            "rocky" or "rockylinux" => "rockylinux",
            var p when p.StartsWith("opensuse", StringComparison.Ordinal) || p == "sles" => "opensuse",
            "darwin" or "macos" => "apple",
            "freebsd" => "freebsd",
            _ => "linux"
        };
        return Icons[icon];
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class ProfileGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is RemoteProtocol.Rdp or RemoteProtocol.Vnc ? "\uE7F4" : value is RemoteProtocol.S3 ? "\uE753" : "\uE756";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
