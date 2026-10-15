using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows;

namespace EmbedDesk.App.ViewModels;

/// <summary>A single header in the shared debug strip; its content stays mounted while hidden.</summary>
public sealed class DebugSessionTab : ObservableObject
{
    private string _kind, _name, _status = "未连接";
    private bool _selected, _closing;
    internal DebugSessionTab(string kind, string name, object owner, FrameworkElement content)
    { _kind = kind; _name = name; Owner = owner; Content = content; }
    public string Kind { get => _kind; internal set { if (SetProperty(ref _kind, value)) OnPropertyChanged(nameof(Title)); } }
    public object Owner { get; }
    public FrameworkElement Content { get; }
    public string Title => $"{Kind} · {Name}";
    public string Name { get => _name; internal set { if (SetProperty(ref _name, value)) OnPropertyChanged(nameof(Title)); } }
    public string Status { get => _status; internal set => SetProperty(ref _status, value); }
    public bool IsSelected { get => _selected; internal set => SetProperty(ref _selected, value); }
    public bool IsClosing { get => _closing; internal set => SetProperty(ref _closing, value); }
    internal bool HasCustomName { get; set; }
}
