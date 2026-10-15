using System.Windows;
using EmbedDesk.App.ViewModels;
namespace EmbedDesk.App;
public partial class DeviceServicesWindow : Window
{
    public DeviceServicesWindow(ConnectionCenterViewModel model) { InitializeComponent(); DataContext = model; WindowTheme.ApplyDarkTitleBar(this); }
}
