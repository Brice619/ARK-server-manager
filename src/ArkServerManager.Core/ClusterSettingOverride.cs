namespace ArkServerManager.Core;
public sealed record ClusterSettingOverride(string FileName, string Section, string Key);
public static class ClusterSettingOverrides
{
    public static void Validate(IReadOnlyList<ClusterSettingOverride> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > 4096) throw new ArgumentException("Too many map setting overrides.");
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in entries)
        {
            if (item is null || item.FileName is not ("GameUserSettings.ini" or "Game.ini")
                || new[] { item.Section, item.Key }.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl) || value.IndexOfAny(['[', ']', '=']) >= 0)
                || !identities.Add(item.FileName + "\0" + item.Section + "\0" + item.Key)) throw new ArgumentException("Choose distinct configuration keys to override.");
        }
    }
    public static bool Contains(IReadOnlyList<ClusterSettingOverride> entries, string file, string section, string key)
        => entries.Any(item => item.FileName.Equals(file, StringComparison.OrdinalIgnoreCase) && item.Section.Equals(section, StringComparison.OrdinalIgnoreCase) && item.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
}
