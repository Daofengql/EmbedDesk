using CommunityToolkit.Mvvm.ComponentModel;
using EmbedDesk.Infrastructure.Connections;
namespace EmbedDesk.App.ViewModels;
public sealed record PortMappingRule(string Id, string DeviceId, string ServiceId, string ServiceName, int LocalPort, SharedServiceKind Kind = SharedServiceKind.Tcp,
    string VirtualPort = "", string BridgePort = "")
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasValidTarget => Kind == SharedServiceKind.Serial
        ? EmbedDesk.Infrastructure.Serial.Com0ComEndpoint.IsComName(VirtualPort) && BridgePort is { Length: > 0 and <= 32 }
            && BridgePort.All(char.IsAsciiLetterOrDigit) && !string.Equals(VirtualPort, BridgePort, StringComparison.OrdinalIgnoreCase)
        : Kind is SharedServiceKind.Tcp or SharedServiceKind.Udp && LocalPort is >= 1 and <= 65535;
}
public sealed class PortMappingRuleViewModel(PortMappingRule rule) : ObservableObject
{
    public PortMappingRule Rule { get; } = rule;
    internal string? MappingId { get; set; }
    private string _status = "未启用";
    internal long PreviousBytes, PreviousTime = Environment.TickCount64;
    public string Status { get => _status; internal set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(DisplayName)); } }
    public string DisplayName => Rule.Kind == SharedServiceKind.Serial
        ? $"{Rule.VirtualPort} → {Rule.DeviceId} · 串口 · {Rule.ServiceName} · {Status}"
        : $"127.0.0.1:{Rule.LocalPort} → {Rule.DeviceId} · {Rule.Kind switch { SharedServiceKind.Udp => "UDP", _ => "TCP" }} · {Rule.ServiceName} · {Status}";
}
