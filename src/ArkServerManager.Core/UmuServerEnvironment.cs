namespace ArkServerManager.Core;

/// <summary>Child-only storage selection for managed ASA compatibility tools and independent prefixes.</summary>
public static class UmuServerEnvironment
{
    public static IReadOnlyDictionary<string, string?> Build(string managementRoot, Guid serverId)
    {
        if (serverId == Guid.Empty || !Path.IsPathFullyQualified(managementRoot))
            throw new ArgumentException("Choose a registered server and absolute management folder.");
        var dependencies = Path.Combine(Path.GetFullPath(managementRoot), "Dependencies", "Compatibility");
        return new Dictionary<string, string?>
        {
            ["WINEPREFIX"] = Path.Combine(dependencies, "Prefixes", serverId.ToString("N")),
            ["UMU_FOLDERS_PATH"] = Path.Combine(dependencies, "Runtime"),
            ["XDG_CACHE_HOME"] = Path.Combine(dependencies, "Cache"),
            ["PROTONPATH"] = "GE-Proton",
            ["GAMEID"] = "umu-default",
            ["STORE"] = "none",
            // Inherited launch overrides must not bypass the managed runtime or replace its verb.
            ["UMU_NO_RUNTIME"] = null,
            ["UMU_NO_PROTON"] = null,
            ["RUNTIMEPATH"] = null,
            ["UMU_CONTAINER_NSENTER"] = null,
            ["PROTON_VERB"] = "waitforexitandrun"
        };
    }
}
