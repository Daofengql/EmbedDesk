using System.ComponentModel;
using System.Windows;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Interop;
using EmbedDesk.App.ViewModels;

namespace EmbedDesk.App;

public partial class MainWindow : Window
{
    private bool _closing;
    private bool _cleanupComplete;
    private LogWindow? _logWindow;
    private bool _terminalFullscreen;
    private WindowState _windowStateBeforeFullscreen;
    private WindowStyle _windowStyleBeforeFullscreen;
    private ResizeMode _resizeModeBeforeFullscreen;
    private readonly List<FilePaneWindow> _fullscreenFileWindows = [];
    private NativeDirectoryDropTarget? _directoryDrop;
    private System.Windows.Forms.NotifyIcon? _tray;
    private bool _explicitExit;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        var settings = viewModel.AppSettings.Snapshot();
        var workArea = SystemParameters.WorkArea;
        Width = Math.Clamp(settings.WindowWidth, MinWidth, Math.Max(MinWidth, workArea.Width - 32));
        Height = Math.Clamp(settings.WindowHeight, MinHeight, Math.Max(MinHeight, workArea.Height - 32));
        MainWorkspaceTabs.SelectedIndex = Math.Clamp(settings.LastWorkspaceIndex, 0, MainWorkspaceTabs.Items.Count - 1);
        if (settings.WindowLeft is { } left && settings.WindowTop is { } top && IsOnVisibleScreen(left, top, Width, Height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        WindowState = settings.WindowState;
        LocationChanged += (_, _) => SaveWindowPreferences(viewModel.AppSettings);
        SizeChanged += (_, _) => SaveWindowPreferences(viewModel.AppSettings);
        StateChanged += (_, _) => SaveWindowPreferences(viewModel.AppSettings);
        WindowTheme.ApplyDarkTitleBar(this);
        DataContext = viewModel;
        SourceInitialized += (_, _) =>
        {
            _directoryDrop = new NativeDirectoryDropTarget(
                paths => Dispatcher.BeginInvoke(new Action(() => RemoteArea.OpenDroppedDirectories(paths))),
                message => Dispatcher.BeginInvoke(new Action(() => RemoteArea.ShowDirectoryDropError(message))));
            try { _directoryDrop.Attach(new WindowInteropHelper(this).Handle); UpdateDirectoryDrop(); }
            catch (Win32Exception ex) { RemoteArea.ShowDirectoryDropError("目录拖放初始化失败：" + ex.Message); }
        };
        RemoteArea.IsVisibleChanged += (_, _) => UpdateDirectoryDrop();
        Closed += (_, _) => { _directoryDrop?.Dispose(); _tray?.Dispose(); viewModel.Connections.Node.ApprovalRequested -= OnApprovalRequested; };
        viewModel.Connections.Node.ApprovalRequested += OnApprovalRequested;
        if (viewModel.RemoteDebug is { } remote)
        {
            remote.PropertyChanged += Remote_PropertyChanged;
            Closed += (_, _) => remote.PropertyChanged -= Remote_PropertyChanged;
        }
    }

    private void Remote_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateDirectoryDrop();
        if (e.PropertyName != nameof(RemoteDebugWorkspaceViewModel.IsTerminalFullscreen)
            || sender is not RemoteDebugWorkspaceViewModel remote || _terminalFullscreen == remote.IsTerminalFullscreen) return;
        _terminalFullscreen = remote.IsTerminalFullscreen;
        if (_terminalFullscreen)
        {
            foreach (var files in OwnedWindows.OfType<FilePaneWindow>().Where(files => files.IsVisible).ToArray())
            {
                _fullscreenFileWindows.Add(files);
                files.Hide();
            }
            _windowStateBeforeFullscreen = WindowState;
            _windowStyleBeforeFullscreen = WindowStyle;
            _resizeModeBeforeFullscreen = ResizeMode;
            // Changing chrome on the same window keeps the live WebViews and PTYs intact.
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowState = WindowState.Normal;
            WindowStyle = _windowStyleBeforeFullscreen;
            ResizeMode = _resizeModeBeforeFullscreen;
            WindowState = _windowStateBeforeFullscreen;
            foreach (var files in _fullscreenFileWindows.Where(files => OwnedWindows.Cast<Window>().Contains(files)))
            {
                var activate = files.ShowActivated;
                files.ShowActivated = false;
                files.Show();
                files.ShowActivated = activate;
            }
            _fullscreenFileWindows.Clear();
        }
    }

    internal void ShowLocalShellWorkspace() => MainWorkspaceTabs.SelectedIndex = 2;
    internal void ShowConnectionCenter() => MainWorkspaceTabs.SelectedIndex = 0;
    internal void ShowRemoteSerial(EmbedDesk.Infrastructure.Connections.DeviceRemoteService service)
    {
        if (DataContext is not MainWindowViewModel model) return;
        var row = model.SerialPorts.FirstOrDefault(p => p.PortName == service.PortName)
            ?? throw new InvalidOperationException("此远程串口已离线，请重新连接设备。");
        var session = model.SerialSessions.FirstOrDefault(s => s.SelectedSerialPort?.PortName == row.PortName) ?? model.AddSerialSession();
        session.SelectedSerialPort = session.SerialPorts.First(p => p.PortName == row.PortName);
        model.SelectedSerialSession = session;
        MainWorkspaceTabs.SelectedIndex = 1;
    }
    private void OpenSettings_Click(object sender, RoutedEventArgs e) => ShowSettings();
    private void ShowSettings() { if (DataContext is MainWindowViewModel model) new SettingsWindow(model) { Owner = this }.ShowDialog(); }
    internal void HideToTray()
    {
        if (_tray is null)
        {
            using var resource = Application.GetResourceStream(new Uri("pack://application:,,,/EmbedDesk.App;component/Assets/embeddesk.ico"))!.Stream;
            using var icon = new System.Drawing.Icon(resource);
            _tray = new System.Windows.Forms.NotifyIcon { Icon = (System.Drawing.Icon)icon.Clone(), Text = "EmbedDesk", Visible = true };
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("显示主窗口", null, (_, _) => Dispatcher.BeginInvoke(new Action(RestoreFromTray)));
            menu.Items.Add("设置", null, (_, _) => Dispatcher.BeginInvoke(new Action(() => { RestoreFromTray(); ShowSettings(); })));
            menu.Items.Add("退出程序", null, (_, _) => Dispatcher.BeginInvoke(new Action(() => { _explicitExit = true; Close(); })));
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(RestoreFromTray));
            _tray.BalloonTipClicked += (_, _) => Dispatcher.BeginInvoke(new Action(() => { RestoreFromTray(); ShowConnectionCenter(); }));
        }
        _tray.Visible = true; Hide();
    }
    internal void RestoreFromTray() { Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); }
    private void OnApprovalRequested(object? sender, EmbedDesk.Infrastructure.Connections.DeviceApproval approval) => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (!IsVisible && _tray is not null) { _tray.BalloonTipTitle = "设备请求授权"; _tray.BalloonTipText = $"设备 {approval.DeviceId} 请求连接，点击处理。"; _tray.ShowBalloonTip(5000); }
    }));

    private void UpdateDirectoryDrop()
    {
        if (_directoryDrop is not null) _directoryDrop.Enabled = RemoteArea.CanAcceptDirectoryDrop;
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        if (_logWindow is null)
        {
            _logWindow = new LogWindow { Owner = this, DataContext = DataContext };
            _logWindow.Closed += (_, _) => _logWindow = null;
            _logWindow.Show();
        }
        else _logWindow.Activate();
    }

    private void OpenConfiguration_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;
        new ConfigurationTransferWindow(viewModel) { Owner = this }.ShowDialog();
    }

    private void OpenAbout_Click(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = this }.ShowDialog();
    }

    private void OpenRepository_Click(object sender, RoutedEventArgs e)
    {
        const string repositoryUrl = "https://github.com/Daofengql/EmbedDesk";
        try
        {
            Process.Start(new ProcessStartInfo(repositoryUrl) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, $"无法打开默认浏览器，请手动访问：\n{repositoryUrl}", "打开开源仓库", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void MainWorkspaceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(sender, MainWorkspaceTabs) && DataContext is MainWindowViewModel viewModel)
            viewModel.AppSettings.Update(settings => settings.LastWorkspaceIndex = MainWorkspaceTabs.SelectedIndex);
    }

    private void SaveWindowPreferences(AppSettingsStore settingsStore)
    {
        if (_cleanupComplete || DataContext is not MainWindowViewModel) return;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        settingsStore.Update(settings =>
        {
            settings.WindowWidth = bounds.Width > 0 ? bounds.Width : Width;
            settings.WindowHeight = bounds.Height > 0 ? bounds.Height : Height;
            settings.WindowLeft = bounds.Left;
            settings.WindowTop = bounds.Top;
            settings.WindowState = WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        });
    }

    private static bool IsOnVisibleScreen(double left, double top, double width, double height)
    {
        var window = new Rect(left, top, width, height);
        return System.Windows.Forms.Screen.AllScreens.Any(screen =>
        {
            var area = screen.WorkingArea;
            var visible = new Rect(area.Left, area.Top, area.Width, area.Height);
            var intersection = Rect.Intersect(window, visible);
            return !intersection.IsEmpty && intersection.Width >= 100 && intersection.Height >= 100;
        });
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F11 && RemoteArea.IsVisible
            && DataContext is MainWindowViewModel { RemoteDebug: { IsDocumentSelected: false } remote })
        {
            // Chrome changes can move native focus back to the window. F11 must
            // keep working even before the user clicks inside the terminal again.
            e.Handled = true;
            if (!e.IsRepeat)
            {
                if (remote.SelectedDesktop is { } desktop) desktop.IsFullscreen = !desktop.IsFullscreen;
                else if (remote.IsLocalShellSelected) remote.IsLocalShellFullscreen = !remote.IsLocalShellFullscreen;
                else if (remote.SelectedSession is { IsWorkspaceReady: true } session)
                    session.IsTerminalFullscreen = !session.IsTerminalFullscreen;
            }
        }
        base.OnPreviewKeyDown(e);
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (!_cleanupComplete)
        {
            e.Cancel = true;
            base.OnClosing(e);
            if (_closing) return;
            if (!_explicitExit && Application.Current is App && DataContext is MainWindowViewModel closeModel)
            {
                var choice = closeModel.AppSettings.Snapshot().ConnectionPreferences.CloseAction;
                if (choice == MainWindowCloseAction.Ask)
                {
                    var dialog = new CloseChoiceWindow { Owner = this };
                    if (dialog.ShowDialog() != true) return;
                    choice = dialog.Choice;
                    if (dialog.Remember) closeModel.AppSettings.Update(s => s.ConnectionPreferences = s.ConnectionPreferences with { CloseAction = choice });
                }
                if (choice == MainWindowCloseAction.Tray) { closeModel.AppSettings.SaveNow(); HideToTray(); return; }
            }
            _explicitExit = false;
            if (Application.Current is App && DataContext is MainWindowViewModel liveModel
                && (liveModel.SerialSessions.Any(s => s.IsOpen || s.IsBusy) || liveModel.Sessions.Any(s => !s.CanStart)
                    || liveModel.Connections.Node.Peers.Count > 0 || liveModel.Connections.IsConnecting
                    || DebugAssistant.HasActiveWork
                    || liveModel.RemoteDebug is { } activeRemote && (activeRemote.HasSessions
                        || activeRemote.DesktopSessions.Any(s => s.IsConnected || s.IsConnecting)
                        || activeRemote.S3Sessions.Any(s => s.IsBusy)
                        || activeRemote.LocalShells.Any(s => s.IsRunning))
                    || OwnedWindows.OfType<LanFileShareWindow>().Any(w => w.HasActiveWork)
                    || OwnedWindows.OfType<IDebugToolWindow>().Any(w => w.HasActiveWork))
                && new ConfirmationWindow("退出程序", "退出将结束当前连接、串口、映射和后台任务，确定退出吗？", "退出程序") { Owner = this }.ShowDialog() != true) return;
            if (DataContext is MainWindowViewModel { RemoteDebug: { } remote } && remote.Documents.Any(document => document.IsDirty)
                && new ConfirmationWindow("退出程序", "有远程文件尚未保存，确定放弃修改并退出吗？", "放弃并退出") { Owner = this }.ShowDialog() != true) return;
            if (OwnedWindows.OfType<IDebugToolWindow>().Any(w => w.HasUnsavedChanges)
                && new ConfirmationWindow("退出程序", "二进制编辑器有未保存修改，确定放弃并退出吗？", "放弃并退出") { Owner = this }.ShowDialog() != true) return;
            if (DataContext is MainWindowViewModel viewModel) viewModel.AppSettings.SaveNow();
            _closing = true;
            IsEnabled = false;
            try
            {
                foreach (var files in OwnedWindows.OfType<LanFileShareWindow>().ToArray()) await files.ShutdownAsync();
                await DebugAssistant.ShutdownAsync();
                foreach (var tool in OwnedWindows.OfType<IDebugToolWindow>().ToArray()) await tool.ShutdownAsync();
                if (Application.Current is App app) await app.StopAsync();
            }
            finally
            {
                _cleanupComplete = true;
                _ = Dispatcher.BeginInvoke(new Action(Close));
            }
            return;
        }
        base.OnClosing(e);
    }
}
