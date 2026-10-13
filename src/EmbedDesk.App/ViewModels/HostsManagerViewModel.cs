using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EmbedDesk.Core.Models;

namespace EmbedDesk.App.ViewModels;

public sealed partial class HostsRuleViewModel : ObservableObject
{
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private string _domain = "";
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _comment = "";
    public HostsMapping ToMapping() => new() { Enabled = Enabled, Domain = Domain, Address = Address, Comment = Comment };
    public static HostsRuleViewModel From(HostsMapping rule) => new() { Enabled = rule.Enabled, Domain = rule.Domain, Address = rule.Address, Comment = rule.Comment };
}

public sealed partial class HostsProfileViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "默认方案";
    public ObservableCollection<HostsRuleViewModel> Rules { get; } = [];
}

public sealed partial class HostsManagerViewModel : ObservableObject
{
    public ObservableCollection<HostsProfileViewModel> Profiles { get; } = [];
    [ObservableProperty] private HostsProfileViewModel? _selectedProfile;
    [ObservableProperty] private string _status = "草稿与系统解析独立；点击应用才会修改 Hosts。";

    internal HostsManagerSettings Capture() => new()
    {
        Profiles = Profiles.Select(p => new HostsProfile { Name = p.Name.Trim(), Rules = HostsMapping.Validate(p.Rules.Select(r => r.ToMapping())) }).ToArray(),
        SelectedProfile = Math.Max(0, Profiles.IndexOf(SelectedProfile!))
    };
    internal void Load(HostsManagerSettings settings)
    {
        Profiles.Clear();
        foreach (var profile in settings.Profiles)
        {
            var vm = new HostsProfileViewModel { Name = profile.Name };
            foreach (var rule in profile.Rules) vm.Rules.Add(HostsRuleViewModel.From(rule));
            Profiles.Add(vm);
        }
        SelectedProfile = Profiles[settings.SelectedProfile];
    }
}
