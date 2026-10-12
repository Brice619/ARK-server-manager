namespace ArkServerManager.Core;
[Flags]
public enum AsaClientPlatforms { PC = 1, WindowsStore = 2, XboxSeries = 4, PlayStation5 = 8, All = 15 }
public static class AsaClientPlatformArguments
{
    public static string Build(AsaClientPlatforms platforms)
    {
        if (platforms == 0 || (platforms & ~AsaClientPlatforms.All) != 0)
            throw new ArgumentException("Choose at least one supported ASA player platform.");
        if (platforms == AsaClientPlatforms.All) return "-ServerPlatform=ALL";
        var tokens = new List<string>();
        foreach (var (value, token) in new[] { (AsaClientPlatforms.PC, "PC"), (AsaClientPlatforms.WindowsStore, "WINGDK"),
            (AsaClientPlatforms.XboxSeries, "XSX"), (AsaClientPlatforms.PlayStation5, "PS5") })
            if (platforms.HasFlag(value)) tokens.Add(token);
        return "-ServerPlatform=" + string.Join("+", tokens);
    }
}
