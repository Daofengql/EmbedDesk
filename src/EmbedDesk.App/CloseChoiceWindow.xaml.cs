using System.Windows;
namespace EmbedDesk.App;
public partial class CloseChoiceWindow : Window
{
    internal MainWindowCloseAction Choice { get; private set; }
    internal bool Remember => RememberChoice.IsChecked == true;
    public CloseChoiceWindow() { InitializeComponent(); WindowTheme.ApplyDarkTitleBar(this); }
    private void Tray_Click(object sender, RoutedEventArgs e) { Choice = MainWindowCloseAction.Tray; DialogResult = true; }
    private void Exit_Click(object sender, RoutedEventArgs e) { Choice = MainWindowCloseAction.Exit; DialogResult = true; }
}
