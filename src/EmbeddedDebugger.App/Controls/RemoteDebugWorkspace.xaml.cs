using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EmbeddedDebugger.App.ViewModels;
using EmbeddedDebugger.Core.Models;

namespace EmbeddedDebugger.App.Controls;

public partial class RemoteDebugWorkspace : UserControl
{
    private RemoteDebugWorkspaceViewModel? _viewModel;
    private Point? _tabDragStart;
    private object? _draggedTab;
    private Border? _dropIndicator;
    public RemoteDebugWorkspace()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => _viewModel = DataContext as RemoteDebugWorkspaceViewModel;
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
    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RemoteWorkspaceViewModel }) CloseSession_Click(sender, e);
        else if (sender is FrameworkElement { DataContext: RemoteFileDocumentViewModel }) CloseDocument_Click(sender, e);
        else if (sender is FrameworkElement { DataContext: DesktopSessionViewModel desktop } && _viewModel is not null
            && new ConfirmationWindow("关闭会话", $"确定关闭“{desktop.DisplayTitle}”并断开远程桌面吗？", "关闭会话") { Owner = Window.GetWindow(this) }.ShowDialog() == true)
            _viewModel.CloseDesktop(desktop);
    }
    private void Tab_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _tabDragStart = null;
        for (var element = e.OriginalSource as DependencyObject; element is not null && element != sender; element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            if (element is Button { Tag: "CloseTab" }) return;
        if (sender is FrameworkElement { DataContext: { } tab }) { _tabDragStart = e.GetPosition(this); _draggedTab = tab; }
    }
    private void Tab_MouseMove(object sender, MouseEventArgs e)
    {
        if (_tabDragStart is not { } start || _draggedTab is null || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(this);
        if (Math.Abs(point.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _tabDragStart = null;
        try { DragDrop.DoDragDrop((DependencyObject)sender, new DataObject("EmbeddedDebugger.WorkspaceTab", _draggedTab), DragDropEffects.Move); }
        finally { ClearDropIndicator(); _draggedTab = null; }
    }
    private object? DraggedTab(DragEventArgs e)
    {
        var tab = e.Data.GetData("EmbeddedDebugger.WorkspaceTab");
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
    private async void CloseSession_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not FrameworkElement { DataContext: RemoteWorkspaceViewModel session }) return;
        var confirmation = new ConfirmationWindow(
            "关闭会话",
            $"确定关闭“{session.DisplayTitle}”吗？当前远程连接将断开。" + (_viewModel.Documents.Any(document => document.Owner == session && document.IsDirty) ? "\n该会话有未保存的文件修改，关闭将放弃这些修改。" : string.Empty),
            "关闭会话") { Owner = Window.GetWindow(this) };
        if (confirmation.ShowDialog() == true && _viewModel.Sessions.Contains(session))
            await _viewModel.CloseSessionCommand.ExecuteAsync(session);
    }
    private async void Profile_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox list && ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) is ListBoxItem { DataContext: RemoteConnectionProfile profile } && _viewModel is not null)
            await _viewModel.OpenProfileInNewTabAsync(profile);
    }
    private void CloseDocument_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not FrameworkElement { DataContext: RemoteFileDocumentViewModel document } || document.IsSaving) return;
        if (document.IsDirty && new ConfirmationWindow("关闭文件", $"“{document.Name}”有未保存的修改，确定放弃修改并关闭吗？", "放弃修改") { Owner = Window.GetWindow(this) }.ShowDialog() != true) return;
        _viewModel.CloseDocument(document);
    }
    private void KeyManager_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        new SshKeyManagerWindow(_viewModel) { Owner = Window.GetWindow(this) }.ShowDialog();
    }
    private async void LocalShell_InputReceived(object? sender, string text)
    {
        if (_viewModel is not null) await _viewModel.LocalShell.SendAsync(text);
    }
    private async void LocalShell_ResizeReceived(object? sender, TerminalSize size)
    {
        if (_viewModel is not null) await _viewModel.LocalShell.ResizeAsync(size.Columns, size.Rows);
    }

    private void ToggleLocalFullscreen_Click(object? sender, EventArgs e)
    {
        if (_viewModel is null) return;
        _viewModel.IsLocalShellFullscreen = !_viewModel.IsLocalShellFullscreen;
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
public sealed class TabCloseVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is LocalShellViewModel ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
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
        var image = new BitmapImage(new Uri($"pack://application:,,,/EmbeddedDebugger.App;component/Assets/Platforms/{name}.png"));
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
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is RemoteProtocol.Rdp or RemoteProtocol.Vnc ? "\uE7F4" : "\uE756";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
