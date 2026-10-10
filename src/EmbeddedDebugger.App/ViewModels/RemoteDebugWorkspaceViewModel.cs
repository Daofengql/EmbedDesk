using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbeddedDebugger.Core.Abstractions;
using EmbeddedDebugger.Core.Models;

namespace EmbeddedDebugger.App.ViewModels;

public sealed class RemoteDebugWorkspaceViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IRemoteDebugService _service;
    private readonly string _settingsDirectory;
    private readonly string _profilePath;
    private RemoteConnectionProfile? _selectedProfile;
    private RemoteProfileEditorViewModel? _profileEditor;
    private RemoteWorkspaceViewModel? _selectedSession;
    private string _status = "选择一个连接配置开始远程会话";
    private bool _isManagerView = true;
    private bool _isLocalShellFullscreen;
    private bool _isSessionPaneExpanded = true;
    private double _sessionPaneWidth = 236;
    private RemoteFileDocumentViewModel? _selectedDocument;
    private DesktopSessionViewModel? _selectedDesktop;

    public RemoteDebugWorkspaceViewModel(IRemoteDebugService service, string? settingsDirectory = null)
    {
        _service = service;
        _settingsDirectory = settingsDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EmbeddedDebugger");
        _profilePath = Path.Combine(_settingsDirectory, "remote-profiles.json");
        Profiles = new ObservableCollection<RemoteConnectionProfile>(LoadProfiles());
        if (Profiles.Count == 0 && !File.Exists(_profilePath)) Profiles.Add(CreateDefaultProfile());
        Sessions = new ObservableCollection<RemoteWorkspaceViewModel>();
        SshKeys = new ObservableCollection<string>(LoadSshKeys());
        LocalShell = new LocalShellViewModel();
        Tabs.Add(LocalShell);
        Sessions.CollectionChanged += (_, _) => SyncTabs();
        Documents.CollectionChanged += (_, _) => SyncTabs();
        DesktopSessions.CollectionChanged += (_, _) => SyncTabs();
        ProtocolOptions = Enum.GetValues<RemoteProtocol>();

        OpenProfileCommand = new AsyncRelayCommand(OpenProfileAsync, () => SelectedProfile is not null && !IsBusy);
        NewProfileCommand = new RelayCommand(NewProfile);
        DeleteProfileCommand = new RelayCommand(DeleteProfile, () => SelectedProfile is not null);
        SaveProfilesCommand = new RelayCommand(SaveProfiles);
        CloseSessionCommand = new AsyncRelayCommand<RemoteWorkspaceViewModel>(CloseSessionAsync);
        ShowManagerCommand = new RelayCommand(() => IsManagerView = true);
        SelectSessionCommand = new RelayCommand<RemoteWorkspaceViewModel>(SelectSession);
        SelectLocalShellCommand = new RelayCommand(() => { SelectedDesktop = null; SelectedDocument = null; SelectedSession = null; IsManagerView = false; });
        SelectDocumentCommand = new RelayCommand<RemoteFileDocumentViewModel>(document => { if (document is not null) { IsLocalShellFullscreen = false; SelectedDocument = document; } });
        ToggleSessionPaneCommand = new RelayCommand(() => IsSessionPaneExpanded = !IsSessionPaneExpanded);
        SelectedProfile = Profiles.FirstOrDefault();
        Profiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasProfiles));
    }

    public ObservableCollection<RemoteConnectionProfile> Profiles { get; }
    public ObservableCollection<RemoteWorkspaceViewModel> Sessions { get; }
    public ObservableCollection<RemoteFileDocumentViewModel> Documents { get; } = [];
    public ObservableCollection<object> Tabs { get; } = [];
    public ObservableCollection<DesktopSessionViewModel> DesktopSessions { get; } = [];
    public ObservableCollection<string> SshKeys { get; }
    public LocalShellViewModel LocalShell { get; }
    public IReadOnlyList<RemoteProtocol> ProtocolOptions { get; }
    public IReadOnlyList<RemoteAuthenticationMode> AuthenticationOptions { get; } = Enum.GetValues<RemoteAuthenticationMode>();
    public IAsyncRelayCommand OpenProfileCommand { get; }
    public IRelayCommand NewProfileCommand { get; }
    public IRelayCommand DeleteProfileCommand { get; }
    public IRelayCommand SaveProfilesCommand { get; }
    public IAsyncRelayCommand<RemoteWorkspaceViewModel> CloseSessionCommand { get; }
    public IRelayCommand ShowManagerCommand { get; }
    public IRelayCommand<RemoteWorkspaceViewModel> SelectSessionCommand { get; }
    public IRelayCommand SelectLocalShellCommand { get; }
    public IRelayCommand ToggleSessionPaneCommand { get; }
    public IRelayCommand<RemoteFileDocumentViewModel> SelectDocumentCommand { get; }

    public RemoteConnectionProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!SetProperty(ref _selectedProfile, value)) return;
            if (_profileEditor is not null) _profileEditor.Profile.PropertyChanged -= OnProfileChanged;
            ProfileEditor = value is null ? null : new RemoteProfileEditorViewModel(value, SshKeys);
            if (ProfileEditor is not null) ProfileEditor.Profile.PropertyChanged += OnProfileChanged;
            OpenProfileCommand.NotifyCanExecuteChanged();
            DeleteProfileCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanDeleteSelectedProfile));
        }
    }

    private void OnProfileChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        ProfileEditor?.NotifyEndpointChanged();
        OnPropertyChanged(nameof(IsPasswordAuthentication));
        OnPropertyChanged(nameof(IsKeyAuthentication));
        SaveProfilesCommand.NotifyCanExecuteChanged();
    }

    public RemoteProfileEditorViewModel? ProfileEditor
    {
        get => _profileEditor;
        private set => SetProperty(ref _profileEditor, value);
    }

    public RemoteWorkspaceViewModel? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (value is not null) SelectedDesktop = null;
            if (ReferenceEquals(_selectedSession, value)) return;
            if (_selectedSession is not null)
            {
                _selectedSession.PropertyChanged -= SelectedSession_PropertyChanged;
                _selectedSession.IsTerminalFullscreen = false;
            }
            if (!SetProperty(ref _selectedSession, value)) return;
            if (value is not null) value.PropertyChanged += SelectedSession_PropertyChanged;
            IsLocalShellFullscreen = false;
            OnPropertyChanged(nameof(IsLocalShellSelected));
            OnPropertyChanged(nameof(IsRemoteSessionSelected));
            OnPropertyChanged(nameof(ActiveTab));
            NotifyFullscreenLayout();
        }
    }

    public RemoteFileDocumentViewModel? SelectedDocument
    {
        get => _selectedDocument;
        set
        {
            if (value is not null) SelectedDesktop = null;
            if (!SetProperty(ref _selectedDocument, value)) return;
            OnPropertyChanged(nameof(IsDocumentSelected));
            OnPropertyChanged(nameof(IsLocalShellSelected));
            OnPropertyChanged(nameof(IsRemoteSessionSelected));
            OnPropertyChanged(nameof(ActiveTab));
            if (value is not null)
            {
                IsLocalShellFullscreen = false;
                if (SelectedSession is not null) SelectedSession.IsTerminalFullscreen = false;
            }
            NotifyFullscreenLayout();
        }
    }
    public DesktopSessionViewModel? SelectedDesktop
    {
        get => _selectedDesktop;
        set
        {
            if (ReferenceEquals(value, _selectedDesktop)) return;
            if (_selectedDesktop is not null) _selectedDesktop.IsFullscreen = false;
            SetProperty(ref _selectedDesktop, value);
            OnPropertyChanged(nameof(IsLocalShellSelected));
            OnPropertyChanged(nameof(IsRemoteSessionSelected));
            OnPropertyChanged(nameof(ActiveTab));
            NotifyFullscreenLayout();
        }
    }
    public void OpenDesktop(RemoteConnectionProfile profile)
    {
        IsLocalShellFullscreen = false;
        SelectedDocument = null;
        SelectedSession = null;
        var desktop = new DesktopSessionViewModel(Clone(profile));
        desktop.PropertyChanged += DesktopChanged;
        DesktopSessions.Add(desktop);
        SelectedDesktop = desktop;
        IsManagerView = false;
        Status = $"正在打开 {desktop.ProtocolLabel} · {desktop.DisplayTitle}";
    }
    private void DesktopChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DesktopSessionViewModel.IsFullscreen)) NotifyFullscreenLayout();
        if (sender is DesktopSessionViewModel desktop && ReferenceEquals(SelectedDesktop, desktop)
            && e.PropertyName == nameof(DesktopSessionViewModel.ConnectionStatus))
            Status = $"{desktop.ProtocolLabel} · {desktop.DisplayTitle} · {desktop.ConnectionStatus}";
    }
    public void CloseDesktop(DesktopSessionViewModel desktop)
    {
        desktop.Dispose();
        desktop.PropertyChanged -= DesktopChanged;
        DesktopSessions.Remove(desktop);
        if (SelectedDesktop == desktop) SelectedDesktop = DesktopSessions.LastOrDefault();
    }
    public bool IsDocumentSelected => SelectedDocument is not null;
    public object ActiveTab => (object?)SelectedDesktop ?? (object?)SelectedDocument ?? (object?)SelectedSession ?? LocalShell;
    public bool IsLocalShellSelected => SelectedDesktop is null && SelectedSession is null && SelectedDocument is null;
    public bool IsRemoteSessionSelected => SelectedDesktop is null && SelectedSession is not null && SelectedDocument is null;
    public void SelectTab(object tab)
    {
        if (tab is DesktopSessionViewModel desktop) { IsLocalShellFullscreen = false; SelectedDocument = null; SelectedSession = null; SelectedDesktop = desktop; }
        else if (tab is RemoteWorkspaceViewModel session) SelectSession(session);
        else if (tab is RemoteFileDocumentViewModel document) SelectDocumentCommand.Execute(document);
        else if (ReferenceEquals(tab, LocalShell)) SelectLocalShellCommand.Execute(null);
    }
    public void MoveTab(object tab, object target, bool after)
    {
        if (ReferenceEquals(tab, target) || !Tabs.Contains(tab) || !Tabs.Contains(target)) return;
        var oldIndex = Tabs.IndexOf(tab);
        var newIndex = Tabs.IndexOf(target) + (after ? 1 : 0);
        if (oldIndex < newIndex) newIndex--;
        Tabs.Move(oldIndex, newIndex);
    }
    private void SyncTabs()
    {
        var current = new HashSet<object>(Sessions.Cast<object>().Concat(Documents).Concat(DesktopSessions)) { LocalShell };
        foreach (var removed in Tabs.Where(tab => !current.Contains(tab)).ToArray()) Tabs.Remove(removed);
        foreach (var added in Sessions.Cast<object>().Concat(Documents).Concat(DesktopSessions).Where(tab => !Tabs.Contains(tab))) Tabs.Add(added);
    }
    public GridLength SessionPaneWidth
    {
        get => new(IsSessionPaneVisible ? _sessionPaneWidth : 0);
        set
        {
            if (!IsSessionPaneVisible) return;
            _sessionPaneWidth = Math.Clamp(value.Value, 160, 236);
            OnPropertyChanged();
        }
    }
    public double SessionPaneMinWidth => IsSessionPaneVisible ? 160 : 0;
    public bool IsLocalShellFullscreen
    {
        get => _isLocalShellFullscreen;
        set
        {
            if (!SetProperty(ref _isLocalShellFullscreen, value)) return;
            LocalShell.IsFullscreen = value;
            NotifyFullscreenLayout();
        }
    }
    public bool IsSessionPaneExpanded
    {
        get => _isSessionPaneExpanded;
        set
        {
            if (!SetProperty(ref _isSessionPaneExpanded, value)) return;
            OnPropertyChanged(nameof(IsSessionPaneVisible));
            OnPropertyChanged(nameof(SessionPaneToggleTooltip));
            OnPropertyChanged(nameof(SessionPaneToggleGlyph));
            OnPropertyChanged(nameof(SessionPaneMinWidth));
            OnPropertyChanged(nameof(SessionPaneWidth));
        }
    }
    public bool IsTerminalFullscreen => IsLocalShellSelected ? IsLocalShellFullscreen
        : SelectedDesktop is not null ? SelectedDesktop.IsFullscreen : IsRemoteSessionSelected && SelectedSession!.IsTerminalFullscreen;
    public bool IsSessionPaneVisible => !IsTerminalFullscreen && IsSessionPaneExpanded;
    public string SessionPaneToggleTooltip => IsSessionPaneExpanded ? "收起会话列表" : "显示会话列表";
    public string SessionPaneToggleGlyph => IsSessionPaneExpanded ? "\uE76B" : "\uE76C";

    private void SelectedSession_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RemoteWorkspaceViewModel.IsTerminalFullscreen)) NotifyFullscreenLayout();
    }

    private void NotifyFullscreenLayout()
    {
        OnPropertyChanged(nameof(IsTerminalFullscreen));
        OnPropertyChanged(nameof(IsSessionPaneVisible));
        OnPropertyChanged(nameof(SessionPaneMinWidth));
        OnPropertyChanged(nameof(SessionPaneWidth));
    }

    public bool IsManagerView
    {
        get => _isManagerView;
        set => SetProperty(ref _isManagerView, value);
    }

    public bool HasSessions => Sessions.Count > 0;
    public bool HasProfiles => Profiles.Count > 0;
    public bool CanDeleteSelectedProfile => SelectedProfile is not null;
    public bool IsBusy { get; private set; }
    public bool IsPasswordAuthentication => ProfileEditor?.AuthenticationMode != RemoteAuthenticationMode.PrivateKey;
    public bool IsKeyAuthentication => ProfileEditor?.AuthenticationMode == RemoteAuthenticationMode.PrivateKey;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private async Task OpenProfileAsync()
    {
        if (SelectedProfile is null || IsBusy) return;
        if (SelectedProfile.Protocol is RemoteProtocol.Rdp or RemoteProtocol.Vnc) { SaveProfiles(); OpenDesktop(SelectedProfile); return; }
        SaveProfiles();
        IsBusy = true;
        OnPropertyChanged(nameof(IsBusy));
        OpenProfileCommand.NotifyCanExecuteChanged();
        try
        {
            var session = CreateSession(SelectedProfile);
            Status = $"正在打开 {session.DisplayTitle}";
            await session.ConnectCommand.ExecuteAsync(null);
            if (!session.IsConnected)
            {
                Status = string.IsNullOrWhiteSpace(session.LastError)
                    ? $"连接失败：{session.DisplayTitle}"
                    : $"连接失败：{session.LastError}";
                await session.DisposeAsync();
                IsManagerView = true;
                return;
            }

            Sessions.Add(session);
            SelectedDocument = null;
            SelectedSession = session;
            OnPropertyChanged(nameof(HasSessions));
            DeleteProfileCommand.NotifyCanExecuteChanged();
            Status = $"已连接 {session.DisplayTitle}";
            IsManagerView = false;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsBusy));
            OpenProfileCommand.NotifyCanExecuteChanged();
        }
    }

    private void NewProfile()
    {
        CreateProfileDraft();
        SaveProfiles();
    }

    public RemoteConnectionProfile CreateProfileDraft()
    {
        var profile = new RemoteConnectionProfile { Name = $"新建连接 {Profiles.Count + 1}", Host = "127.0.0.1", Port = 22, Protocol = RemoteProtocol.Ssh };
        Profiles.Add(profile);
        SelectedProfile = profile;
        return profile;
    }

    public void RemoveProfile(RemoteConnectionProfile profile)
    {
        if (!Profiles.Remove(profile)) return;
        SelectedProfile = Profiles.FirstOrDefault();
        SaveProfiles();
    }

    public void AddSshKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || SshKeys.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        SshKeys.Add(path);
        SaveSshKeys();
    }

    public void RemoveSshKey(string? path)
    {
        if (path is null) return;
        SshKeys.Remove(path);
        SaveSshKeys();
    }

    private void SelectSession(RemoteWorkspaceViewModel? session)
    {
        if (session is null) return;
        IsLocalShellFullscreen = false;
        SelectedDocument = null;
        SelectedSession = session;
        IsManagerView = false;
    }

    public async Task OpenProfileAsync(RemoteConnectionProfile profile)
    {
        SelectedProfile = profile;
        await OpenProfileAsync();
    }

    public async Task OpenProfileInNewTabAsync(RemoteConnectionProfile profile)
    {
        if (profile.Protocol is RemoteProtocol.Rdp or RemoteProtocol.Vnc) { SaveProfiles(); OpenDesktop(profile); return; }
        IsLocalShellFullscreen = false;
        SaveProfiles();
        var session = CreateSession(profile);
        Sessions.Add(session);
        SelectedDocument = null;
        SelectedSession = session;
        OnPropertyChanged(nameof(HasSessions));
        DeleteProfileCommand.NotifyCanExecuteChanged();
        IsManagerView = false;
        Status = $"正在连接 {session.DisplayTitle}";

        // Keep the tab visible while the handshake runs. It displays connection
        // details and errors until the remote terminal is ready.
        await session.ConnectCommand.ExecuteAsync(null);
        Status = session.IsWorkspaceReady
            ? $"已连接 {session.DisplayTitle}"
            : $"连接失败：{session.LastError}";
    }

    private void DeleteProfile()
    {
        if (SelectedProfile is null) return;
        var index = Profiles.IndexOf(SelectedProfile);
        Profiles.Remove(SelectedProfile);
        SelectedProfile = Profiles.Count == 0 ? null : Profiles[Math.Min(index, Profiles.Count - 1)];
        SaveProfiles();
    }

    private void SaveProfiles()
    {
        try
        {
            var directory = Path.GetDirectoryName(_profilePath)!;
            Directory.CreateDirectory(directory);
            var records = Profiles.Select(profile => new PersistedProfile
            {
                Name = profile.Name,
                Protocol = profile.Protocol,
                Host = profile.Host,
                Port = profile.Port,
                Username = profile.Username,
                AcceptUnknownHostKey = profile.AcceptUnknownHostKey,
                AuthenticationMode = profile.AuthenticationMode,
                PrivateKeyPath = profile.PrivateKeyPath
            }).ToArray();
            File.WriteAllText(_profilePath, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
            Status = $"已保存 {records.Length} 个连接配置";
        }
        catch (IOException ex) { Status = $"保存连接配置失败：{ex.Message}"; }
    }

    private async Task CloseSessionAsync(RemoteWorkspaceViewModel? session)
    {
        if (session is null) return;
        foreach (var document in Documents.Where(document => document.Owner == session).ToArray())
        {
            if (document.SaveCommand.ExecutionTask is { } saving) await saving;
        }
        // Cancel and finish pending previews before removing their tabs, so an
        // in-flight download cannot open a document for a closing session.
        await session.DisposeAsync();
        foreach (var document in Documents.Where(document => document.Owner == session).ToArray()) CloseDocument(document);
        Sessions.Remove(session);
        if (ReferenceEquals(SelectedSession, session)) SelectedSession = Sessions.LastOrDefault();
        OnPropertyChanged(nameof(HasSessions));
        if (Sessions.Count == 0) IsManagerView = true;
        DeleteProfileCommand.NotifyCanExecuteChanged();
        Status = Sessions.Count == 0 ? "选择一个连接配置开始远程会话" : "会话已关闭";
    }

    private RemoteWorkspaceViewModel CreateSession(RemoteConnectionProfile profile)
    {
        var session = new RemoteWorkspaceViewModel(_service, Clone(profile));
        session.DocumentOpened += (_, document) =>
        {
            if (!Documents.Contains(document)) Documents.Add(document);
            SelectedDocument = document;
        };
        return session;
    }

    public void CloseDocument(RemoteFileDocumentViewModel document)
    {
        if (document.IsSaving) return;
        Documents.Remove(document);
        document.Owner.CloseDocument(document);
        if (SelectedDocument == document) { SelectedDocument = null; SelectedSession = document.Owner; }
    }

    private IReadOnlyList<RemoteConnectionProfile> LoadProfiles()
    {
        try
        {
            if (!File.Exists(_profilePath)) return [];
            var records = JsonSerializer.Deserialize<PersistedProfile[]>(File.ReadAllText(_profilePath)) ?? [];
            return records.Where(record => !string.IsNullOrWhiteSpace(record.Host)).Select(record => new RemoteConnectionProfile
            {
                Name = string.IsNullOrWhiteSpace(record.Name) ? record.Host : record.Name,
                Protocol = record.Protocol,
                Host = record.Host,
                Port = record.Port is > 0 and <= 65535 ? record.Port : RemoteProfileEditorViewModel.DefaultPort(record.Protocol),
                Username = record.Username,
                AcceptUnknownHostKey = record.AcceptUnknownHostKey,
                AuthenticationMode = record.AuthenticationMode,
                PrivateKeyPath = record.PrivateKeyPath
            }).ToArray();
        }
        catch (JsonException) { return []; }
        catch (IOException) { return []; }
    }

    private static RemoteConnectionProfile CreateDefaultProfile() => new() { Name = "本机 Linux", Host = "127.0.0.1", Port = 22, Username = "root" };
    private static RemoteConnectionProfile Clone(RemoteConnectionProfile profile) => new()
    {
        Name = profile.Name,
        Protocol = profile.Protocol,
        Host = profile.Host,
        Port = profile.Port,
        Username = profile.Username,
        Password = profile.Password,
        AcceptUnknownHostKey = profile.AcceptUnknownHostKey,
        AuthenticationMode = profile.AuthenticationMode,
        PrivateKeyPath = profile.PrivateKeyPath,
        PrivateKeyPassphrase = profile.PrivateKeyPassphrase
    };

    public async ValueTask DisposeAsync()
    {
        foreach (var desktop in DesktopSessions.ToArray()) CloseDesktop(desktop);
        foreach (var session in Sessions.ToArray()) await session.DisposeAsync();
        Documents.Clear();
        SelectedDocument = null;
        Sessions.Clear();
        await LocalShell.DisposeAsync();
    }

    private sealed class PersistedProfile
    {
        public string Name { get; set; } = string.Empty;
        public RemoteProtocol Protocol { get; set; }
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; }
        public string Username { get; set; } = string.Empty;
        [JsonIgnore] public string Password { get; set; } = string.Empty;
        public bool AcceptUnknownHostKey { get; set; }
        public RemoteAuthenticationMode AuthenticationMode { get; set; }
        public string PrivateKeyPath { get; set; } = string.Empty;
    }

    private string KeyPath => Path.Combine(_settingsDirectory, "ssh-keys.json");
    private IReadOnlyList<string> LoadSshKeys()
    {
        try { return File.Exists(KeyPath) ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(KeyPath))?.Where(File.Exists).ToArray() ?? [] : []; }
        catch { return []; }
    }
    private void SaveSshKeys()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!); File.WriteAllText(KeyPath, JsonSerializer.Serialize(SshKeys.ToArray(), new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }
}
