using System.Windows;
using EmbeddedDebugger.App.ViewModels;

namespace EmbeddedDebugger.App;

internal static class SerialPortUsePrompt
{
    public static async Task<bool> EnsureExclusiveAsync(Window owner, MainWindowViewModel? workspace, string portName)
    {
        if (workspace?.IsSerialPortInUse(portName) != true) return true;
        var confirmation = new ConfirmationWindow("串口正在被调试标签使用",
            $"串口 {portName} 当前由串口调试标签占用。烧录需要独占它，是否关闭该标签的连接并继续？标签记录和配置会保留。",
            "关闭调试连接并继续") { Owner = owner };
        if (confirmation.ShowDialog() != true) return false;
        if (await workspace.ReleaseSerialPortForToolAsync(portName)) return true;
        return false;
    }
}
