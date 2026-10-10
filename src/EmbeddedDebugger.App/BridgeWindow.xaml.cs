using System.Windows;

namespace EmbeddedDebugger.App;

public partial class BridgeWindow : Window
{
    public BridgeWindow()
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
    }
}
