using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using EmbedDesk.App.ViewModels;
using Microsoft.Win32;

namespace EmbedDesk.App.Controls;

public partial class SerialWorkspace : UserControl
{
    private SerialSessionViewModel? _viewModel;
    private readonly BufferedTrafficPresenter _presenter;

    public SerialWorkspace()
    {
        InitializeComponent();
        _presenter = new BufferedTrafficPresenter(ReceiveText);
        RefreshReceive();
        Unloaded += (_, _) =>
        {
            if (_viewModel?.IsDisposed != true) return;
            DisposeView();
        };
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel = DataContext as SerialSessionViewModel;
            if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelChanged;
            BindBuffer();
        };
    }

    internal void DisposeView()
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelChanged;
        _presenter.Bind(null); Shell.Dispose();
    }
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SerialSessionViewModel.SelectedSerialPort)) BindBuffer();
    }
    private void BindBuffer()
    {
        _presenter.Bind(_viewModel?.Terminal);
        RefreshReceive();
    }
    private void DisplayOption_Changed(object sender, RoutedEventArgs args) => RefreshReceive();
    private void RefreshReceive()
    {
        _presenter?.Configure(new(HexDisplay.IsChecked == true, ShowTime.IsChecked == true, ShowSent.IsChecked == true), AutoScroll.IsChecked == true);
    }
    private async void Shell_InputReceived(object? sender, string text) { if (_viewModel is not null) await _viewModel.SendShellInputAsync(text); }
    private async void SaveReceive_Click(object sender, RoutedEventArgs e)
    {
        _presenter.Flush();
        var dialog = new SaveFileDialog { Filter = "文本文件|*.txt", FileName = $"serial-{DateTime.Now:yyyyMMdd-HHmmss}.txt" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await File.WriteAllTextAsync(dialog.FileName, ReceiveText.Text);
    }
}
