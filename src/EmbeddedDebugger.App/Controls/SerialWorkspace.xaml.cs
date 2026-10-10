using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using EmbeddedDebugger.App.ViewModels;
using Microsoft.Win32;

namespace EmbeddedDebugger.App.Controls;

public partial class SerialWorkspace : UserControl
{
    private MainWindowViewModel? _viewModel;
    private TerminalBufferViewModel? _buffer;
    private BridgeWindow? _bridgeWindow;

    public SerialWorkspace()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel = DataContext as MainWindowViewModel;
            if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelChanged;
            BindBuffer();
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainWindowViewModel.SelectedSerialPort)) BindBuffer();
    }
    private void BindBuffer()
    {
        if (_buffer is not null) _buffer.Entries.CollectionChanged -= OnEntriesChanged;
        _buffer = _viewModel?.SelectedSerialPort?.Terminal;
        if (_buffer is not null) _buffer.Entries.CollectionChanged += OnEntriesChanged;
        RefreshReceive();
    }
    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs args) => RefreshReceive();
    private void DisplayOption_Changed(object sender, RoutedEventArgs args) => RefreshReceive();
    private void RefreshReceive()
    {
        if (ReceiveText is null) return;
        var text = new StringBuilder();
        foreach (var entry in _buffer?.Entries ?? [])
        {
            if (!entry.IsReceived && ShowSent.IsChecked != true) continue;
            if (ShowTime.IsChecked == true) text.Append('[').Append(entry.DisplayTime).Append("] ");
            if (ShowSent.IsChecked == true) text.Append(entry.Direction).Append("  ");
            text.Append(HexDisplay.IsChecked == true ? entry.DisplayHex : entry.DisplayText);
            if (HexDisplay.IsChecked == true || ShowTime.IsChecked == true || ShowSent.IsChecked == true) text.AppendLine();
            else if (entry.Data.Length > 0 && entry.Data[^1] == '\n') text.AppendLine();
        }
        ReceiveText.Text = text.ToString();
        if (AutoScroll.IsChecked == true) ReceiveText.ScrollToEnd();
    }
    private void NormalMode_Checked(object sender, RoutedEventArgs e) { if (_viewModel is not null) _viewModel.ShellMode = false; }
    private async void Shell_InputReceived(object? sender, string text) { if (_viewModel is not null) await _viewModel.SendShellInputAsync(text); }
    private void OpenBridge_Click(object sender, RoutedEventArgs e)
    {
        if (_bridgeWindow is null)
        {
            _bridgeWindow = new BridgeWindow { Owner = Window.GetWindow(this), DataContext = DataContext };
            _bridgeWindow.Closed += (_, _) => _bridgeWindow = null;
            _bridgeWindow.Show();
        }
        else _bridgeWindow.Activate();
    }
    private async void SaveReceive_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "文本文件|*.txt", FileName = $"serial-{DateTime.Now:yyyyMMdd-HHmmss}.txt" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await File.WriteAllTextAsync(dialog.FileName, ReceiveText.Text);
    }
}
