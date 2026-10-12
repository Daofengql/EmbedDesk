using System.Windows;

namespace EmbedDesk.App;

public partial class BridgeWindow : Window
{
    public BridgeWindow()
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
    }
}
