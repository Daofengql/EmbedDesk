using CommunityToolkit.Mvvm.ComponentModel;
using EmbedDesk.Infrastructure.Connections;
namespace EmbedDesk.App.ViewModels;
public sealed record PortMappingRule(string Id, string DeviceId, string ServiceId, string ServiceName, int LocalPort, SharedServiceKind Kind = SharedServiceKind.Tcp,
    string VirtualPort = "", string BridgePort = "", string RemoteEndpoint = "")
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasValidTarget => RemoteEndpoint is { Length: <= 300 } && !RemoteEndpoint.Any(char.IsControl) && (Kind == SharedServiceKind.Serial
        ? EmbedDesk.Infrastructure.Serial.Com0ComEndpoint.IsComName(VirtualPort) && BridgePort is { Length: > 0 and <= 32 }
            && BridgePort.All(char.IsAsciiLetterOrDigit) && !string.Equals(VirtualPort, BridgePort, StringComparison.OrdinalIgnoreCase)
        : Kind is SharedServiceKind.Tcp or SharedServiceKind.Udp && LocalPort is >= 1 and <= 65535);
}
public sealed class PortMappingRuleViewModel(PortMappingRule rule) : ObservableObject
{
    public PortMappingRule Rule { get; private set; } = rule;
    internal string? MappingId { get; set; }
    private string _status = "未启用";
    internal long PreviousBytes, PreviousTime = Environment.TickCount64;
    public string Status { get => _status; internal set { if (SetProperty(ref _status, value)) OnPropertyChanged(nameof(DisplayName)); } }
    public string LocalEndpoint => Rule.Kind == SharedServiceKind.Serial ? Rule.VirtualPort : $"127.0.0.1:{Rule.LocalPort}";
    public string RemoteTarget => $"{Rule.DeviceId} · {Rule.Kind switch { SharedServiceKind.Serial => "串口", SharedServiceKind.Udp => "UDP", _ => "TCP" }} · {(Rule.RemoteEndpoint.Length > 0 ? Rule.RemoteEndpoint : Rule.ServiceName)}";
    public string DisplayName => $"{LocalEndpoint} → {RemoteTarget} · {Rule.ServiceName} · {Status}";
    internal void UpdateTarget(DeviceRemoteService service)
    {
        Rule = Rule with { ServiceName = service.Service.Name, RemoteEndpoint = service.TargetEndpoint };
        OnPropertyChanged(nameof(Rule)); OnPropertyChanged(nameof(RemoteTarget)); OnPropertyChanged(nameof(DisplayName));
    }
}
