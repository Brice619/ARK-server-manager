namespace ArkServerManager.Core;

public sealed record ClusterOverridePolicy(List<ClusterSettingOverride> Overrides, bool MaxPlayers, bool Mods);

public sealed record PresetApplicationPlan(Guid ServerId, ServerSettings Settings, List<string> Mods, string UserSettings, string GameSettings)
{
    public ClusterOverridePolicy? OverridePolicy { get; init; }
    public override string ToString() => $"Preset application for {ServerId}; configuration values redacted";
}

/// <summary>Builds a reviewable selected-map change; committing files/mod cleanup is a separate stopped-server transaction.</summary>
public static class PresetApplication
{
    private static readonly HashSet<string> PreservedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "ServerAdminPassword", "AdminPassword", "ServerPassword", "RCONEnabled", "RCONPort",
        "SessionName", "ServerName", "Port", "GamePort", "QueryPort", "ClusterId", "ClusterDirOverride",
        "AltSaveDirectoryName", "SaveWorldDirectoryName", "MapName", "ServerMap", "ActiveMods", "ModIDS"
    };

    public static PresetApplicationPlan Build(ServerDefinition server, ServerPreset preset, string userSettings, string gameSettings, bool preserveClusterOverrides = false, IReadOnlyList<ClusterSettingOverride>? removals = null)
    {
        if (preset.Edition != server.Edition) throw new ArgumentException("Choose a preset for the same ARK edition.");
        server.Settings.Validate();
        ClusterSettingOverrides.Validate(server.ClusterOverrides);
        if (preset.Settings is null || preset.Settings.MaxPlayers < 1 || preset.Mods is null)
            throw new ArgumentException("The preset settings are incomplete.");
        AsaModLaunchArguments.Build(preset.Mods);
        var mods = preserveClusterOverrides && server.OverrideClusterMods ? server.Mods : preset.Mods;
        ServerCatalog.RequireModClientCompatibility(server, mods.Count);
        var overrides = preserveClusterOverrides ? server.ClusterOverrides : [];
        var templates = PresetConfigurationEntries.Apply(preset.UserSettings ?? "", preset.GameSettings ?? "", preset.Settings.Values);
        var user = Overlay(userSettings, templates.UserSettings, "GameUserSettings.ini", overrides);
        var game = Overlay(gameSettings, templates.GameSettings, "Game.ini", overrides);
        var settings = server.Settings.Copy() with { MaxPlayers = preserveClusterOverrides && server.OverrideClusterMaxPlayers ? server.Settings.MaxPlayers : preset.Settings.MaxPlayers };
        // Identity and networking are specific to the selected existing server.
        user = new IniDocument(BasicServerConfiguration.Apply(user.ToString(), settings));
        user.SetValues("ServerSettings", "ActiveMods", server.Edition == ArkEdition.ASE && mods.Count > 0 ? [string.Join(",", mods)] : []);
        game.SetValues("ModInstaller", "ModIDS", server.Edition == ArkEdition.ASE ? mods : []);
        if (server.Edition == ArkEdition.ASE && user.GetValues("/Script/ShooterGame.ShooterGameUserSettings", "Version").Count == 0)
            user.SetValues("/Script/ShooterGame.ShooterGameUserSettings", "Version", ["5"]);
        if (removals is not null)
        {
            ClusterSettingOverrides.Validate(removals);
            foreach (var removal in removals)
            {
                if (PreservedKeys.Contains(removal.Key) || removal.Key.Equals("MaxPlayers", StringComparison.OrdinalIgnoreCase) || removal.Key.Equals("Version", StringComparison.OrdinalIgnoreCase)
                    || preserveClusterOverrides && ClusterSettingOverrides.Contains(overrides, removal.FileName, removal.Section, removal.Key))
                    throw new ArgumentException("Identity, capacity, mod and retained override settings cannot be removed through this change.");
                (removal.FileName == "Game.ini" ? game : user).SetValues(removal.Section, removal.Key, []);
            }
        }
        foreach (var key in settings.Values.Keys.Union(preset.Settings.Values.Keys, StringComparer.OrdinalIgnoreCase).ToArray())
        {
            var parts = key.Split("::", StringSplitOptions.None);
            if (PreservedKeys.Contains(parts[^1]) || parts[^1].Equals("Version", StringComparison.OrdinalIgnoreCase)) continue;
            var values = parts.Length == 1 ? user.GetValues("ServerSettings", key)
                : parts.Length == 3 ? (parts[0] == "Game.ini" ? game : user).GetValues(parts[1], parts[2]) : null;
            if (values is null) continue;
            if (values.Count == 1) settings.Values[key] = values[0];
            else settings.Values.Remove(key);
        }
        return new(server.Id, settings, [.. mods], user.ToString(), game.ToString());
    }

    private static IniDocument Overlay(string current, string template, string file, IReadOnlyList<ClusterSettingOverride> overrides)
    {
        if (ConfigurationSyntax.Check(template).Count > 0 || template.Contains('\0')) throw new ArgumentException("Preset configuration has invalid syntax.");
        var result = new IniDocument(current);
        var entries = new IniDocument(template).Entries();
        foreach (var group in entries.GroupBy(e => e.Section + "\0" + e.Key, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            if (ClusterSettingOverrides.Contains(overrides, file, first.Section, first.Key)) continue;
            if (PreservedKeys.Contains(first.Key) || first.Key.Equals("Version", StringComparison.OrdinalIgnoreCase)
                && first.Section.EndsWith("GameUserSettings", StringComparison.OrdinalIgnoreCase)) continue;
            result.SetValues(first.Section, first.Key, group.Select(e => e.Value));
        }
        return result;
    }
}
