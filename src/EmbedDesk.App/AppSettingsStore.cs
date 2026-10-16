using System.IO;
using System.IO.Ports;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmbedDesk.App;

/// <summary>Versioned, local-only application preferences. Secrets and session data do not belong here.</summary>
internal sealed class AppSettingsStore : IDisposable
{
    private const int CurrentSchemaVersion = 3;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly object _sync = new();
    private readonly object _writeSync = new();
    private readonly string _path;
    private readonly Timer _saveTimer;
    private AppSettingsDocument _document;
    private bool _dirty;
    private bool _disposed;

    public AppSettingsStore(string? directory = null)
    {
        _path = Path.Combine(directory ?? AppDataPaths.SettingsDirectory, "app-settings.json");
        _document = Load(_path);
        _saveTimer = new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public string FilePath => _path;

    public AppSettingsDocument Snapshot()
    {
        lock (_sync) return Clone(_document);
    }

    public void Update(Action<AppSettingsDocument> update)
    {
        lock (_sync)
        {
            if (_disposed) return;
            update(_document);
            _document.ConnectionPreferences = NormalizeConnectionPreferences(_document.ConnectionPreferences ?? new());
            _document.SchemaVersion = CurrentSchemaVersion;
            _dirty = true;
            _saveTimer.Change(400, Timeout.Infinite);
        }
    }

    public void SaveSerialPreferences(ViewModels.SerialPortRowViewModel row)
    {
        if (row.IsRemote) return;
        var preference = new SerialPortPreference
        {
            BaudRate = row.BaudRate,
            CustomBaudRate = row.CustomBaudRate,
            DataBits = row.DataBits,
            Parity = row.Parity,
            StopBits = row.StopBits,
            Handshake = row.Handshake
        };
        Update(document =>
        {
            document.SerialPorts[row.PortName] = preference;
        });
    }

    public SerialPortPreference? GetSerialPreferences(string portName)
    {
        lock (_sync)
            return _document.SerialPorts.TryGetValue(portName, out var saved) ? saved : null;
    }

    public IReadOnlyList<NetworkConnectionPreset> GetNetworkPresets()
    {
        lock (_sync) return _document.NetworkPresets.OrderBy(preset => preset.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public IReadOnlyList<DebugConnectionPreset> GetDebugPresets()
    {
        lock (_sync) return _document.DebugPresets.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    public void SaveDebugPreset(DebugConnectionPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        DebugConnectionPreset.Validate(preset.Endpoint);
        var normalized = preset with { Name = preset.Name.Trim() };
        if (normalized.Name.Length is < 1 or > 64) throw new ArgumentException("预设名称长度应为 1–64 个字符。");
        Update(document =>
        {
            var index = document.DebugPresets.FindIndex(p => p.Id == normalized.Id);
            if (index < 0 && document.DebugPresets.Count >= 100) throw new InvalidOperationException("最多保存 100 个调试连接预设。");
            if (document.DebugPresets.Any(p => p.Id != normalized.Id && string.Equals(p.Name, normalized.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("已存在同名预设，请更换名称。");
            if (index < 0) document.DebugPresets.Add(normalized); else document.DebugPresets[index] = normalized;
        });
    }
    public void DeleteDebugPreset(Guid id) => Update(document => document.DebugPresets.RemoveAll(p => p.Id == id));
    internal ConfigurationMergeResult MergeDebugPresets(IEnumerable<DebugConnectionPreset> presets)
    {
        lock (_sync)
        {
            var added = 0; var skipped = 0;
            foreach (var preset in presets)
            {
                if (!IsValidDebugPreset(preset) || _document.DebugPresets.Count >= 100
                    || _document.DebugPresets.Any(p => string.Equals(p.Name, preset.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
                { skipped++; continue; }
                _document.DebugPresets.Add(preset with { Id = Guid.NewGuid(), Name = preset.Name.Trim() }); added++;
            }
            if (added > 0) MarkDirtyLocked();
            return new(added, skipped);
        }
    }
    private static bool IsValidDebugPreset(DebugConnectionPreset preset)
    {
        if (preset?.Name is not { Length: > 0 and <= 64 } || preset.Endpoint is null) return false;
        try { DebugConnectionPreset.Validate(preset.Endpoint); return true; } catch (ArgumentException) { return false; }
    }

    /// <summary>Merges imported non-secret presets without replacing local entries.</summary>
    internal ConfigurationMergeResult MergeNetworkPresets(IEnumerable<NetworkConnectionPreset> presets)
    {
        ArgumentNullException.ThrowIfNull(presets);
        lock (_sync)
        {
            var added = 0;
            var skipped = 0;
            foreach (var preset in presets)
            {
                if (preset is null || string.IsNullOrWhiteSpace(preset.Name)
                    || _document.NetworkPresets.Any(existing => string.Equals(existing.Name, preset.Name, StringComparison.OrdinalIgnoreCase))
                    || _document.NetworkPresets.Count >= 100)
                {
                    skipped++;
                    continue;
                }

                var imported = preset with { Id = Guid.NewGuid(), Name = preset.Name.Trim() };
                _document.NetworkPresets.Add(imported);
                if (!imported.TreatAsHttp && imported.Protocol != EmbedDesk.Core.Models.TransportProtocol.Mqtt)
                    MergeDebugPresets([DebugConnectionPreset.FromNetwork(imported)]);
                added++;
            }

            if (added > 0) MarkDirtyLocked();
            return new ConfigurationMergeResult(added, skipped);
        }
    }

    public void SaveNetworkPreset(NetworkConnectionPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var normalized = preset with
        {
            Name = preset.Name.Trim(),
            RemoteHost = preset.RemoteHost.Trim(),
            LocalHost = preset.LocalHost.Trim(),
            ClientLocalAddress = preset.ClientLocalAddress?.Trim() ?? string.Empty,
            MqttTopic = preset.MqttTopic.Trim(),
            MqttUsername = preset.MqttUsername.Trim()
        };
        if (normalized.Name.Length is < 1 or > 64) throw new ArgumentException("预设名称长度应为 1–64 个字符。", nameof(preset));
        if (!Enum.IsDefined(normalized.Protocol) || !Enum.IsDefined(normalized.Mode)
            || normalized.LocalPort is < 0 or > 65535 || normalized.RemotePort is < 1 or > 65535)
            throw new ArgumentException("网络预设包含无效的协议、模式或端口。", nameof(preset));
        if (normalized.Protocol == EmbedDesk.Core.Models.TransportProtocol.Mqtt && string.IsNullOrWhiteSpace(normalized.MqttTopic))
            throw new ArgumentException("MQTT 预设必须包含主题。", nameof(preset));
        Update(document =>
        {
            var index = document.NetworkPresets.FindIndex(existing => existing.Id == normalized.Id);
            if (index < 0 && document.NetworkPresets.Count >= 100) throw new InvalidOperationException("最多保存 100 个网络连接预设。");
            if (document.NetworkPresets.Any(existing => existing.Id != normalized.Id && string.Equals(existing.Name, normalized.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("已存在同名预设，请更换名称。");
            if (index < 0) document.NetworkPresets.Add(normalized);
            else document.NetworkPresets[index] = normalized;
        });
    }

    public void DeleteNetworkPreset(Guid id) => Update(document => document.NetworkPresets.RemoveAll(preset => preset.Id == id));

    public IReadOnlyList<SerialBridgeClientPreset> GetSerialBridgePresets()
    {
        lock (_sync) return _document.SerialBridgePresets.OrderBy(preset => preset.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    /// <summary>Merges imported network serial presets without replacing local entries.</summary>
    internal ConfigurationMergeResult MergeSerialBridgePresets(IEnumerable<SerialBridgeClientPreset> presets)
    {
        ArgumentNullException.ThrowIfNull(presets);
        lock (_sync)
        {
            var added = 0;
            var skipped = 0;
            foreach (var preset in presets)
            {
                if (preset is null || string.IsNullOrWhiteSpace(preset.Name)
                    || _document.SerialBridgePresets.Any(existing => string.Equals(existing.Name, preset.Name, StringComparison.OrdinalIgnoreCase))
                    || _document.SerialBridgePresets.Count >= 100)
                {
                    skipped++;
                    continue;
                }

                _document.SerialBridgePresets.Add(preset with { Id = Guid.NewGuid(), Name = preset.Name.Trim(), Host = preset.Host.Trim() });
                added++;
            }

            if (added > 0) MarkDirtyLocked();
            return new ConfigurationMergeResult(added, skipped);
        }
    }

    public void SaveSerialBridgePreset(SerialBridgeClientPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var normalized = preset with { Name = preset.Name.Trim(), Host = preset.Host.Trim() };
        if (normalized.Name.Length is < 1 or > 64) throw new ArgumentException("预设名称长度应为 1–64 个字符。", nameof(preset));
        if (string.IsNullOrWhiteSpace(normalized.Host) || normalized.Port is < 1 or > 65535)
            throw new ArgumentException("请填写有效的网络串口服务地址和端口。", nameof(preset));
        Update(document =>
        {
            var index = document.SerialBridgePresets.FindIndex(existing => existing.Id == normalized.Id);
            if (index < 0 && document.SerialBridgePresets.Count >= 100) throw new InvalidOperationException("最多保存 100 个网络串口预设。");
            if (document.SerialBridgePresets.Any(existing => existing.Id != normalized.Id && string.Equals(existing.Name, normalized.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("已存在同名预设，请更换名称。");
            if (index < 0) document.SerialBridgePresets.Add(normalized);
            else document.SerialBridgePresets[index] = normalized;
        });
    }

    public void DeleteSerialBridgePreset(Guid id) => Update(document => document.SerialBridgePresets.RemoveAll(preset => preset.Id == id));

    public IReadOnlyList<StcpTunnelProfile> GetStcpTunnels()
    {
        lock (_sync) return _document.StcpTunnels.OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public void SaveStcpTunnel(StcpTunnelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var normalized = profile with
        {
            Name = profile.Name.Trim(),
            ServerAddress = profile.ServerAddress.Trim(),
            ServiceName = profile.ServiceName.Trim(),
            TargetHost = profile.TargetHost.Trim(),
            CredentialTarget = profile.CredentialTarget.Trim()
        };
        normalized.Validate();
        if (normalized.Id == Guid.Empty) throw new ArgumentException("STCP 配置编号无效。", nameof(profile));
        if (normalized.CredentialTarget.Length > 0 && normalized.CredentialTarget != StcpTunnelProfile.CreateCredentialTarget(normalized.Id))
            throw new ArgumentException("STCP 凭据索引无效。", nameof(profile));

        Update(document =>
        {
            var index = document.StcpTunnels.FindIndex(existing => existing.Id == normalized.Id);
            if (document.StcpTunnels.Any(existing => existing.Id != normalized.Id
                && string.Equals(existing.Name, normalized.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("已存在同名 STCP 配置，请更换名称。");
            if (index < 0 && document.StcpTunnels.Count >= 100) throw new InvalidOperationException("最多保存 100 个 STCP 配置。");
            if (index < 0) document.StcpTunnels.Add(normalized);
            else document.StcpTunnels[index] = normalized;
        });
    }

    public void DeleteStcpTunnel(Guid id) => Update(document => document.StcpTunnels.RemoveAll(profile => profile.Id == id));

    internal ConfigurationMergeResult MergeStcpTunnels(IEnumerable<StcpTunnelProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        lock (_sync)
        {
            var added = 0;
            var skipped = 0;
            foreach (var profile in profiles)
            {
                if (profile is null || string.IsNullOrWhiteSpace(profile.Name)
                    || _document.StcpTunnels.Any(existing => string.Equals(existing.Name, profile.Name, StringComparison.OrdinalIgnoreCase))
                    || _document.StcpTunnels.Count >= 100)
                {
                    skipped++;
                    continue;
                }

                var imported = profile with { Id = Guid.NewGuid(), CredentialTarget = string.Empty };
                try { imported.Validate(); }
                catch (ArgumentException) { skipped++; continue; }
                _document.StcpTunnels.Add(imported);
                added++;
            }

            if (added > 0) MarkDirtyLocked();
            return new ConfigurationMergeResult(added, skipped);
        }
    }

    public NetworkProbePreferences GetNetworkProbePreferences()
    {
        lock (_sync) return NormalizeNetworkProbePreferences(_document.NetworkProbePreferences);
    }

    public void SaveNetworkProbePreferences(NetworkProbePreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var normalized = NormalizeNetworkProbePreferences(preferences);
        Update(document => document.NetworkProbePreferences = normalized);
    }

    public NetworkToolPreferences GetNetworkToolPreferences()
    {
        lock (_sync) return NormalizeNetworkToolPreferences(_document.NetworkToolPreferences ?? new NetworkToolPreferences());
    }

    public void SaveCidrCalculatorPreferences(CidrCalculatorPreferences preferences) => UpdateNetworkToolPreferences(
        current => current with { CidrCalculator = NormalizeCidrCalculatorPreferences(preferences) });

    public void SaveDnsQueryPreferences(DnsQueryPreferences preferences) => UpdateNetworkToolPreferences(
        current => current with { DnsQuery = NormalizeDnsQueryPreferences(preferences) });

    public void SaveTcpPortScanPreferences(TcpPortScanPreferences preferences) => UpdateNetworkToolPreferences(
        current => current with { TcpPortScan = NormalizeTcpPortScanPreferences(preferences) });

    public void SaveTlsDiagnosticPreferences(TlsDiagnosticPreferences preferences) => UpdateNetworkToolPreferences(
        current => current with { TlsDiagnostic = NormalizeTlsDiagnosticPreferences(preferences) });

    public void SaveIperf3ToolPreferences(Iperf3ToolPreferences preferences) => UpdateNetworkToolPreferences(
        current => current with { Iperf3 = NormalizeIperf3ToolPreferences(preferences) });

    public void SaveNetworkDiscoveryPreferences(NetworkDiscoveryPreferences preferences) => UpdateNetworkToolPreferences(
        current => current with { NetworkDiscovery = NormalizeNetworkDiscoveryPreferences(preferences) });

    public void SaveNatDetectionPreferences(NatDetectionPreferences preferences) => UpdateNetworkToolPreferences(
        current => current with { NatDetection = NormalizeNatDetectionPreferences(preferences) });

    public void SaveUpnpDiscoveryPreferences(UpnpDiscoveryPreferences preferences) => UpdateNetworkToolPreferences(
        current => current with { UpnpDiscovery = NormalizeUpnpDiscoveryPreferences(preferences) });

    public void SaveNow() { lock (_writeSync) SaveCore(); }
    public void SaveNowOrThrow() { lock (_writeSync) SaveCore(throwOnFailure: true); }
    private void SaveCore(bool throwOnFailure = false)
    {
        AppSettingsDocument snapshot;
        lock (_sync)
        {
            if (_disposed) return;
            _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
            if (!_dirty) return;
            _dirty = false;
            snapshot = Clone(_document);
        }

        var directory = Path.GetDirectoryName(_path)!;
        var temporaryPath = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_path)) File.Replace(temporaryPath, _path, null);
            else File.Move(temporaryPath, _path);
        }
        catch (IOException) { MarkDirty(); TryDeleteTemporary(temporaryPath); if (throwOnFailure) throw; }
        catch (UnauthorizedAccessException) { MarkDirty(); TryDeleteTemporary(temporaryPath); if (throwOnFailure) throw; }
    }

    private void MarkDirtyLocked()
    {
        if (_disposed) return;
        _document.SchemaVersion = CurrentSchemaVersion;
        _dirty = true;
        _saveTimer.Change(400, Timeout.Infinite);
    }

    public void Dispose()
    {
        lock (_writeSync)
        {
            SaveCore(); lock (_sync) { _disposed = true; _saveTimer.Dispose(); }
        }
    }

    public static void ApplySerialSettings(SerialPortPreference? preference, ViewModels.SerialPortRowViewModel row)
    {
        if (preference is null) return;
        if (preference.BaudRate is >= 50 and <= 4_000_000) row.BaudRate = preference.BaudRate;
        row.CustomBaudRate = preference.CustomBaudRate is >= 0 and <= 4_000_000 ? preference.CustomBaudRate : 0;
        if (preference.DataBits is >= 5 and <= 8) row.DataBits = preference.DataBits;
        if (Enum.IsDefined(preference.Parity)) row.Parity = preference.Parity;
        if (Enum.IsDefined(preference.StopBits) && preference.StopBits != StopBits.None) row.StopBits = preference.StopBits;
        if (Enum.IsDefined(preference.Handshake)) row.Handshake = preference.Handshake;
    }

    private static AppSettingsDocument Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new AppSettingsDocument();
            using var stream = File.OpenRead(path);
            var document = JsonSerializer.Deserialize<AppSettingsDocument>(stream, JsonOptions);
            if (document is null || document.SchemaVersion is < 1 or > CurrentSchemaVersion) return new AppSettingsDocument();
            if (document.SchemaVersion == 1) document.LastWorkspaceIndex = Math.Clamp(document.LastWorkspaceIndex, 0, 4) + 1;
            if (document.SchemaVersion < 3)
            {
                document.LastWorkspaceIndex = document.LastWorkspaceIndex switch { 3 => 1, >= 4 => document.LastWorkspaceIndex - 1, _ => document.LastWorkspaceIndex };
                document.DebugPresets = (document.NetworkPresets ?? []).Where(p => IsValidNetworkPreset(p) && !p.TreatAsHttp && p.Protocol != EmbedDesk.Core.Models.TransportProtocol.Mqtt)
                    .Select(DebugConnectionPreset.FromNetwork).ToList();
            }
            document.SchemaVersion = CurrentSchemaVersion;
            document.SerialPorts = new Dictionary<string, SerialPortPreference>(document.SerialPorts ?? new Dictionary<string, SerialPortPreference>(), StringComparer.OrdinalIgnoreCase);
            document.LastWorkspaceIndex = Math.Clamp(document.LastWorkspaceIndex, 0, 4);
            document.DebugPresets = (document.DebugPresets ?? []).Where(IsValidDebugPreset).DistinctBy(p => p.Id).Take(100).ToList();
            document.ConnectionPreferences = NormalizeConnectionPreferences(document.ConnectionPreferences ?? new());
            document.RecentDeviceIds = (document.RecentDeviceIds ?? []).Where(s => s is { Length: 12 } && s.All(Uri.IsHexDigit)).Distinct().Take(20).ToList();
            document.PortMappingRules = (document.PortMappingRules ?? []).Where(r => r is not null && Guid.TryParseExact(r.Id, "N", out _) && r.DeviceId is { Length: 12 } && r.DeviceId.All(Uri.IsHexDigit) && Guid.TryParseExact(r.ServiceId, "N", out _) && r.ServiceName is { Length: > 0 and <= 64 } && r.HasValidTarget).DistinctBy(r => r.Id).Take(128).ToList();
            document.SharedServices = (document.SharedServices ?? []).Where(s => { if (s is null) return false; try { s.Validate(); return true; } catch (ArgumentException) { return false; } }).DistinctBy(s => s.Id).Take(128).ToList();
            document.RemoteSessionPaneWidth = Math.Clamp(document.RemoteSessionPaneWidth, 160, 236);
            document.NetworkSessionPaneWidth = Math.Clamp(document.NetworkSessionPaneWidth, 180, 280);
            document.NetworkPresets = (document.NetworkPresets ?? new List<NetworkConnectionPreset>()).Where(IsValidNetworkPreset).Take(100).ToList();
            document.SerialBridgePresets = (document.SerialBridgePresets ?? new List<SerialBridgeClientPreset>())
                .Where(preset => preset is not null && !string.IsNullOrWhiteSpace(preset.Name) && preset.Name.Length <= 64
                    && !string.IsNullOrWhiteSpace(preset.Host) && preset.Port is >= 1 and <= 65535)
                .Take(100).ToList();
            document.StcpTunnels = (document.StcpTunnels ?? new List<StcpTunnelProfile>())
                .Where(IsValidStcpProfile)
                .GroupBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).Take(100).ToList();
            document.NetworkDisplayPreferences ??= new NetworkDisplayPreferences();
            document.NetworkProbePreferences = NormalizeNetworkProbePreferences(document.NetworkProbePreferences ?? new NetworkProbePreferences());
            document.NetworkToolPreferences = NormalizeNetworkToolPreferences(document.NetworkToolPreferences ?? new NetworkToolPreferences());
            if (!double.IsFinite(document.WindowWidth) || document.WindowWidth < 640) document.WindowWidth = 1340;
            if (!double.IsFinite(document.WindowHeight) || document.WindowHeight < 480) document.WindowHeight = 940;
            if (document.WindowLeft is { } left && !double.IsFinite(left)) document.WindowLeft = null;
            if (document.WindowTop is { } top && !double.IsFinite(top)) document.WindowTop = null;
            if (!Enum.IsDefined(document.WindowState)) document.WindowState = System.Windows.WindowState.Normal;
            if (document.WindowState == System.Windows.WindowState.Minimized) document.WindowState = System.Windows.WindowState.Normal;
            return document;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettingsDocument();
        }
    }

    private static AppSettingsDocument Clone(AppSettingsDocument source) => new()
    {
        SchemaVersion = CurrentSchemaVersion,
        WindowWidth = source.WindowWidth,
        WindowHeight = source.WindowHeight,
        WindowLeft = source.WindowLeft,
        WindowTop = source.WindowTop,
        WindowState = source.WindowState,
        LastWorkspaceIndex = source.LastWorkspaceIndex,
        ConnectionPreferences = source.ConnectionPreferences with { RelayServers = (source.ConnectionPreferences.RelayServers ?? []).ToList() },
        SharedServices = source.SharedServices.ToList(),
        RecentDeviceIds = source.RecentDeviceIds.Take(20).ToList(),
        PortMappingRules = source.PortMappingRules.ToList(),
        RemoteSessionPaneWidth = source.RemoteSessionPaneWidth,
        RemoteSessionPaneExpanded = source.RemoteSessionPaneExpanded,
        SerialPorts = (source.SerialPorts ?? new Dictionary<string, SerialPortPreference>()).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
        NetworkPresets = (source.NetworkPresets ?? new List<NetworkConnectionPreset>()).ToList(),
        DebugPresets = (source.DebugPresets ?? []).ToList(),
        SerialBridgePresets = (source.SerialBridgePresets ?? new List<SerialBridgeClientPreset>()).ToList(),
        StcpTunnels = (source.StcpTunnels ?? new List<StcpTunnelProfile>()).ToList(),
        NetworkSessionPaneWidth = source.NetworkSessionPaneWidth,
        NetworkSessionPaneExpanded = source.NetworkSessionPaneExpanded,
        NetworkDisplayPreferences = source.NetworkDisplayPreferences ?? new NetworkDisplayPreferences(),
        NetworkProbePreferences = source.NetworkProbePreferences ?? new NetworkProbePreferences(),
        NetworkToolPreferences = NormalizeNetworkToolPreferences(source.NetworkToolPreferences ?? new NetworkToolPreferences()),
    };

    private static ConnectionPreferences NormalizeConnectionPreferences(ConnectionPreferences value)
    {
        var legacyUrl = value.ServerUrl?.Trim() ?? string.Empty;
        var servers = (value.RelayServers ?? [])
            .Where(profile => profile is not null && IsValidRelayProfile(profile))
            .GroupBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First() with { Name = group.First().Name.Trim(), Url = group.First().Url.Trim().TrimEnd('/') })
            .Take(64)
            .ToList();
        foreach (var profile in RelayServerCatalog.BuiltInServers)
        {
            servers.RemoveAll(existing => existing.Id == profile.Id || string.Equals(existing.Url, profile.Url, StringComparison.OrdinalIgnoreCase));
            servers.Add(profile);
        }
        if (servers.Count == 0 && IsValidRelayUrl(legacyUrl))
            servers.Add(new RelayServerProfile { Name = "自定义中继", Url = legacyUrl.TrimEnd('/') });
        var defaultId = value.DefaultRelayServerId ?? string.Empty;
        if (servers.All(profile => !string.Equals(profile.Id, defaultId, StringComparison.OrdinalIgnoreCase)))
            defaultId = servers.FirstOrDefault()?.Id ?? string.Empty;
        var defaultUrl = servers.FirstOrDefault(profile => string.Equals(profile.Id, defaultId, StringComparison.OrdinalIgnoreCase))?.Url ?? legacyUrl;
        return value with
        {
            ServerUrl = defaultUrl,
            RelayServers = servers,
            DefaultRelayServerId = defaultId,
            ProxyUrl = value.ProxyUrl ?? "", ProxyUser = value.ProxyUser ?? "", TrustedRootFile = value.TrustedRootFile ?? "",
            ProxyMode = value.ProxyMode is "Direct" or "System" or "Custom" ? value.ProxyMode : "Direct",
            TimeoutSeconds = Math.Clamp(value.TimeoutSeconds, 2, 120), RetrySeconds = Math.Clamp(value.RetrySeconds, 1, 300),
            CloseAction = Enum.IsDefined(value.CloseAction) ? value.CloseAction : MainWindowCloseAction.Ask
        };
    }

    private static bool IsValidRelayProfile(RelayServerProfile? profile) => profile is not null
        && profile.Id is { Length: > 0 and <= 64 }
        && profile.Name is { Length: > 0 and <= 64 } && !profile.Name.Any(char.IsControl)
        && IsValidRelayUrl(profile.Url);

    private static bool IsValidRelayUrl(string? value)
        => RelayServerProfile.TryNormalizeUrl(value, out _);

    private void UpdateNetworkToolPreferences(Func<NetworkToolPreferences, NetworkToolPreferences> update)
    {
        lock (_sync)
        {
            var current = NormalizeNetworkToolPreferences(_document.NetworkToolPreferences ?? new NetworkToolPreferences());
            _document.NetworkToolPreferences = update(current);
            _document.SchemaVersion = CurrentSchemaVersion;
            _dirty = true;
            _saveTimer.Change(400, Timeout.Infinite);
        }
    }

    private static NetworkToolPreferences NormalizeNetworkToolPreferences(NetworkToolPreferences preferences) => new()
    {
        CidrCalculator = NormalizeCidrCalculatorPreferences(preferences.CidrCalculator ?? new CidrCalculatorPreferences()),
        DnsQuery = NormalizeDnsQueryPreferences(preferences.DnsQuery ?? new DnsQueryPreferences()),
        TcpPortScan = NormalizeTcpPortScanPreferences(preferences.TcpPortScan ?? new TcpPortScanPreferences()),
        TlsDiagnostic = NormalizeTlsDiagnosticPreferences(preferences.TlsDiagnostic ?? new TlsDiagnosticPreferences()),
        Iperf3 = NormalizeIperf3ToolPreferences(preferences.Iperf3 ?? new Iperf3ToolPreferences()),
        NetworkDiscovery = NormalizeNetworkDiscoveryPreferences(preferences.NetworkDiscovery ?? new NetworkDiscoveryPreferences()),
        NatDetection = NormalizeNatDetectionPreferences(preferences.NatDetection ?? new NatDetectionPreferences()),
        UpnpDiscovery = NormalizeUpnpDiscoveryPreferences(preferences.UpnpDiscovery ?? new UpnpDiscoveryPreferences())
    };

    private static CidrCalculatorPreferences NormalizeCidrCalculatorPreferences(CidrCalculatorPreferences preferences) => preferences with
    {
        Input = NormalizeText(preferences.Input, "192.168.1.0/24", 128)
    };

    private static DnsQueryPreferences NormalizeDnsQueryPreferences(DnsQueryPreferences preferences)
    {
        var doh = NormalizeText(preferences.DohEndpoint, "https://dns.alidns.com/dns-query", 2048);
        if (!Uri.TryCreate(doh, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps || endpoint.UserInfo.Length > 0 || endpoint.Fragment.Length > 0)
            doh = "https://dns.alidns.com/dns-query";
        var dnsServer = NormalizeText(preferences.DnsServer, string.Empty, 255);
        if (!IPAddress.TryParse(dnsServer, out _)) dnsServer = string.Empty;
        var recordType = NormalizeText(preferences.RecordType, "自动（域名 A / IP PTR）", 32);
        return preferences with
        {
            Query = NormalizeText(preferences.Query, "example.com", 255),
            RecordType = recordType,
            QueryClassIndex = Math.Clamp(preferences.QueryClassIndex, 0, 2),
            TransportIndex = Math.Clamp(preferences.TransportIndex, 0, 2),
            DnsServer = dnsServer,
            DohEndpoint = doh,
            Port = Math.Clamp(preferences.Port, 1, 65535),
            TimeoutMilliseconds = Math.Clamp(preferences.TimeoutMilliseconds, 100, 60000),
            Retries = Math.Clamp(preferences.Retries, 0, 5)
        };
    }

    private static TcpPortScanPreferences NormalizeTcpPortScanPreferences(TcpPortScanPreferences preferences) => preferences with
    {
        Host = NormalizeText(preferences.Host, "127.0.0.1", 255),
        Ports = NormalizeText(preferences.Ports, "22,80,443,3389,5900,8080", 2048),
        AddressFamilyIndex = Math.Clamp(preferences.AddressFamilyIndex, 0, 2),
        TimeoutMilliseconds = Math.Clamp(preferences.TimeoutMilliseconds, 100, 60000),
        Concurrency = Math.Clamp(preferences.Concurrency, 1, 256)
    };

    private static TlsDiagnosticPreferences NormalizeTlsDiagnosticPreferences(TlsDiagnosticPreferences preferences) => preferences with
    {
        Host = NormalizeText(preferences.Host, "example.com", 255),
        Port = Math.Clamp(preferences.Port, 1, 65535),
        ServerName = NormalizeText(preferences.ServerName, string.Empty, 255),
        TimeoutMilliseconds = Math.Clamp(preferences.TimeoutMilliseconds, 100, 60000)
    };

    private static Iperf3ToolPreferences NormalizeIperf3ToolPreferences(Iperf3ToolPreferences preferences)
    {
        var protocolIndex = Math.Clamp(preferences.ProtocolIndex, 0, 1);
        var rateUnitIndex = Math.Clamp(preferences.RateUnitIndex, 0, 4);
        return preferences with
        {
            ModeIndex = Math.Clamp(preferences.ModeIndex, 0, 1),
            Host = NormalizeText(preferences.Host, "127.0.0.1", 255),
            Port = Math.Clamp(preferences.Port, 1, 65535),
            ProtocolIndex = protocolIndex,
            DirectionIndex = Math.Clamp(preferences.DirectionIndex, 0, 1),
            DurationSeconds = Math.Clamp(preferences.DurationSeconds, 1, 3600),
            BlockSizeBytes = Math.Clamp(preferences.BlockSizeBytes, 1, 1048576),
            Rate = NormalizeText(preferences.Rate, "20", 24),
            RateUnitIndex = protocolIndex == 0 && rateUnitIndex == 4 ? 0 : rateUnitIndex
        };
    }

    private static NetworkDiscoveryPreferences NormalizeNetworkDiscoveryPreferences(NetworkDiscoveryPreferences preferences)
    {
        var address = NormalizeText(preferences.LocalAddress, string.Empty, 64);
        if (!IPAddress.TryParse(address, out var parsed) || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) address = string.Empty;
        return preferences with { LocalAddress = address, Concurrency = Math.Clamp(preferences.Concurrency, 1, 256) };
    }

    private static NatDetectionPreferences NormalizeNatDetectionPreferences(NatDetectionPreferences preferences) => preferences with
    {
        Servers = NormalizeMultilineText(preferences.Servers, 2048)
    };

    private static UpnpDiscoveryPreferences NormalizeUpnpDiscoveryPreferences(UpnpDiscoveryPreferences preferences) => preferences with
    {
        SearchTypeIndex = Math.Clamp(preferences.SearchTypeIndex, 0, 2)
    };

    private static string NormalizeText(string? value, string fallback, int maximumLength)
    {
        var text = value?.Trim() ?? string.Empty;
        return text.Length is > 0 && text.Length <= maximumLength && !text.Any(char.IsControl) ? text : fallback;
    }

    private static string NormalizeMultilineText(string? value, int maximumLength)
    {
        var text = value?.Trim() ?? string.Empty;
        return text.Length <= maximumLength && !text.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t') ? text : string.Empty;
    }

    private static NetworkProbePreferences NormalizeNetworkProbePreferences(NetworkProbePreferences preferences)
    {
        var host = preferences.Host?.Trim() ?? string.Empty;
        var localAddress = preferences.LocalAddress?.Trim() ?? string.Empty;
        return preferences with
        {
            Host = host.Length is > 0 and <= 255 && !host.Any(char.IsControl) ? host : "127.0.0.1",
            Mode = Enum.IsDefined(preferences.Mode) ? preferences.Mode : NetworkProbeToolMode.Icmp,
            AddressFamily = Enum.IsDefined(preferences.AddressFamily) ? preferences.AddressFamily : EmbedDesk.Core.Abstractions.NetworkProbeAddressFamily.Any,
            LocalAddress = IPAddress.TryParse(localAddress, out _) ? localAddress : string.Empty,
            TcpPort = Math.Clamp(preferences.TcpPort, 1, 65535),
            PacketSize = Math.Clamp(preferences.PacketSize, 0, 65500),
            Count = Math.Clamp(preferences.Count, 0, 1_000_000),
            IntervalMilliseconds = Math.Clamp(preferences.IntervalMilliseconds, 100, 60_000),
            MaxHops = Math.Clamp(preferences.MaxHops, 1, 255),
            TimeoutMilliseconds = Math.Clamp(preferences.TimeoutMilliseconds, 100, 10_000)
        };
    }

    private static bool IsValidNetworkPreset(NetworkConnectionPreset? preset) => preset is not null
        && preset.Id != Guid.Empty && preset.Name.Length is > 0 and <= 64
        && Enum.IsDefined(preset.Protocol) && Enum.IsDefined(preset.Mode)
        && preset.LocalPort is >= 0 and <= 65535 && preset.RemotePort is >= 1 and <= 65535
        && (string.IsNullOrWhiteSpace(preset.ClientLocalAddress) || IPAddress.TryParse(preset.ClientLocalAddress, out _))
        && (preset.Protocol != EmbedDesk.Core.Models.TransportProtocol.Mqtt || !string.IsNullOrWhiteSpace(preset.MqttTopic));

    private static bool IsValidStcpProfile(StcpTunnelProfile? profile)
    {
        if (profile is null || profile.Id == Guid.Empty || profile.Name is not { Length: > 0 and <= 64 }
            || (!string.IsNullOrEmpty(profile.CredentialTarget) && profile.CredentialTarget != StcpTunnelProfile.CreateCredentialTarget(profile.Id))) return false;
        try { profile.Validate(); return true; }
        catch (ArgumentException) { return false; }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private void MarkDirty()
    {
        lock (_sync) _dirty = true;
    }

    private static void TryDeleteTemporary(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed class AppSettingsDocument
{
    public int SchemaVersion { get; set; } = 3;
    public ConnectionPreferences ConnectionPreferences { get; set; } = new();
    public List<EmbedDesk.Infrastructure.Connections.SharedDeviceService> SharedServices { get; set; } = [];
    public List<string> RecentDeviceIds { get; set; } = [];
    public List<ViewModels.PortMappingRule> PortMappingRules { get; set; } = [];
    public double WindowWidth { get; set; } = 1340;
    public double WindowHeight { get; set; } = 940;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public System.Windows.WindowState WindowState { get; set; } = System.Windows.WindowState.Normal;
    public int LastWorkspaceIndex { get; set; }
    public double RemoteSessionPaneWidth { get; set; } = 236;
    public bool RemoteSessionPaneExpanded { get; set; } = true;
    public Dictionary<string, SerialPortPreference> SerialPorts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<NetworkConnectionPreset> NetworkPresets { get; set; } = [];
    public List<DebugConnectionPreset> DebugPresets { get; set; } = [];
    public List<SerialBridgeClientPreset> SerialBridgePresets { get; set; } = [];
    public List<StcpTunnelProfile> StcpTunnels { get; set; } = [];
    public double NetworkSessionPaneWidth { get; set; } = 210;
    public bool NetworkSessionPaneExpanded { get; set; } = true;
    public NetworkDisplayPreferences NetworkDisplayPreferences { get; set; } = new();
    public NetworkProbePreferences NetworkProbePreferences { get; set; } = new();
    public NetworkToolPreferences NetworkToolPreferences { get; set; } = new();
}

internal sealed class SerialPortPreference
{
    public int BaudRate { get; set; } = 115200;
    public int CustomBaudRate { get; set; }
    public int DataBits { get; set; } = 8;
    public Parity Parity { get; set; } = Parity.None;
    public StopBits StopBits { get; set; } = StopBits.One;
    public Handshake Handshake { get; set; } = Handshake.None;
}
