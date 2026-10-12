namespace ArkServerManager.Core;

public static class ServerMapIdentifier
{
    public static string ForLaunch(ArkEdition edition, string map)
    {
        if (!Enum.IsDefined(edition) || string.IsNullOrWhiteSpace(map)
            || map.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-')))
            throw new ArgumentException("Choose a validated map identifier before launch.");
        // Studio Wildcard's UGC documentation names this ASA entry map explicitly.
        // Custom maps must keep their supplied entry name; do not infer a suffix.
        return edition == ArkEdition.ASA && map == "TheIsland" ? "TheIsland_WP" : map;
    }
}
