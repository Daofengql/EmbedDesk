using EmbedDesk.Core.Models;

namespace EmbedDesk.App.ViewModels;

public sealed record NetworkToolViewModel(
    string Name,
    string Description,
    string Glyph,
    TransportProtocol? Protocol,
    TransportMode? Mode,
    bool TreatAsHttp = false,
    bool IsSupported = true)
{
    public string Status => IsSupported ? Description : "协议适配即将加入";
}
