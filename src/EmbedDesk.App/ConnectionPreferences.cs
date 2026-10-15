using CommunityToolkit.Mvvm.ComponentModel;

namespace EmbedDesk.App;

internal enum MainWindowCloseAction { Ask, Tray, Exit }
internal sealed record RelayServerProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public bool BuiltIn { get; init; }

    public static bool TryNormalizeUrl(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is not { Length: > 0 and <= 2048 } || value.Any(char.IsControl)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0
            || uri.Scheme is not ("http" or "https")) return false;
        normalized = uri.AbsoluteUri.TrimEnd('/');
        return true;
    }
}

internal static class RelayServerCatalog
{
    // 正式免费中继部署后在此加入公开地址，不能把私人测试节点作为产品预设。
    internal static IReadOnlyList<RelayServerProfile> BuiltInServers { get; } = Array.Empty<RelayServerProfile>();
}

internal sealed class RelayServerStatus : ObservableObject
{
    public RelayServerStatus(RelayServerProfile profile) => Profile = profile;
    public RelayServerProfile Profile { get; }
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
    public List<RelayServerProfile> RelayServers { get; init; } = RelayServerCatalog.BuiltInServers.ToList();
    public string DefaultRelayServerId { get; init; } = RelayServerCatalog.BuiltInServers.FirstOrDefault()?.Id ?? string.Empty;
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
