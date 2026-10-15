namespace ArkServerManager.Core;

/// <summary>Prepares the native ASE mod installer; ARK owns downloading and unpacking its selected Workshop items.</summary>
public sealed class AseAutomaticMods(HttpClient http)
{
    public async Task PrepareAsync(ServerDefinition server, ServerPlatform platform, CancellationToken ct)
    {
        if (server.Edition != ArkEdition.ASE || server.Mods.Count == 0) return;
        AsaModLaunchArguments.Build(server.Mods);
        if (!Enum.IsDefined(platform)) throw new ArgumentException("Choose a supported server platform.");
        var configuration = await InstallationConfigurationReader.ReadAsync(server.Directory, server.Edition, platform.ToString(), ct);
        var user = new IniDocument(configuration.GameUserSettings ?? "");
        var game = new IniDocument(configuration.Game ?? "");
        if (!user.GetValues("ServerSettings", "ActiveMods").SequenceEqual(new[] { string.Join(",", server.Mods) })
            || !game.GetValues("ModInstaller", "ModIDS").SequenceEqual(server.Mods))
            throw new InvalidOperationException("The selected mods do not match prepared server configuration. Prepare the mod settings before startup.");
        var folder = Path.Combine(server.Directory, "Engine", "Binaries", "ThirdParty", "SteamCMD",
            platform == ServerPlatform.Windows ? "Win64" : "Linux");
        RequireUnlinked(folder);
        await new SteamCmdBootstrap(http).EnsureAsync(folder, platform, ct);
        RequireUnlinked(folder);
        // Compatibility is checked when mods are selected; ARK owns their installation.
    }

    private static void RequireUnlinked(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The native mod tool location uses a linked path. Review this installation.");
    }
}
