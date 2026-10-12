namespace ArkServerManager.Core;

/// <summary>Bare legacy entries target ServerSettings; qualified entries preserve their file and section.</summary>
public static class PresetConfigurationEntries
{
    public static (string UserSettings, string GameSettings) Apply(string userSettings, string gameSettings,
        IReadOnlyDictionary<string, string> entries)
    {
        var user = new IniDocument(userSettings); var game = new IniDocument(gameSettings);
        foreach (var entry in entries)
        {
            if (entry.Key.Any(char.IsControl) || entry.Value is null || entry.Value.Any(c => char.IsControl(c) && c != '\t'))
                throw new ArgumentException("Preset configuration entries must fit on one line.");
            var parts = entry.Key.Split("::", StringSplitOptions.None);
            if (parts.Length == 1) user.SetValues("ServerSettings", parts[0], [entry.Value]);
            else if (parts.Length == 3 && parts[0] is "GameUserSettings.ini" or "Game.ini")
                (parts[0] == "Game.ini" ? game : user).SetValues(parts[1], parts[2], [entry.Value]);
            else throw new ArgumentException("Use File.ini::Section::Setting for a qualified preset entry.");
        }
        return (user.ToString(), game.ToString());
    }
}
