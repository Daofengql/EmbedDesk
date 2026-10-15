using System.ComponentModel;
using System.Windows;
using EmbedDesk.App.Controls;
using EmbedDesk.Core.Abstractions;

namespace EmbedDesk.App;

public partial class PidDebugWindow : Window, IDebugToolWindow
{
    private readonly PidDebugView _view;
    private bool _closing, _closed;
    public bool HasActiveWork => _view.HasActiveWork;
    public bool HasUnsavedChanges => _view.HasUnsavedChanges;

    public PidDebugWindow(ISerialPortService serial)
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        _view = new PidDebugView(serial);
        Content = _view;
        Closing += OnClosing;
    }

    public new object? FindName(string name) => _view.FindName(name) ?? base.FindName(name);
    internal Task ExportCsvAsync(string path, bool includeFiltered, CancellationToken cancellationToken = default) =>
        _view.ExportCsvAsync(path, includeFiltered, cancellationToken);
    public Task ShutdownAsync() => _view.ShutdownAsync();

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (_closing) return;
        if (HasActiveWork && MessageBox.Show(this, "断开 PID 连接、取消导出并关闭窗口？关闭不会自动停止开发板控制。", "关闭 PID 助手", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _closing = true;
        await ShutdownAsync();
        _closed = true;
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
}