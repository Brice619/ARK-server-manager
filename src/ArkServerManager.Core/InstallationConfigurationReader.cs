using System.Text;

namespace ArkServerManager.Core;

public sealed record InstallationConfiguration(string Directory, string Platform, string? GameUserSettings,
    string? Game, IReadOnlyList<string> Findings)
{
    public string? GameUserSettingsSHA256 { get; init; }
    public string? GameSHA256 { get; init; }
}
public sealed record InstallationConfigurationRequest(string Directory, ArkEdition Edition, string Platform);
public sealed record InstallationConfigurationSummary(string Directory, string Platform,
    IReadOnlyList<string> SessionNames, IReadOnlyList<string> MaximumPlayers,
    bool JoinPasswordConfigured, bool AdminPasswordConfigured, bool HasGameUserSettings, bool HasGame,
    IReadOnlyList<string> Findings);

/// <summary>Read-only import source. Missing files stay missing; returned text can contain passwords.</summary>
public static class InstallationConfigurationReader
{
    private const int MaximumBytes = 4 * 1024 * 1024;

    public static InstallationConfigurationSummary Summarize(InstallationConfiguration configuration)
    {
        var ini = new IniDocument(configuration.GameUserSettings ?? "");
        var names = ini.GetValues("SessionSettings", "SessionName");
        var players = ini.GetValues("ServerSettings", "MaxPlayers");
        var findings = configuration.Findings.ToList();
        if (names.Count > 1) findings.Add("Repeated session names require review.");
        if (players.Count > 1) findings.Add("Repeated player limits require review.");
        bool PasswordSet(string key) => ini.GetValues("ServerSettings", key).Any(value => value.Length > 0);
        return new(configuration.Directory, configuration.Platform, names, players,
            PasswordSet("ServerPassword"), PasswordSet("ServerAdminPassword") || PasswordSet("AdminPassword"),
            configuration.GameUserSettings is not null, configuration.Game is not null, findings);
    }

    public static async Task<InstallationConfiguration> ReadAsync(string directory, ArkEdition edition, string platform,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(directory) || !Enum.IsDefined(edition) || platform is not ("Windows" or "Linux"))
            throw new ArgumentException("Choose a valid installation and platform.");
        var root = Path.GetFullPath(directory);
        if (!new InstallationDiscovery().Evaluate(root).Candidates.Any(c => c.Edition == edition && c.Platform == platform))
            throw new InvalidDataException("No matching installation was found.");
        var folder = Path.Combine(root, "ShooterGame", "Saved", "Config", platform + "Server");
        for (var ancestor = new DirectoryInfo(folder); ancestor is not null; ancestor = ancestor.Parent)
            if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked configuration paths require separate validation.");
        var findings = new List<string>();
        async Task<(string? Text, string? SHA256)> ReadFile(string name)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(folder, name);
            if (!File.Exists(path)) { findings.Add(name + " has not been found."); return (null, null); }
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked configuration files require separate validation.");
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                8192, FileOptions.Asynchronous);
            if (input.Length > MaximumBytes) throw new InvalidDataException("The configuration file exceeds the import size limit.");
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await input.ReadAsync(buffer, ct)) > 0)
            {
                if (bytes.Length + count > MaximumBytes) throw new InvalidDataException("The configuration file exceeds the import size limit.");
                bytes.Write(buffer, 0, count);
            }
            bytes.Position = 0;
            var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes.ToArray())).ToLowerInvariant();
            using var reader = new StreamReader(bytes, new UTF8Encoding(false, true), true);
            return (await reader.ReadToEndAsync(ct), fingerprint);
        }
        var settings = await ReadFile("GameUserSettings.ini");
        var game = await ReadFile("Game.ini");
        return new(root, platform, settings.Text, game.Text, findings)
        { GameUserSettingsSHA256 = settings.SHA256, GameSHA256 = game.SHA256 };
    }
}
