using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbedDesk.Core.Models;

namespace EmbedDesk.App.ViewModels;

public sealed class DesktopSessionViewModel : ObservableObject, IDisposable, IRemoteTabViewModel
{
    private string _connectionStatus = "连接中";
    private string _message = "正在初始化远程桌面…";
    private bool _isFullscreen;
    private string _customTitle = string.Empty;
    public DesktopSessionViewModel(RemoteConnectionProfile profile)
    {
        Profile = profile;
        ReconnectCommand = new RelayCommand(() => ConnectRequested?.Invoke(this, EventArgs.Empty), () => !IsConnecting && !IsDisposed);
        DisconnectCommand = new RelayCommand(() => DisconnectRequested?.Invoke(this, EventArgs.Empty), () => IsConnecting || IsConnected);
    }
    public RemoteConnectionProfile Profile { get; }
    public NetworkStatusViewModel NetworkStatus { get; } = new();
    public string DisplayTitle => Profile.Name;
    public string CustomTitle
    {
        get => _customTitle;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (!SetProperty(ref _customTitle, normalized)) return;
            OnPropertyChanged(nameof(TabTitle));
        }
    }
    public string TabTitle => string.IsNullOrWhiteSpace(CustomTitle) ? DisplayTitle : CustomTitle;
    public string ProtocolLabel => Profile.Protocol == RemoteProtocol.Rdp ? "RDP" : "VNC";
    public string Endpoint => $"{Profile.Host}:{Profile.Port}";
    public string HostPlatform => Profile.Protocol == RemoteProtocol.Rdp ? "windows" : "unknown";
    public string ConnectionStatus => _connectionStatus;
    public string Message => _message;
    public bool IsConnected => ConnectionStatus == "已连接";
    public bool IsConnecting => ConnectionStatus == "连接中";
    public bool IsDisposed { get; private set; }
    public bool IsFullscreen { get => _isFullscreen; set => SetProperty(ref _isFullscreen, value); }
    public IRelayCommand ReconnectCommand { get; }
    public IRelayCommand DisconnectCommand { get; }
    public event EventHandler? ConnectRequested;
    public event EventHandler? DisconnectRequested;
    public event EventHandler? Disposed;
    public void SetState(string state, string message)
    {
        if (IsDisposed) return;
        SetProperty(ref _connectionStatus, state, nameof(ConnectionStatus));
        SetProperty(ref _message, message, nameof(Message));
        if (IsConnected) NetworkStatus.Track(new(Profile.Host, Profile.Port), $"{ProtocolLabel} · {Endpoint}");
        else NetworkStatus.Stop(IsConnecting ? "等待连接" : "探测已停止");
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsConnecting));
        if (!IsConnected) IsFullscreen = false;
        ReconnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
    }
    public void Dispose()
    {
        if (IsDisposed) return;
        IsFullscreen = false;
        IsDisposed = true;
        NetworkStatus.Dispose();
        Disposed?.Invoke(this, EventArgs.Empty);
        Profile.Password = string.Empty;
    }
}
