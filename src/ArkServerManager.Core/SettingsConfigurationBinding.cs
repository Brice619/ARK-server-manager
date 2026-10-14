using System.Globalization;

namespace ArkServerManager.Core;

/// <summary>Configuration fields which also drive catalog-based launch arguments must stay synchronized.</summary>
public static class SettingsConfigurationBinding
{
    public const string MissingFileHash = "0000000000000000000000000000000000000000000000000000000000000000";
    public static ServerSettings BasicSettings(ServerDefinition server, string text)
    {
        var ini = new IniDocument(text);
        string Read(string section, string key, string fallback) => ini.GetValues(section, key).LastOrDefault() ?? fallback;
        int Number(string section, string key, int fallback) => int.Parse(Read(section, key, fallback.ToString(CultureInfo.InvariantCulture)), NumberStyles.None, CultureInfo.InvariantCulture);
        var result = server.Settings.Copy() with
        {
            Name = Read("SessionSettings", "SessionName", server.Settings.Name),
            MaxPlayers = Number("/Script/Engine.GameSession", "MaxPlayers", server.Settings.MaxPlayers),
            GamePort = Number("SessionSettings", "Port", server.Settings.GamePort),
            QueryPort = Number("SessionSettings", "QueryPort", server.Settings.QueryPort),
            RconPort = Number("ServerSettings", "RCONPort", server.Settings.RconPort)
        };
        result.Validate();
        return result;
    }

    public static void ValidateBasic(CatalogDocument document, Guid serverId, string text)
    {
        var catalog = new ServerCatalog();
        foreach (var cluster in document.Clusters) catalog.AddCluster(cluster);
        foreach (var server in document.Servers) catalog.AddServer(server);
        var current = document.Servers.Single(s => s.Id == serverId);
        catalog.UpdateSettings(serverId, BasicSettings(current, text));
    }

    public static async Task SynchronizeBasicAsync(CatalogRepository repository, Guid serverId, string text, CancellationToken ct)
    {
        await repository.ChangeAsync(catalog =>
        {
            var current = catalog.Servers.Single(s => s.Id == serverId);
            var desired = BasicSettings(current, text);
            if (BasicSettingsValues.From(desired) != BasicSettingsValues.From(current.Settings)) catalog.UpdateSettings(serverId, desired);
        }, ct);
    }
}
