using CommunityToolkit.Mvvm.ComponentModel;

namespace EmbedDesk.App.ViewModels;

public sealed class SerialSendQueueItem : ObservableObject
{
    private bool _enabled = true;
    private string _payload = string.Empty;
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public string Payload { get => _payload; set => SetProperty(ref _payload, value); }
}
