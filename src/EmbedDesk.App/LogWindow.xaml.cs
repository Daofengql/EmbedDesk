using System.Windows;

namespace EmbedDesk.App;

public partial class LogWindow : Window
{
    public LogWindow()
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
    }
}
