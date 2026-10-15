using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using EmbedDesk.App.ViewModels;
using EmbedDesk.Core.Models;
using EmbedDesk.Infrastructure.Diagnostics;

namespace EmbedDesk.App.Controls;

public partial class DebugAssistantWorkspace : UserControl
{
    private MainWindowViewModel? _vm;
    private readonly Dictionary<object, DebugSessionTab> _byOwner = [];
    private readonly Dictionary<DebugSessionTab, PropertyChangedEventHandler> _subscriptions = [];
    private Point? _dragStart;
    private TextBox? _renameEditor;
    private DebugSessionTab? _selectedTab;
    private bool _closing;
    private bool _selecting;
    private Task? _shutdown;
    private Task? _dialogOperation;
    private DebugSessionTab? _editingTab;
    private IInputElement? _previousFocus;
    internal DebugSessionDialog? SessionDialog => DialogHost.Content as DebugSessionDialog;

    public ObservableCollection<DebugSessionTab> Tabs { get; } = [];

    public bool HasActiveWork => Tabs.Any(tab => tab.Owner switch
    {
        SerialSessionViewModel serial => serial.IsOpen || serial.IsBusy,
        NetworkSessionViewModel network => !network.CanStart,
        PidDebugView pid => pid.HasActiveWork,
        CameraDebugView camera => camera.HasActiveWork,
        _ => false
    });

    public bool HasUnsavedChanges => Tabs.Any(tab => tab.Owner switch
    {
        PidDebugView pid => pid.HasUnsavedChanges,
        CameraDebugView camera => camera.HasUnsavedChanges,
        _ => false
    });

    public DebugAssistantWorkspace()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => { SyncSerialSessions(); SyncNetworkSessions(); ShowSelectedTab(); };
    }

    private void OnDataContextChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.SerialSessions.CollectionChanged -= SerialSessions_Changed;
            _vm.Sessions.CollectionChanged -= NetworkSessions_Changed;
            _vm.PropertyChanged -= MainViewModelChanged;
        }
        _vm = DataContext as MainWindowViewModel;
        if (_vm is not null)
        {
            _vm.SerialSessions.CollectionChanged += SerialSessions_Changed;
            _vm.Sessions.CollectionChanged += NetworkSessions_Changed;
            _vm.PropertyChanged += MainViewModelChanged;
            SyncSerialSessions();
            SyncNetworkSessions();
        }
        ShowSelectedTab();
    }

    private void MainViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_selecting || _closing) return;
        object? selected = e.PropertyName switch
        {
            nameof(MainWindowViewModel.SelectedSerialSession) => _vm?.SelectedSerialSession,
            nameof(MainWindowViewModel.SelectedSession) => _vm?.SelectedSession,
            _ => null
        };
        if (selected is not null && _byOwner.TryGetValue(selected, out var tab)) SelectTab(tab);
    }

    private void SerialSessions_Changed(object? sender, NotifyCollectionChangedEventArgs e)
    { if (!_closing) SyncSerialSessions(); }

    private void NetworkSessions_Changed(object? sender, NotifyCollectionChangedEventArgs e)
    { if (!_closing) SyncNetworkSessions(); }

    private void SyncSerialSessions()
    {
        if (_vm is null) return;
        foreach (var session in _vm.SerialSessions)
            if (!_byOwner.ContainsKey(session))
                AddTab("串口", session.Title, session, new SerialWorkspace { DataContext = session });
        foreach (var tab in Tabs.Where(tab => tab.Owner is SerialSessionViewModel serial && !_vm.SerialSessions.Contains(serial)).ToArray())
            RemoveTabVisual(tab);
        if (_selectedTab is null) ShowSelectedTab();
        UpdateEmptyState();
    }

    private void SyncNetworkSessions()
    {
        if (_vm is null) return;
        foreach (var session in _vm.Sessions)
            if (!_byOwner.ContainsKey(session))
                AddTab(session.TreatAsHttp ? $"HTTP {session.Mode}" : $"{session.Protocol} {session.Mode}", session.Endpoint, session, new NetworkWorkspace(session) { DataContext = _vm });
        foreach (var tab in Tabs.Where(tab => tab.Owner is NetworkSessionViewModel network && !_vm.Sessions.Contains(network)).ToArray())
            RemoveTabVisual(tab);
        UpdateEmptyState();
        if (_selectedTab is null) ShowSelectedTab();
    }

    private DebugSessionTab AddTab(string kind, string title, object owner, FrameworkElement content)
    {
        var tab = new DebugSessionTab(kind, title, owner, content);
        Tabs.Add(tab);
        _byOwner[owner] = tab;
        ContentHost.Children.Add(content);
        content.Visibility = Visibility.Collapsed;
        PropertyChangedEventHandler handler = (_, args) => OwnerChanged(tab, args);
        _subscriptions[tab] = handler;
        if (owner is INotifyPropertyChanged notify) notify.PropertyChanged += handler;
        if (owner is PidDebugView pid) pid.ActivityChanged += ToolActivityChanged;
        if (owner is CameraDebugView camera) camera.ActivityChanged += ToolActivityChanged;
        OwnerChanged(tab, new(null));
        return tab;
    }

    private void OwnerChanged(DebugSessionTab tab, PropertyChangedEventArgs args)
    {
        if (tab.Owner is SerialSessionViewModel serial)
        {
            tab.Name = serial.SelectedSerialPort is null && string.IsNullOrEmpty(serial.CustomTitle) ? serial.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) : serial.Title;
            tab.Status = serial.StatusLabel;
        }
        else if (tab.Owner is NetworkSessionViewModel network)
        {
            if (!tab.HasCustomName) tab.Name = network.Endpoint;
            tab.Status = network.State;
        }
        if (ReferenceEquals(_selectedTab, tab) && args.PropertyName is null or ""
            or nameof(SerialSessionViewModel.SelectedSerialPort) or nameof(SerialSessionViewModel.CanConfigure)
            or nameof(NetworkSessionViewModel.Endpoint) or nameof(NetworkSessionViewModel.State)
            or nameof(NetworkSessionViewModel.CanStart) or nameof(NetworkSessionViewModel.IsSending))
            RefreshConnectionActions();
    }

    private void ToolActivityChanged(object? sender, EventArgs e)
    {
        var tab = Tabs.FirstOrDefault(item => ReferenceEquals(item.Owner, sender));
        if (tab is not null)
        {
            tab.Status = sender switch
            {
                PidDebugView pid => pid.ConnectionStatus,
                CameraDebugView camera => camera.ConnectionStatus,
                _ => "未连接"
            };
            if (ReferenceEquals(_selectedTab, tab)) RefreshConnectionActions();
        }
    }

    private void RemoveTabVisual(DebugSessionTab tab)
    {
        FinishRename();
        if (_subscriptions.Remove(tab, out var handler) && tab.Owner is INotifyPropertyChanged notify) notify.PropertyChanged -= handler;
        if (tab.Owner is PidDebugView pid) pid.ActivityChanged -= ToolActivityChanged;
        if (tab.Owner is CameraDebugView camera) camera.ActivityChanged -= ToolActivityChanged;
        if (tab.Content is SerialWorkspace serial) serial.DisposeView();
        if (tab.Content is NetworkWorkspace network) network.DisposeView();
        ContentHost.Children.Remove(tab.Content);
        Tabs.Remove(tab);
        _byOwner.Remove(tab.Owner);
        if (ReferenceEquals(_selectedTab, tab)) _selectedTab = null;
    }

    internal void SelectTab(DebugSessionTab? tab)
    {
        if (tab is null || !Tabs.Contains(tab) || tab.IsClosing) { UpdateEmptyState(); return; }
        if (!ReferenceEquals(_selectedTab, tab)) FinishRename();
        _selectedTab = tab;
        foreach (var candidate in Tabs) candidate.IsSelected = ReferenceEquals(candidate, tab);
        foreach (var candidate in Tabs) candidate.Content.Visibility = ReferenceEquals(candidate, tab) ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Collapsed;
        WorkspaceMessageBorder.Visibility = Visibility.Collapsed;
        RefreshConnectionActions();
        _selecting = true;
        try
        {
            if (tab.Owner is SerialSessionViewModel serial && _vm is not null) _vm.SelectedSerialSession = serial;
            if (tab.Owner is NetworkSessionViewModel network && _vm is not null) _vm.SelectedSession = network;
        }
        finally { _selecting = false; }
        Dispatcher.BeginInvoke(new Action(() => (SessionTabs.ItemContainerGenerator.ContainerFromItem(tab) as FrameworkElement)?.BringIntoView()), DispatcherPriority.Loaded);
    }

    private void ShowSelectedTab()
    {
        if (_selectedTab is not null && Tabs.Contains(_selectedTab)) { SelectTab(_selectedTab); return; }
        var selected = _vm?.SelectedSerialSession ?? (object?)_vm?.SelectedSession;
        var tab = selected is not null && _byOwner.TryGetValue(selected, out var selectedTab)
            ? selectedTab
            : Tabs.FirstOrDefault();
        if (tab is not null) SelectTab(tab); else UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        EmptyState.Visibility = Tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EditConnectionButton.Visibility = Tabs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (Tabs.Count == 0) { EditConnectionButton.IsEnabled = false; WorkspaceMessageBorder.Visibility = Visibility.Collapsed; }
    }

    private static DebugToolConnectionPanel? ToolConnection(DebugSessionTab tab) => tab.Owner switch
    { PidDebugView pid => pid.ConnectionPanel, CameraDebugView camera => camera.ConnectionPanel, _ => null };
    internal DebugToolEndpoint CaptureConnection(DebugSessionTab tab) => tab.Owner switch
    {
        SerialSessionViewModel serial => new(DebugToolTransport.Serial, "127.0.0.1", 9000, serial.SelectedSerialPort?.ToSettings() ?? new()),
        NetworkSessionViewModel network => DebugConnectionPreset.FromOptions(network.Options),
        _ => ToolConnection(tab)!.CaptureEndpoint()
    };
    private static bool CanApplyConnection(DebugSessionTab tab) => tab.Owner switch
    {
        SerialSessionViewModel serial => serial.CanConfigure,
        NetworkSessionViewModel network => network.CanStart && !network.IsSending,
        _ => ToolConnection(tab)?.IsActive == false
    };
    private void RefreshConnectionActions()
    {
        if (_selectedTab is not { } tab) return;
        try
        {
            var endpoint = CaptureConnection(tab);
            EditConnectionButton.ToolTip = CanApplyConnection(tab) ? "编辑连接 · " + (endpoint.Transport == DebugToolTransport.Serial ? string.IsNullOrEmpty(endpoint.Serial?.PortName) ? "请选择串口设备" : $"{endpoint.Serial?.PortName} · {endpoint.Serial?.BaudRate} baud"
                : $"{DebugSessionDialog.TransportNames[endpoint.Transport]} · {endpoint.Host}:{endpoint.Port}") : "请先停止当前连接与发送任务，再编辑连接。";
        }
        catch (Exception ex) when (ex is FormatException or OverflowException) { EditConnectionButton.ToolTip = "编辑连接参数"; }
        EditConnectionButton.IsEnabled = CanApplyConnection(tab) && !tab.IsClosing;
    }
    private void EditConnection_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTab is not { } tab || _vm is null) return;
        try
        {
            if (!CanApplyConnection(tab)) throw new InvalidOperationException("请先停止当前连接与发送任务，再编辑连接。");
            var kind = tab.Owner switch
            {
                SerialSessionViewModel => DebugSessionKind.Serial,
                PidDebugView => DebugSessionKind.Pid,
                CameraDebugView => DebugSessionKind.Camera,
                NetworkSessionViewModel network => network.Protocol == "MQTT" ? DebugSessionKind.Mqtt : network.TreatAsHttp
                    ? network.Options.Mode == TransportMode.Server ? DebugSessionKind.HttpServer : DebugSessionKind.HttpClient
                    : network.Options.Mode == TransportMode.Server ? DebugSessionKind.NetworkServer : DebugSessionKind.NetworkClient,
                _ => throw new InvalidOperationException("此会话不支持连接设置。")
            };
            OpenDialog(new DebugSessionDialog(_vm, kind, CaptureConnection(tab), (tab.Owner as NetworkSessionViewModel)?.Options), tab);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or OverflowException) { ShowWorkspaceError(ex.Message); }
    }
    internal async Task ApplyConnectionAsync(DebugSessionTab tab, DebugToolEndpoint endpoint, TransportOptions? options = null)
    {
        if (_vm is null || !Tabs.Contains(tab) || tab.IsClosing || _closing) throw new InvalidOperationException("标签已关闭。");
        if (!CanApplyConnection(tab)) throw new InvalidOperationException("请先停止当前连接与发送任务，再修改连接设置。");
        DebugConnectionPreset.Validate(endpoint);
        if (tab.Owner is SerialSessionViewModel serial)
        {
            if (endpoint.Transport != DebugToolTransport.Serial) throw new ArgumentException("串口标签只能使用串口连接。");
            var settings = endpoint.Serial!;
            var row = serial.SerialPorts.FirstOrDefault(port => port.PortName == settings.PortName);
            if (row is null)
            {
                row = new(new(settings.PortName, settings.PortName, "设备尚未上线", null)) { IsAvailable = false };
                serial.SerialPorts.Add(row);
            }
            serial.SelectedSerialPort = row;
            row.BaudRate = settings.BaudRate; row.CustomBaudRate = 0; row.DataBits = settings.DataBits;
            row.Parity = settings.Parity; row.StopBits = settings.StopBits; row.Handshake = settings.Handshake;
        }
        else if (tab.Owner is NetworkSessionViewModel network)
        {
            if (endpoint.Transport == DebugToolTransport.Serial) throw new ArgumentException("网络标签请选择 TCP 或 UDP。");
            await _vm.ReconfigureNetworkAsync(network, options ?? DebugConnectionPreset.ToOptions(endpoint));
            tab.Kind = network.TreatAsHttp ? $"HTTP {network.Mode}" : $"{network.Protocol} {network.Mode}";
        }
        else ToolConnection(tab)!.ApplyEndpoint(endpoint);
    }

    private void NewSession_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is not null && !_closing) OpenDialog(new DebugSessionDialog(_vm));
    }

    private void OpenDialog(DebugSessionDialog dialog, DebugSessionTab? editingTab = null)
    {
        if (_closing || SessionDialog is not null) return;
        FinishRename();
        _previousFocus = Keyboard.FocusedElement;
        _editingTab = editingTab;
        dialog.Cancelled += Dialog_Cancelled;
        dialog.Submitted += Dialog_Submitted;
        WorkspaceSurface.IsEnabled = false;
        // Native WebView2 content cannot be covered by a WPF overlay. Hide only its visual host.
        if (_selectedTab?.Owner is SerialSessionViewModel { ShellMode: true }) ContentHost.Visibility = Visibility.Hidden;
        DialogHost.Content = dialog;
        DialogLayer.Visibility = Visibility.Visible;
    }

    private void CloseDialog()
    {
        if (SessionDialog is { } dialog)
        {
            dialog.Cancelled -= Dialog_Cancelled;
            dialog.Submitted -= Dialog_Submitted;
        }
        DialogHost.Content = null;
        DialogLayer.Visibility = Visibility.Collapsed;
        ContentHost.Visibility = Visibility.Visible;
        WorkspaceSurface.IsEnabled = true;
        _editingTab = null;
        if (_previousFocus is UIElement { IsVisible: true, IsEnabled: true } previous) previous.Focus();
        else NewSessionButton.Focus();
        _previousFocus = null;
    }

    private void Dialog_Cancelled(object? sender, EventArgs e) => CloseDialog();
    private async void Dialog_Submitted(object? sender, EventArgs e)
    {
        if (sender is not DebugSessionDialog dialog || dialog.Request is null || _dialogOperation is not null || _closing) return;
        var operation = ApplyDialogAsync(dialog, _editingTab);
        _dialogOperation = operation;
        try { await operation; }
        finally { if (ReferenceEquals(_dialogOperation, operation)) _dialogOperation = null; }
    }

    private async Task ApplyDialogAsync(DebugSessionDialog dialog, DebugSessionTab? editingTab)
    {
        if (_vm is null || dialog.Request is not { } request) return;
        dialog.SetBusy(true);
        try
        {
            if (editingTab is not null) await ApplyConnectionAsync(editingTab, request.Endpoint, request.NetworkOptions);
            else if (request.NetworkOptions is { } options) await _vm.CreateNetworkSessionAsync(options);
            else
            {
                DebugSessionTab? tab;
                if (request.Kind == DebugSessionKind.Serial)
                {
                    var session = _vm.AddSerialSession();
                    tab = _byOwner.GetValueOrDefault(session);
                }
                else tab = request.Kind == DebugSessionKind.Pid ? AddPidSession() : AddCameraSession();
                if (tab is not null)
                {
                    if (request.Endpoint.Transport != DebugToolTransport.Serial || !string.IsNullOrEmpty(request.Endpoint.Serial?.PortName)) await ApplyConnectionAsync(tab, request.Endpoint);
                    SelectTab(tab);
                }
            }
            CloseDialog();
            RefreshConnectionActions();
        }
        catch (Exception ex) { dialog.ShowError(ex.Message); }
        finally { dialog.SetBusy(false); }
    }

    private void ShowWorkspaceError(string message) { WorkspaceMessage.Text = message; WorkspaceMessageBorder.Visibility = Visibility.Visible; }

    internal DebugSessionTab? AddPidSession()
    {
        if (_vm is null) return null;
        var view = new PidDebugView(_vm.DebugSerialPortService);
        view.UseEmbeddedLayout();
        var tab = AddTab("PID", "新会话", view, view);
        SelectTab(tab);
        return tab;
    }

    internal DebugSessionTab? AddCameraSession()
    {
        if (_vm is null) return null;
        var view = new CameraDebugView(_vm.DebugSerialPortService);
        view.UseEmbeddedLayout();
        var tab = AddTab("摄像头", "新会话", view, view);
        SelectTab(tab);
        return tab;
    }

    private void SelectTab_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DebugSessionTab tab) SelectTab(tab);
    }

    private void RenameTab_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button { DataContext: DebugSessionTab tab, Parent: Grid label }) return;
        e.Handled = true;
        _dragStart = null;
        FinishRename();
        SelectTab(tab);
        var editor = label.Children.OfType<TextBox>().Single();
        _renameEditor = editor;
        editor.Text = tab.Name;
        editor.Tag = tab.Name;
        editor.Visibility = Visibility.Visible;
        editor.Focus();
        editor.SelectAll();
    }

    private void TitleEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape)) return;
        e.Handled = true;
        FinishRename(e.Key == Key.Enter);
        ((sender as FrameworkElement)?.Parent as Grid)?.Children.OfType<Button>().FirstOrDefault()?.Focus();
    }

    private void TitleEditor_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => FinishRename();

    private void FinishRename(bool commit = true)
    {
        var editor = _renameEditor;
        _renameEditor = null;
        if (editor is null) return;
        if (commit && editor.DataContext is DebugSessionTab tab && editor.Text.Trim() != (string?)editor.Tag)
        {
            if (tab.Owner is SerialSessionViewModel serial) serial.CustomTitle = editor.Text;
            else if (editor.Text.Trim() is { Length: > 0 } name) { tab.HasCustomName = true; tab.Name = name; }
        }
        editor.Visibility = Visibility.Collapsed;
    }

    private async void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DebugSessionTab tab) return;
        try { await CloseTabAsync(tab, true); }
        catch (Exception ex) { tab.Status = "关闭失败"; ShowWorkspaceError("关闭失败：" + ex.Message); }
    }

    internal async Task CloseTabAsync(DebugSessionTab tab, bool confirm = true)
    {
        if (_closing || tab.IsClosing || !Tabs.Contains(tab)) return;
        var active = tab.Owner switch
        {
            SerialSessionViewModel serial => serial.IsOpen || serial.IsBusy,
            NetworkSessionViewModel network => !network.CanStart,
            PidDebugView pid => pid.HasActiveWork,
            CameraDebugView camera => camera.HasActiveWork,
            _ => false
        };
        var message = $"确定关闭“{tab.Title}”吗？" + (active ? "将断开连接并停止后台任务。" : "该标签的历史和发送配置将不再保留。");
        if (tab.Owner is PidDebugView) message += "关闭不会自动停止开发板控制。";
        if (confirm && new ConfirmationWindow("关闭调试标签", message, "关闭标签") { Owner = Window.GetWindow(this) }.ShowDialog() != true) return;
        tab.IsClosing = true;
        tab.Content.IsEnabled = false;
        var selected = _selectedTab;
        var index = Tabs.IndexOf(tab);
        try
        {
        switch (tab.Owner)
        {
            case SerialSessionViewModel serial when _vm is not null:
                await _vm.CloseSerialSessionAsync(serial, keepEmptyTab: false);
                break;
            case NetworkSessionViewModel network when _vm is not null:
                await _vm.RemoveNetworkSessionAsync(network);
                break;
            case PidDebugView pid:
                await pid.ShutdownAsync();
                RemoveTabVisual(tab);
                break;
            case CameraDebugView camera:
                await camera.ShutdownAsync();
                RemoveTabVisual(tab);
                break;
            default:
                RemoveTabVisual(tab);
                break;
        }
        if (selected != tab && selected is not null && Tabs.Contains(selected)) SelectTab(selected);
        else if (Tabs.Count > 0) SelectTab(Tabs[Math.Min(index, Tabs.Count - 1)]);
        UpdateEmptyState();
        }
        finally { tab.IsClosing = false; if (Tabs.Contains(tab)) tab.Content.IsEnabled = true; }
    }

    private void Tabs_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void Tab_MouseDown(object sender, MouseButtonEventArgs e)
        => _dragStart = _renameEditor is null && e.ClickCount == 1 ? e.GetPosition(this) : null;

    private void Tab_MouseMove(object sender, MouseEventArgs e)
    {
        if (_renameEditor is not null || _dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement element || element.DataContext is not DebugSessionTab tab) return;
        var delta = e.GetPosition(this) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragStart = null;
        DragDrop.DoDragDrop(element, new DataObject(typeof(DebugSessionTab), tab), DragDropEffects.Move);
    }

    private void Tab_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetData(typeof(DebugSessionTab)) is DebugSessionTab tab && Tabs.Contains(tab) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void Tab_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(DebugSessionTab)) is not DebugSessionTab source || (sender as FrameworkElement)?.DataContext is not DebugSessionTab target) return;
        var from = Tabs.IndexOf(source);
        var to = Tabs.IndexOf(target);
        if (from >= 0 && to >= 0 && from != to) Tabs.Move(from, to);
        e.Handled = true;
    }

    public Task ShutdownAsync() => _shutdown ??= ShutdownCoreAsync();

    private async Task ShutdownCoreAsync()
    {
        if (_closing) return;
        _closing = true;
        if (_dialogOperation is { } operation) await operation;
        CloseDialog();
        foreach (var tab in Tabs.ToArray())
        {
            switch (tab.Owner)
            {
                case PidDebugView pid: await pid.ShutdownAsync(); break;
                case CameraDebugView camera: await camera.ShutdownAsync(); break;
                case NetworkSessionViewModel network: await network.StopAsync(); break;
                case SerialSessionViewModel serial: await serial.CloseAsync("已关闭"); break;
            }
        }
        if (_vm is not null)
        {
            _vm.SerialSessions.CollectionChanged -= SerialSessions_Changed;
            _vm.Sessions.CollectionChanged -= NetworkSessions_Changed;
            _vm.PropertyChanged -= MainViewModelChanged;
        }
        foreach (var tab in Tabs.ToArray()) RemoveTabVisual(tab);
        UpdateEmptyState();
    }
}
