using CommunityToolkit.Mvvm.ComponentModel;
using EmbedDesk.Infrastructure.Connections;

namespace EmbedDesk.App;

internal enum MainWindowCloseAction { Ask, Tray, Exit }
internal sealed record RelayServerProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public bool BuiltIn { get; init; }

    public static bool TryNormalizeUrl(string? value, out string normalized)
        => RelayConnectionInfo.TryNormalizeUrl(value, out normalized);
}

internal static class RelayServerCatalog
{
    // This public value filters scans; it is neither a user credential nor device authorization.
    private const string FreeRelayAdmissionToken = "avGCGdvDnrGM_KOjnRkAVyffju1yhPw8-DqFXfz2VK8";
    internal static IReadOnlyList<RelayServerProfile> BuiltInServers { get; } = Array.AsReadOnly<RelayServerProfile>(
    [
        new() { Id = "free-relay-1", Name = "免费中继 1", Url = "https://embeddesk-free-relay-sgp.silverdragon.cn", BuiltIn = true }
    ]);

    internal static bool TryGetAdmissionToken(RelayServerProfile profile, out string token)
    {
        var known = BuiltInServers.FirstOrDefault(server => server.Id == profile.Id && server.Url == profile.Url);
        token = known is not null ? FreeRelayAdmissionToken : string.Empty;
        return known is not null;
    }
}

internal sealed class RelayServerStatus : ObservableObject
{
    public RelayServerStatus(RelayServerProfile profile, bool hasAdmissionToken = false)
    { Profile = profile; HasAdmissionToken = hasAdmissionToken; }
    public RelayServerProfile Profile { get; }
    public bool HasAdmissionToken { get; }
    public string AdmissionLabel => HasAdmissionToken ? "准入 Token 已配置" : "未配置准入 Token";
    private string _state = "未测速";
    private string _latencyText = "—";
    private string _error = string.Empty;
    private bool _isDefault;
    public string State { get => _state; set => SetProperty(ref _state, value); }
    public string LatencyText { get => _latencyText; set => SetProperty(ref _latencyText, value); }
    public string Error { get => _error; set => SetProperty(ref _error, value); }
    public bool IsAvailable { get; set; }
    public bool IsDefault
    {
        get => _isDefault;
        set { if (SetProperty(ref _isDefault, value)) OnPropertyChanged(nameof(DefaultLabel)); }
    }
    public string DefaultLabel => IsDefault ? "默认中继" : "设为默认";
    public string SourceLabel => Profile.BuiltIn ? "内置免费" : "自定义";
    public bool CanDelete => !Profile.BuiltIn;
}

internal sealed record ConnectionPreferences
{
    public string ServerUrl { get; init; } = string.Empty;
    // Legacy field retained for JSON migration; the runtime always auto-connects when a relay exists.
    public bool AutoConnect { get; init; } = true;
    // AppSettingsStore merges the authoritative catalog after migrating saved relay preferences.
    public List<RelayServerProfile> RelayServers { get; init; } = [];
    public string DefaultRelayServerId { get; init; } = string.Empty;
    // Until the next settings save, only existing profiles may use the old global admission secret.
    public bool RelayTokensMigrated { get; init; }
    public bool PreferDirect { get; init; } = true;
    public int TimeoutSeconds { get; init; } = 10;
    public int RetrySeconds { get; init; } = 5;
    public string ProxyMode { get; init; } = "Direct";
    public string ProxyUrl { get; init; } = string.Empty;
    public string ProxyUser { get; init; } = string.Empty;
    public string TrustedRootFile { get; init; } = string.Empty;
    public bool StaticPassword { get; init; }
    public bool ManualApprovalOnly { get; init; }
    public MainWindowCloseAction CloseAction { get; init; }
}
