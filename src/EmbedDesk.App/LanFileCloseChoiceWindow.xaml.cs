using System.Windows;
namespace EmbedDesk.App;
public partial class LanFileCloseChoiceWindow : Window
{
    public bool KeepService { get; private set; }
    public LanFileCloseChoiceWindow() { InitializeComponent(); WindowTheme.ApplyDarkTitleBar(this); }
    private void Keep_Click(object sender, RoutedEventArgs e) { KeepService = true; DialogResult = true; }
    private void Stop_Click(object sender, RoutedEventArgs e) { KeepService = false; DialogResult = true; }
}
