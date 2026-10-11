using System.Windows;
using System.Windows.Controls;
using EmbeddedDebugger.App.ViewModels;

namespace EmbeddedDebugger.App.Controls;

public partial class EmbeddedToolsWorkspace : UserControl
{
    public EmbeddedToolsWorkspace() => InitializeComponent();

    private void OpenTool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tool }) return;
        var owner = Window.GetWindow(this);
        Window? window = tool switch
        {
            "Firmware" => new FirmwareToolWindow((DataContext as MainWindowViewModel)?.SerialPortService ?? throw new InvalidOperationException("串口服务不可用。")),
            "Image" => new ImageWriterWindow(),
            "Checksum" => new ByteToolWindow(checksum: true),
            "Codec" => new ByteToolWindow(),
            "C51" => new FirmwareToolWindow((DataContext as MainWindowViewModel)?.SerialPortService ?? throw new InvalidOperationException("串口服务不可用。"), 1),
            "Ch347" => new FirmwareToolWindow((DataContext as MainWindowViewModel)?.SerialPortService ?? throw new InvalidOperationException("串口服务不可用。"), 2),
            "Logs" => new LogWindow { DataContext = DataContext },
            _ => null
        };
        if (window is null) return;
        window.Owner = owner;
        window.Show();
    }
}
