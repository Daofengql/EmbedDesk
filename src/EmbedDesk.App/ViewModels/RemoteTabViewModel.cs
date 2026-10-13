namespace EmbedDesk.App.ViewModels;

/// <summary>
/// Common tab title contract used by the remote debugging workspace.
/// Custom titles are local to the open tab and do not change saved connection profiles.
/// </summary>
public interface IRemoteTabViewModel
{
    string TabTitle { get; }
    string CustomTitle { get; set; }
}
