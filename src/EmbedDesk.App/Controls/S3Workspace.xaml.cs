using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using EmbedDesk.App.ViewModels;

namespace EmbedDesk.App.Controls;

public sealed class S3ObjectTypeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? "前缀" : "对象";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public partial class S3Workspace : UserControl
{
    private S3BrowserViewModel? _viewModel;

    public S3Workspace()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => _viewModel = DataContext as S3BrowserViewModel;
    }

    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null) await _viewModel.ConnectAsync();
    }

    private async void GoUp_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null) await _viewModel.GoUpAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null) await _viewModel.RefreshAsync();
    }

    private async void Prefix_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _viewModel is null) return;
        e.Handled = true;
        await _viewModel.RefreshAsync();
    }

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null) await _viewModel.UploadAsync();
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null) await _viewModel.DownloadSelectedAsync();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null) await _viewModel.DeleteSelectedAsync();
    }

    private void CancelTransfer_Click(object sender, RoutedEventArgs e) => _viewModel?.CancelTransfer();

    private async void LoadMore_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null) await _viewModel.LoadMoreAsync();
    }

    private async void Object_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is not null) await _viewModel.OpenObjectAsync(_viewModel.SelectedObject);
    }
}
