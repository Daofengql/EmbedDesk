using System.IO;
using System.Text.Json;
using EmbedDesk.Core.Models;

namespace EmbedDesk.App;

internal sealed record HostsProfile
{
    public string Name { get; init; } = "默认方案";
    public HostsMapping[] Rules { get; init; } = [];
}

internal sealed record HostsManagerSettings
{
    public int SchemaVersion { get; init; } = 2;
    public int SelectedProfile { get; init; }
    public HostsProfile[] Profiles { get; init; } = [new()];
}

/// <summary>Explicit local drafts, kept separate from transferable connection settings.</summary>
internal sealed class HostsManagerSettingsStore(string directory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _path = Path.Combine(directory, "hosts-manager.json");
    private byte[]? _loaded;
    public bool DisabledLegacySubdomains { get; private set; }
    public string BackupDirectory => Path.Combine(directory, "hosts-backups");

    public HostsManagerSettings Load()
    {
        if (!File.Exists(_path)) { _loaded = null; return new(); }
        _loaded = ReadBounded(_path);
        var settings = Parse(_loaded, out var disabled);
        DisabledLegacySubdomains = disabled;
        return settings;
    }

    public void Save(HostsManagerSettings settings)
    {
        Validate(settings);
        Directory.CreateDirectory(directory);
        // Serialize multiple windows/processes; reject stale drafts rather than overwriting them.
        using var guard = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var current = File.Exists(_path) ? ReadBounded(_path) : null;
        if ((_loaded is null) != (current is null) || (_loaded is not null && !(_loaded.AsSpan().SequenceEqual(current))))
            throw new IOException("方案已在其他窗口中更改。请先导出当前草稿，再关闭并重新打开管理器。");
        var bytes = Serialize(settings);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(_path)) File.Replace(temp, _path, _path + ".bak");
            else File.Move(temp, _path);
            _loaded = bytes;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static byte[] ReadBounded(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("配置文件超过 4 MiB。");
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return bytes;
    }

    public static HostsManagerSettings Parse(byte[] bytes) => Parse(bytes, out _);

    public static HostsManagerSettings Parse(byte[] bytes, out bool disabledLegacySubdomains)
    {
        if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("配置文件超过 4 MiB。");
        var settings = JsonSerializer.Deserialize<HostsManagerSettings>(bytes, JsonOptions) ?? throw new InvalidDataException("配置为空。");
        if (settings.SchemaVersion is not (1 or 2)) throw new InvalidDataException("配置版本不受支持。");
        settings = settings with { SchemaVersion = 2 };
        disabledLegacySubdomains = false;
        // Keep old rules, but never reinterpret an enabled wildcard as a silently active exact rule.
        using var document = JsonDocument.Parse(bytes);
        var profiles = Property(document.RootElement, "Profiles");
        if (profiles.ValueKind == JsonValueKind.Array && settings.Profiles is not null)
        {
            var index = 0;
            foreach (var profile in profiles.EnumerateArray())
            {
                var rules = Property(profile, "Rules");
                if (rules.ValueKind == JsonValueKind.Array && settings.Profiles[index]?.Rules is { } mappings)
                {
                    var ruleIndex = 0;
                    foreach (var rule in rules.EnumerateArray())
                    {
                        if (Property(rule, "MatchSubdomains").ValueKind == JsonValueKind.True && mappings[ruleIndex] is { } mapping)
                        {
                            mappings[ruleIndex] = mapping with { Enabled = false };
                            disabledLegacySubdomains = true;
                        }
                        ruleIndex++;
                    }
                }
                index++;
            }
        }
        Validate(settings); return settings;
    }

    private static JsonElement Property(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object
        ? element.EnumerateObject().LastOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value : default;
    public static byte[] Serialize(HostsManagerSettings settings) { Validate(settings); return JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions); }
    private static void Validate(HostsManagerSettings settings)
    {
        if (settings.SchemaVersion != 2 || settings.Profiles is not { Length: > 0 and <= 50 }
            || settings.SelectedProfile < 0 || settings.SelectedProfile >= settings.Profiles.Length)
            throw new InvalidDataException("配置版本、方案数量或选择项无效。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in settings.Profiles)
        {
            if (profile is null || string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 64
                || profile.Name.Any(char.IsControl) || !names.Add(profile.Name.Trim()) || profile.Rules is null || profile.Rules.Any(r => r is null))
                throw new InvalidDataException("方案名称无效、重复或规则缺失。");
            _ = HostsMapping.Validate(profile.Rules);
        }
    }
}
