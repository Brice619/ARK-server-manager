using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace ArkServerManager.Core;

public sealed record ExistingServerImportReviewRequest(string Directory, ArkEdition Edition, string Platform);
public sealed record ExistingServerImportReview(string Directory, ArkEdition Edition, string Platform,
    string Name, int MaxPlayers, int GamePort, int QueryPort, int RconPort, List<string> SavedWorlds,
    List<string> ConfiguredMods, bool AdminPasswordConfigured, bool RconEnabled, List<string> Findings, string Token);

/// <summary>Read-only, password-free source review. Saved filenames are suggestions, not an inferred active map.</summary>
public sealed class ExistingServerImportReviewService(CatalogRepository repository)
{
    public async Task<ExistingServerImportReview> ReadAsync(ExistingServerImportReviewRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Directory) || !Path.IsPathFullyQualified(request.Directory)
            || !Enum.IsDefined(request.Edition) || request.Platform is not ("Windows" or "Linux"))
            throw new ArgumentException("Choose an absolute installation folder, edition and platform.");
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Directory));
        var catalog = await repository.ReadAsync(ct);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var server in catalog.Servers)
        {
            var registered = Path.TrimEndingDirectorySeparator(Path.GetFullPath(server.Directory));
            if (directory.Equals(registered, comparison) || directory.StartsWith(registered + Path.DirectorySeparatorChar, comparison)
                || registered.StartsWith(directory + Path.DirectorySeparatorChar, comparison))
                throw new InvalidOperationException("This folder overlaps an already managed server. Open that server to edit it.");
        }
        var source = await InstallationConfigurationReader.ReadAsync(directory, request.Edition, request.Platform, ct);
        var ini = new IniDocument(source.GameUserSettings ?? "");
        var game = new IniDocument(source.Game ?? "");
        var findings = source.Findings.ToList();
        string One(string section, string key)
        {
            var values = ini.GetValues(section, key);
            if (values.Count > 1) findings.Add("Repeated " + key + " values need review.");
            return values.Count == 1 ? values[0] : "";
        }
        int Number(string key, int fallback)
        {
            var text = One("ServerSettings", key);
            if (text.Length == 0)
            {
                findings.Add(key + " is not recorded here. The shown default must be confirmed against the launch configuration.");
                return fallback;
            }
            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0) return number;
            findings.Add(key + " could not be read. Confirm it in the import review."); return fallback;
        }
        var name = One("SessionSettings", "SessionName");
        if (name.Length == 0) name = One("ServerSettings", "ServerName");
        var admin = One("ServerSettings", "ServerAdminPassword");
        if (admin.Length == 0) admin = One("ServerSettings", "AdminPassword");
        var rcon = One("ServerSettings", "RCONEnabled").Equals("True", StringComparison.OrdinalIgnoreCase);
        if (admin.Length == 0) findings.Add("An administrator password must be configured before starting through the app.");
        if (!rcon) findings.Add("The administration connection must be enabled before starting through the app.");
        var worlds = new List<string>();
        foreach (var folderName in new[] { "SavedArks", "SavedArksLocal" })
        {
            var folder = Path.Combine(directory, "ShooterGame", "Saved", folderName);
            CheckPath(folder);
            if (!System.IO.Directory.Exists(folder)) continue;
            foreach (var path in System.IO.Directory.EnumerateFiles(folder, "*.ark", SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested(); CheckPath(path);
                if (worlds.Count >= 1024) throw new InvalidDataException("Too many saved worlds to review.");
                worlds.Add(Path.GetFileNameWithoutExtension(path));
            }
        }
        var mods = ini.GetValues("ServerSettings", "ActiveMods").SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Concat(game.GetValues("ModInstaller", "ModIDS").SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            .Distinct(StringComparer.Ordinal).ToList();
        if (request.Edition == ArkEdition.ASA) findings.Add("Confirm ASA's active mod IDs from its launch configuration; cached mods are not assumed active.");
        AsaModLaunchArguments.Build(mods);
        var max = Number("MaxPlayers", 70); var port = Number("Port", 7777); var query = Number("QueryPort", 27015); var rconPort = Number("RCONPort", 32330);
        var token = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { directory, request.Edition, request.Platform, source.GameUserSettingsSHA256, source.GameSHA256, Worlds = worlds.Order(StringComparer.Ordinal).ToArray() })));
        return new(directory, request.Edition, request.Platform, name, max, port, query, rconPort,
            worlds.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList(), mods,
            admin.Length > 0, rcon, findings, token);
    }

    private static void CheckPath(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Import review must not follow linked saved-state paths.");
    }
}
