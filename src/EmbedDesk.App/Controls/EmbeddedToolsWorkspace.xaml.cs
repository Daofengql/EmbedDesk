using System.Windows;
using System.Windows.Controls;
using EmbedDesk.App.ViewModels;

namespace EmbedDesk.App.Controls;

public partial class EmbeddedToolsWorkspace : UserControl
{
    public EmbeddedToolsWorkspace() => InitializeComponent();

    private void OpenTool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tool }) return;
        var owner = Window.GetWindow(this);
        var viewModel = DataContext as MainWindowViewModel;
        Window? window = tool switch
        {
            "Firmware" => new FirmwareToolWindow(viewModel?.SerialPortService ?? throw new InvalidOperationException("串口服务不可用。"), viewModel),
            "EspConnect" => new EspConnectToolWindow(viewModel?.SerialPortService ?? throw new InvalidOperationException("串口服务不可用。"), viewModel),
            "Image" => new ImageWriterWindow(),
            "Checksum" => new ByteToolWindow(checksum: true),
            "Codec" => new ByteToolWindow(),
            "C51" => new StcIspToolWindow(viewModel?.SerialPortService ?? throw new InvalidOperationException("串口服务不可用。"), viewModel),
            "Ch347" => new Ch347ToolWindow(),
            "Logs" => new TrafficRecordingWindow((DataContext as MainWindowViewModel) ?? throw new InvalidOperationException("会话服务不可用。")),
            _ => null
        };
        if (window is null) return;
        window.Owner = owner;
        window.Show();
    }
}
