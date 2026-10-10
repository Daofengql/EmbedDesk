using System.Windows;
using System.Windows.Controls;
using EmbeddedDebugger.Core.Models;

namespace EmbeddedDebugger.App;

public partial class NetworkTypeSelectionWindow : Window
{
    public NetworkTypeSelectionWindow()
    {
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
    }

    public TransportProtocol? Protocol { get; private set; }

    private void Protocol_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        Protocol = tag switch
        {
            "Udp" => TransportProtocol.Udp,
            "Mqtt" => TransportProtocol.Mqtt,
            _ => TransportProtocol.Tcp
        };
        DialogResult = true;
    }
}
