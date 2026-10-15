using System.Text;
using System.Windows;

namespace EmbedDesk.App;

public partial class ConfigurationImportPreviewWindow : Window
{
    internal ConfigurationImportPreviewWindow(ImportedConfiguration imported)
    {
        ArgumentNullException.ThrowIfNull(imported);
        InitializeComponent();
        WindowTheme.ApplyDarkTitleBar(this);
        SummaryText.Text = $"远程连接 {imported.RemoteProfiles.Count} 项 · 网络串口 {imported.SerialBridgePresets.Count} 项 · STCP {imported.StcpTunnels.Count} 项";
        PreviewText.Text = BuildPreview(imported);
    }

    private static string BuildPreview(ImportedConfiguration imported)
    {
        var builder = new StringBuilder();
        builder.AppendLine("[远程连接]");
        foreach (var profile in imported.RemoteProfiles)
        {
            var endpoint = profile.Protocol == EmbedDesk.Core.Models.RemoteProtocol.S3
                ? $"{(string.IsNullOrWhiteSpace(profile.Host) ? "AWS S3" : profile.Host)} · {profile.S3Region} · 桶 {profile.S3Bucket}"
                : $"{profile.Host}:{profile.Port} · 用户 {profile.Username}";
            builder.AppendLine($"- {profile.Name} ({GetProtocolLabel(profile.Protocol)}) · {endpoint}");
        }

        builder.AppendLine();
        builder.AppendLine("[网络串口预设]");
        foreach (var preset in imported.SerialBridgePresets)
            builder.AppendLine($"- {preset.Name} · {preset.Host}:{preset.Port}");

        builder.AppendLine();
        builder.AppendLine("[STCP 隧道配置]");
        foreach (var tunnel in imported.StcpTunnels)
        {
            var mapping = tunnel.Role == EmbedDesk.Protocols.StcpTunnelRole.Service
                ? $"服务 {tunnel.Name} → {tunnel.TargetHost}:{tunnel.TargetPort}"
                : $"访问 {tunnel.ServiceName} → 本地端口 {tunnel.LocalPort}";
            builder.AppendLine($"- {tunnel.Name} ({mapping}) · {tunnel.ServerAddress}:{tunnel.ServerPort} · 需要重新配置共享密钥");
        }

        return builder.ToString();
    }

    private static string GetProtocolLabel(EmbedDesk.Core.Models.RemoteProtocol protocol) => protocol switch
    {
        EmbedDesk.Core.Models.RemoteProtocol.Ssh => "SSH",
        EmbedDesk.Core.Models.RemoteProtocol.Telnet => "Telnet",
        EmbedDesk.Core.Models.RemoteProtocol.Rdp => "RDP",
        EmbedDesk.Core.Models.RemoteProtocol.Vnc => "VNC",
        EmbedDesk.Core.Models.RemoteProtocol.S3 => "S3",
        _ => protocol.ToString()
    };

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
