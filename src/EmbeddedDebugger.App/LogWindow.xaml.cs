using System.Windows;

namespace EmbeddedDebugger.App;

public partial class LogWindow : Window
{
    public LogWindow()
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
    }
}
