using System.ComponentModel;
using System.Windows;
using EmbedDesk.App.Controls;
using EmbedDesk.Core.Abstractions;

namespace EmbedDesk.App;

public partial class CameraDebugWindow : Window, IDebugToolWindow
{
    private readonly CameraDebugView _view;
    private bool _closing, _closed;
    public bool HasActiveWork => _view.HasActiveWork;
    public bool HasUnsavedChanges => _view.HasUnsavedChanges;

    public CameraDebugWindow(ISerialPortService serial)
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        _view = new CameraDebugView(serial);
        Content = _view;
        Closing += OnClosing;
    }

    public new object? FindName(string name) => _view.FindName(name) ?? base.FindName(name);
    public Task ShutdownAsync() => _view.ShutdownAsync();

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (_closing) return;
        if (HasActiveWork && MessageBox.Show(this, "断开图像连接并关闭窗口？", "关闭摄像头助手", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _closing = true;
        await ShutdownAsync();
        _closed = true;
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
}