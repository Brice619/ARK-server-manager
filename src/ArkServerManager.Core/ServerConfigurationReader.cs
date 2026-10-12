namespace ArkServerManager.Core;

public sealed record ServerConfigurationReadRequest(Guid ServerId, string FileName);
public sealed record ServerConfigurationFile(Guid ServerId, string FileName, string Platform,
    string? Text, IReadOnlyList<string> Findings)
{
    public string? SourceSHA256 { get; init; }
}

public static class ServerConfigurationReader
{
    public static async Task<ServerConfigurationFile> ReadAsync(CatalogRepository repository,
        ServerConfigurationReadRequest request, CancellationToken ct = default)
    {
        if (request.ServerId == Guid.Empty || request.FileName is not ("GameUserSettings.ini" or "Game.ini"))
            throw new ArgumentException("Choose a server and supported configuration file.");
        var catalog = await repository.ReadAsync(ct);
        var server = catalog.Servers.SingleOrDefault(s => s.Id == request.ServerId)
            ?? throw new ArgumentException("The selected server is unavailable.");
        var platforms = new InstallationDiscovery().Evaluate(server.Directory).Candidates
            .Where(c => c.Edition == server.Edition).Select(c => c.Platform).Distinct().ToArray();
        var native = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : null;
        var platform = platforms.Length == 1 ? platforms[0] : platforms.FirstOrDefault(p => p == native);
        if (platform is null)
            throw new InvalidDataException("A single server platform could not be identified. Review the installation first.");
        var configuration = await InstallationConfigurationReader.ReadAsync(server.Directory, server.Edition, platform, ct);
        var result = new ServerConfigurationFile(server.Id, request.FileName, platform,
            request.FileName == "Game.ini" ? configuration.Game : configuration.GameUserSettings, configuration.Findings)
        { SourceSHA256 = request.FileName == "Game.ini" ? configuration.GameSHA256 : configuration.GameUserSettingsSHA256 };
        if (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(result).Length > 3 * 1024 * 1024)
            throw new InvalidDataException("The configuration is too large for the editor connection.");
        return result;
    }
}
