using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using EmbeddedDebugger.App.ViewModels;

namespace EmbeddedDebugger.App;

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

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Max(MinWidth, workArea.Width - 32));
        Height = Math.Min(Height, Math.Max(MinHeight, workArea.Height - 32));
        WindowTheme.ApplyDarkTitleBar(this);
        DataContext = viewModel;
        if (viewModel.RemoteDebug is { } remote)
        {
            remote.PropertyChanged += Remote_PropertyChanged;
            Closed += (_, _) => remote.PropertyChanged -= Remote_PropertyChanged;
        }
    }

    private void Remote_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
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
                if (remote.IsLocalShellSelected) remote.IsLocalShellFullscreen = !remote.IsLocalShellFullscreen;
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
            if (DataContext is MainWindowViewModel { RemoteDebug: { } remote } && remote.Documents.Any(document => document.IsDirty)
                && new ConfirmationWindow("退出程序", "有远程文件尚未保存，确定放弃修改并退出吗？", "放弃并退出") { Owner = this }.ShowDialog() != true) return;
            _closing = true;
            IsEnabled = false;
            try
            {
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
