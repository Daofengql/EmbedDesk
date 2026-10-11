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
            "EspConnect" => new EspConnectToolWindow((DataContext as MainWindowViewModel)?.SerialPortService ?? throw new InvalidOperationException("串口服务不可用。")),
            "Image" => new ImageWriterWindow(),
            "Checksum" => new ByteToolWindow(checksum: true),
            "Codec" => new ByteToolWindow(),
            "C51" => new StcIspToolWindow((DataContext as MainWindowViewModel)?.SerialPortService ?? throw new InvalidOperationException("串口服务不可用。")),
            "Ch347" => new Ch347ToolWindow(),
            "Logs" => new TrafficRecordingWindow((DataContext as MainWindowViewModel) ?? throw new InvalidOperationException("会话服务不可用。")),
            _ => null
        };
        if (window is null) return;
        window.Owner = owner;
        window.Show();
    }
}
