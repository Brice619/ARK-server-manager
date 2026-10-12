namespace ArkServerManager.Core;
public sealed record ClusterInheritanceReset(ServerPreset Preset, List<ClusterSettingOverride> Removals);
public static class ClusterInheritance
{
    public static ClusterInheritanceReset BuildReset(ServerDefinition server, ClusterOverrideSave edit, ServerPreset shared, string userSource, string gameSource)
    {
        if (server.Id != edit.Expected.ServerId || server.ClusterId != edit.Expected.ClusterId || shared.Edition != server.Edition)
            throw new ArgumentException("Review inherited settings for the same cluster map.");
        ClusterSettingOverrides.Validate(edit.Expected.Overrides); ClusterSettingOverrides.Validate(edit.Overrides);
        var templates = PresetConfigurationEntries.Apply(shared.UserSettings ?? "", shared.GameSettings ?? "", shared.Settings.Values);
        var sharedUser = new IniDocument(templates.UserSettings); var sharedGame = new IniDocument(templates.GameSettings);
        var user = new IniDocument(""); var game = new IniDocument("");
        var removals = new List<ClusterSettingOverride>();
        foreach (var key in edit.Expected.Overrides.Where(key => !ClusterSettingOverrides.Contains(edit.Overrides, key.FileName, key.Section, key.Key)))
        {
            var values = (key.FileName == "Game.ini" ? sharedGame : sharedUser).GetValues(key.Section, key.Key);
            (key.FileName == "Game.ini" ? game : user).SetValues(key.Section, key.Key, values);
            if (values.Count == 0) removals.Add(key);
        }
        var settings = server.Settings.Copy() with { Values = [], MaxPlayers = edit.Expected.MaxPlayers && !edit.MaxPlayers ? shared.Settings.MaxPlayers : server.Settings.MaxPlayers };
        var mods = edit.Expected.Mods && !edit.Mods ? shared.Mods : server.Mods;
        return new(new ServerPreset("Inherited settings for " + server.Settings.Name, server.Edition, settings, [.. mods])
            { UserSettings = user.ToString(), GameSettings = game.ToString() }, removals);
    }
}
