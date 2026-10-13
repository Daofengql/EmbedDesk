using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbedDesk.Core.Abstractions;
using EmbedDesk.Core.Models;

namespace EmbedDesk.App.ViewModels;

public sealed class RemoteDebugWorkspaceViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IRemoteDebugService _service;
    private readonly AppSettingsStore? _appSettings;
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
    private S3BrowserViewModel? _selectedS3;
    private LocalShellViewModel? _selectedLocalShell;
    private int _localShellNumber;

    public RemoteDebugWorkspaceViewModel(IRemoteDebugService service, string? settingsDirectory = null)
        : this(service, settingsDirectory, null)
    {
    }

    internal RemoteDebugWorkspaceViewModel(IRemoteDebugService service, string? settingsDirectory, AppSettingsStore? appSettings,
        LocalShellStartupOptions? startup = null)
    {
        _service = service;
        _appSettings = appSettings;
        _settingsDirectory = settingsDirectory ?? AppDataPaths.SettingsDirectory;
        _profilePath = Path.Combine(_settingsDirectory, "remote-profiles.json");
        var persisted = appSettings?.Snapshot();
        _isSessionPaneExpanded = persisted?.RemoteSessionPaneExpanded ?? true;
        _sessionPaneWidth = Math.Clamp(persisted?.RemoteSessionPaneWidth ?? 236, 160, 236);
        Profiles = new ObservableCollection<RemoteConnectionProfile>(LoadProfiles());
        if (Profiles.Count == 0 && !File.Exists(_profilePath)) Profiles.Add(CreateDefaultProfile());
        Sessions = new ObservableCollection<RemoteWorkspaceViewModel>();
        SshKeys = new ObservableCollection<string>(LoadSshKeys());
        LocalShells = new ObservableCollection<LocalShellViewModel>();
        if (startup?.Directories is { Count: > 0 } directories)
            foreach (var directory in directories) AddLocalShell(directory);
        else AddLocalShell();
        Sessions.CollectionChanged += (_, _) => SyncTabs();
        Documents.CollectionChanged += (_, _) => SyncTabs();
        DesktopSessions.CollectionChanged += (_, _) => SyncTabs();
        S3Sessions.CollectionChanged += (_, _) => SyncTabs();
        ProtocolOptions = Enum.GetValues<RemoteProtocol>();

        OpenProfileCommand = new AsyncRelayCommand(OpenProfileAsync, () => SelectedProfile is not null && !IsBusy);
        NewProfileCommand = new RelayCommand(NewProfile);
        DeleteProfileCommand = new RelayCommand(DeleteProfile, () => SelectedProfile is not null);
        SaveProfilesCommand = new RelayCommand(SaveProfiles);
        CloseSessionCommand = new AsyncRelayCommand<RemoteWorkspaceViewModel>(CloseSessionAsync);
        ShowManagerCommand = new RelayCommand(() => IsManagerView = true);
        SelectSessionCommand = new RelayCommand<RemoteWorkspaceViewModel>(SelectSession);
        SelectLocalShellCommand = new RelayCommand(() => { if ((SelectedLocalShell ?? LocalShell) is { } shell) SelectLocalShell(shell); else AddLocalShell(); });
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
    public ObservableCollection<S3BrowserViewModel> S3Sessions { get; } = [];
    public ObservableCollection<LocalShellViewModel> LocalShells { get; }
    public ObservableCollection<string> SshKeys { get; }
    /// <summary>Compatibility alias for the first local shell.</summary>
    public LocalShellViewModel? LocalShell => LocalShells.FirstOrDefault();
    public LocalShellViewModel? SelectedLocalShell
    {
        get => _selectedLocalShell;
        private set
        {
            if (!SetProperty(ref _selectedLocalShell, value)) return;
            OnPropertyChanged(nameof(ActiveTab));
            OnPropertyChanged(nameof(IsLocalShellSelected));
            NotifyFullscreenLayout();
        }
    }
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

    public LocalShellViewModel AddLocalShell(string? workingDirectory = null)
    {
        var shell = new LocalShellViewModel(++_localShellNumber, workingDirectory);
        LocalShells.Add(shell);
        Tabs.Add(shell);
        OnPropertyChanged(nameof(LocalShell));
        OnPropertyChanged(nameof(HasTabs));
        SelectLocalShell(shell);
        return shell;
    }

    public async Task CloseLocalShellAsync(LocalShellViewModel shell)
    {
        if (!LocalShells.Contains(shell)) return;
        await shell.DisposeAsync();
        LocalShells.Remove(shell);
        SyncTabs();
        if (ReferenceEquals(SelectedLocalShell, shell)) SelectedLocalShell = LocalShell;
        OnPropertyChanged(nameof(LocalShell));
    }

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
            if (value is not null) { SelectedDesktop = null; SelectedS3 = null; }
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
            if (value is not null) { SelectedDesktop = null; SelectedS3 = null; }
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
            if (value is not null) { IsLocalShellFullscreen = false; SelectedS3 = null; }
            SetProperty(ref _selectedDesktop, value);
            OnPropertyChanged(nameof(IsLocalShellSelected));
            OnPropertyChanged(nameof(IsRemoteSessionSelected));
            OnPropertyChanged(nameof(ActiveTab));
            NotifyFullscreenLayout();
        }
    }
    public S3BrowserViewModel? SelectedS3
    {
        get => _selectedS3;
        set
        {
            if (ReferenceEquals(value, _selectedS3)) return;
            if (value is not null) IsLocalShellFullscreen = false;
            SetProperty(ref _selectedS3, value);
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
        SelectedS3 = null;
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
    public async Task OpenS3BrowserAsync(RemoteConnectionProfile profile)
    {
        IsLocalShellFullscreen = false;
        SelectedDocument = null;
        SelectedSession = null;
        SelectedDesktop = null;
        var browser = new S3BrowserViewModel(Clone(profile));
        browser.PropertyChanged += S3BrowserChanged;
        S3Sessions.Add(browser);
        SelectedS3 = browser;
        IsManagerView = false;
        Status = $"正在打开 S3 · {browser.DisplayTitle}";
        await browser.ConnectAsync();
        Status = $"S3 · {browser.DisplayTitle} · {browser.Status}";
    }
    private void S3BrowserChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is S3BrowserViewModel browser && ReferenceEquals(SelectedS3, browser) && e.PropertyName == nameof(S3BrowserViewModel.Status))
            Status = $"S3 · {browser.DisplayTitle} · {browser.Status}";
    }
    public async Task CloseS3BrowserAsync(S3BrowserViewModel browser)
    {
        browser.PropertyChanged -= S3BrowserChanged;
        await browser.DisposeAsync();
        S3Sessions.Remove(browser);
        if (ReferenceEquals(SelectedS3, browser)) SelectedS3 = S3Sessions.LastOrDefault();
        if (S3Sessions.Count == 0 && Sessions.Count == 0 && DesktopSessions.Count == 0) IsManagerView = true;
    }

    public async Task CloseTabAsync(object tab)
    {
        switch (tab)
        {
            case LocalShellViewModel shell:
                await CloseLocalShellAsync(shell);
                break;
            case RemoteWorkspaceViewModel session:
                await CloseSessionAsync(session);
                break;
            case DesktopSessionViewModel desktop:
                CloseDesktop(desktop);
                break;
            case S3BrowserViewModel browser:
                await CloseS3BrowserAsync(browser);
                break;
            case RemoteFileDocumentViewModel document:
                CloseDocument(document);
                break;
        }
    }

    public async Task DuplicateTabAsync(object tab)
    {
        if (!Tabs.Contains(tab)) return;
        switch (tab)
        {
            case LocalShellViewModel local:
                AddLocalShell(local.InitialDirectory);
                break;
            case RemoteWorkspaceViewModel session:
                await OpenProfileInNewTabAsync(session.Profile, saveProfiles: false);
                break;
            case DesktopSessionViewModel desktop:
                OpenDesktop(desktop.Profile);
                break;
            case S3BrowserViewModel browser:
                await OpenS3BrowserAsync(browser.Profile);
                break;
            default:
                Status = "文件预览标签不能复制";
                break;
        }
    }

    public void SetTabTitle(object tab, string title)
    {
        if (Tabs.Contains(tab) && tab is IRemoteTabViewModel remoteTab) remoteTab.CustomTitle = title.Trim();
    }
    public bool IsDocumentSelected => SelectedDocument is not null;
    public object? ActiveTab => (object?)SelectedS3 ?? (object?)SelectedDesktop ?? (object?)SelectedDocument ?? (object?)SelectedSession ?? SelectedLocalShell;
    public bool HasTabs => Tabs.Count > 0;
    public bool IsLocalShellSelected => SelectedS3 is null && SelectedDesktop is null && SelectedSession is null && SelectedDocument is null && SelectedLocalShell is not null;
    public bool IsRemoteSessionSelected => SelectedS3 is null && SelectedDesktop is null && SelectedSession is not null && SelectedDocument is null;
    public void SelectTab(object tab)
    {
        if (!Tabs.Contains(tab)) return;
        if (tab is DesktopSessionViewModel desktop) { IsLocalShellFullscreen = false; SelectedDocument = null; SelectedSession = null; SelectedDesktop = desktop; }
        else if (tab is S3BrowserViewModel s3) { IsLocalShellFullscreen = false; SelectedDocument = null; SelectedSession = null; SelectedDesktop = null; SelectedS3 = s3; IsManagerView = false; }
        else if (tab is RemoteWorkspaceViewModel session) SelectSession(session);
        else if (tab is RemoteFileDocumentViewModel document) SelectDocumentCommand.Execute(document);
        else if (tab is LocalShellViewModel shell) SelectLocalShell(shell);
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
        var active = ActiveTab;
        var index = active is null ? 0 : Math.Max(0, Tabs.IndexOf(active));
        var current = new HashSet<object>(Sessions.Cast<object>().Concat(Documents).Concat(DesktopSessions).Concat(S3Sessions).Concat(LocalShells));
        foreach (var removed in Tabs.Where(tab => !current.Contains(tab)).ToArray()) Tabs.Remove(removed);
        foreach (var added in Sessions.Cast<object>().Concat(Documents).Concat(DesktopSessions).Concat(S3Sessions).Where(tab => !Tabs.Contains(tab))) Tabs.Add(added);
        OnPropertyChanged(nameof(HasTabs));
        if (active is null || current.Contains(active)) return;
        IsLocalShellFullscreen = false;
        SelectedS3 = null;
        SelectedDesktop = null;
        SelectedDocument = null;
        SelectedSession = null;
        SelectedLocalShell = null;
        if (Tabs.Count > 0) SelectTab(Tabs[Math.Min(index, Tabs.Count - 1)]);
    }
    public GridLength SessionPaneWidth
    {
        get => new(IsSessionPaneVisible ? _sessionPaneWidth : 0);
        set
        {
            if (!IsSessionPaneVisible) return;
            _sessionPaneWidth = Math.Clamp(value.Value, 160, 236);
            OnPropertyChanged();
            SavePanePreferences();
        }
    }
    public double SessionPaneMinWidth => IsSessionPaneVisible ? 160 : 0;
    public bool IsLocalShellFullscreen
    {
        get => _isLocalShellFullscreen;
        set
        {
            if (!SetProperty(ref _isLocalShellFullscreen, value)) return;
            if (SelectedLocalShell is not null) SelectedLocalShell.IsFullscreen = value;
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
            SavePanePreferences();
        }
    }
    public bool IsTerminalFullscreen => IsLocalShellSelected ? IsLocalShellFullscreen
        : SelectedDesktop is not null ? SelectedDesktop.IsFullscreen : IsRemoteSessionSelected && SelectedSession!.IsTerminalFullscreen;
    public bool IsSessionPaneVisible => !IsTerminalFullscreen && IsSessionPaneExpanded;
    public string SessionPaneToggleTooltip => IsSessionPaneExpanded ? "收起会话列表" : "显示会话列表";
    public string SessionPaneToggleGlyph => IsSessionPaneExpanded ? "\uE76B" : "\uE76C";

    private void SavePanePreferences()
    {
        _appSettings?.Update(settings =>
        {
            settings.RemoteSessionPaneWidth = _sessionPaneWidth;
            settings.RemoteSessionPaneExpanded = _isSessionPaneExpanded;
        });
    }

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
    internal void ReportDirectoryDropStatus(string message) => Status = message;

    private async Task OpenProfileAsync()
    {
        if (SelectedProfile is null || IsBusy) return;
        if (SelectedProfile.Protocol == RemoteProtocol.S3) { SaveProfiles(); await OpenS3BrowserAsync(SelectedProfile); return; }
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

    /// <summary>Merges imported connection metadata and deliberately drops all credentials.</summary>
    internal ConfigurationMergeResult MergeProfiles(IEnumerable<RemoteConnectionProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var added = 0;
        var skipped = 0;
        foreach (var imported in profiles)
        {
            if (imported is null || string.IsNullOrWhiteSpace(imported.Name)
                || Profiles.Any(existing => string.Equals(existing.Name, imported.Name, StringComparison.OrdinalIgnoreCase))
                || Profiles.Count >= 100)
            {
                skipped++;
                continue;
            }

            var copy = Clone(imported);
            copy.Password = string.Empty;
            copy.PrivateKeyPassphrase = string.Empty;
            copy.S3CredentialTarget = string.Empty;
            copy.S3AccessKeyId = string.Empty;
            copy.S3SecretAccessKey = string.Empty;
            copy.RememberS3Credentials = false;
            Profiles.Add(copy);
            added++;
        }

        if (added > 0)
        {
            SelectedProfile ??= Profiles.FirstOrDefault();
            SaveProfiles();
            DeleteProfileCommand.NotifyCanExecuteChanged();
        }

        return new ConfigurationMergeResult(added, skipped);
    }

    public void RemoveProfile(RemoteConnectionProfile profile)
    {
        if (!Profiles.Remove(profile)) return;
        DeleteS3Credential(profile);
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

    private void SelectLocalShell(LocalShellViewModel? shell)
    {
        if (shell is null || !LocalShells.Contains(shell)) return;
        IsLocalShellFullscreen = false;
        SelectedDesktop = null;
        SelectedS3 = null;
        SelectedDocument = null;
        SelectedSession = null;
        SelectedLocalShell = shell;
        IsManagerView = false;
        OnPropertyChanged(nameof(IsLocalShellSelected));
        OnPropertyChanged(nameof(IsRemoteSessionSelected));
        NotifyFullscreenLayout();
    }

    public async Task OpenProfileAsync(RemoteConnectionProfile profile)
    {
        SelectedProfile = profile;
        await OpenProfileAsync();
    }

    public async Task OpenProfileInNewTabAsync(RemoteConnectionProfile profile, bool saveProfiles = true)
    {
        if (saveProfiles) SaveProfiles();
        if (profile.Protocol == RemoteProtocol.S3) { await OpenS3BrowserAsync(profile); return; }
        if (profile.Protocol is RemoteProtocol.Rdp or RemoteProtocol.Vnc) { OpenDesktop(profile); return; }
        IsLocalShellFullscreen = false;
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
        if (ReferenceEquals(ActiveTab, session) && Sessions.Contains(session))
            Status = session.IsWorkspaceReady ? $"已连接 {session.DisplayTitle}" : $"连接失败：{session.LastError}";
    }

    private void DeleteProfile()
    {
        if (SelectedProfile is null) return;
        var index = Profiles.IndexOf(SelectedProfile);
        DeleteS3Credential(SelectedProfile);
        Profiles.Remove(SelectedProfile);
        SelectedProfile = Profiles.Count == 0 ? null : Profiles[Math.Min(index, Profiles.Count - 1)];
        SaveProfiles();
    }

    private static void DeleteS3Credential(RemoteConnectionProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.S3CredentialTarget)) return;
        try { S3CredentialStore.Delete(profile.S3CredentialTarget); }
        catch (System.ComponentModel.Win32Exception) { }
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
                PrivateKeyPath = profile.PrivateKeyPath,
                S3CredentialTarget = profile.RememberS3Credentials ? profile.S3CredentialTarget : string.Empty,
                S3Region = profile.S3Region,
                S3Bucket = profile.S3Bucket,
                RememberS3Credentials = profile.RememberS3Credentials
            }).ToArray();
            var temporaryPath = _profilePath + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
                if (File.Exists(_profilePath)) File.Replace(temporaryPath, _profilePath, null);
                else File.Move(temporaryPath, _profilePath);
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
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
        var wasActive = ReferenceEquals(ActiveTab, document);
        Documents.Remove(document);
        document.Owner.CloseDocument(document);
        if (SelectedDocument == document) SelectedDocument = null;
        if (wasActive && Sessions.Contains(document.Owner)) SelectSession(document.Owner);
    }

    private IReadOnlyList<RemoteConnectionProfile> LoadProfiles()
    {
        try
        {
            if (!File.Exists(_profilePath)) return [];
            var records = JsonSerializer.Deserialize<PersistedProfile[]>(File.ReadAllText(_profilePath)) ?? [];
            return records.Where(record => record.Protocol == RemoteProtocol.S3 || !string.IsNullOrWhiteSpace(record.Host)).Select(record => new RemoteConnectionProfile
            {
                Name = string.IsNullOrWhiteSpace(record.Name) ? record.Host : record.Name,
                Protocol = record.Protocol,
                Host = record.Host,
                Port = record.Port is > 0 and <= 65535 ? record.Port : RemoteProfileEditorViewModel.DefaultPort(record.Protocol),
                Username = record.Username,
                AcceptUnknownHostKey = record.AcceptUnknownHostKey,
                AuthenticationMode = record.AuthenticationMode,
                PrivateKeyPath = record.PrivateKeyPath,
                S3CredentialTarget = record.RememberS3Credentials ? record.S3CredentialTarget : string.Empty,
                S3Region = string.IsNullOrWhiteSpace(record.S3Region) ? "us-east-1" : record.S3Region,
                S3Bucket = record.S3Bucket,
                RememberS3Credentials = record.RememberS3Credentials
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
        PrivateKeyPassphrase = profile.PrivateKeyPassphrase,
        S3CredentialTarget = profile.S3CredentialTarget,
        S3Region = profile.S3Region,
        S3Bucket = profile.S3Bucket,
        RememberS3Credentials = profile.RememberS3Credentials,
        S3AccessKeyId = profile.S3AccessKeyId,
        S3SecretAccessKey = profile.S3SecretAccessKey
    };

    public async ValueTask DisposeAsync()
    {
        foreach (var desktop in DesktopSessions.ToArray()) CloseDesktop(desktop);
        foreach (var browser in S3Sessions.ToArray()) await CloseS3BrowserAsync(browser);
        foreach (var session in Sessions.ToArray()) await session.DisposeAsync();
        foreach (var profile in Profiles) { profile.S3AccessKeyId = string.Empty; profile.S3SecretAccessKey = string.Empty; }
        Documents.Clear();
        SelectedDocument = null;
        Sessions.Clear();
        foreach (var shell in LocalShells.ToArray()) await shell.DisposeAsync();
        LocalShells.Clear();
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
        public string S3CredentialTarget { get; set; } = string.Empty;
        public string S3Region { get; set; } = "us-east-1";
        public string S3Bucket { get; set; } = string.Empty;
        public bool RememberS3Credentials { get; set; }
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
