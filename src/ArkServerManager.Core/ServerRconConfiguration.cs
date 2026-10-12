using System.Globalization;

namespace ArkServerManager.Core;

/// <summary>Resolves local administration credentials from the registered server's actual INI.</summary>
public static class ServerRconConfiguration
{
    public static async Task<RconEndpoint> ReadAsync(CatalogRepository repository, Guid serverId,
        TimeSpan timeout, RconResponseMode responseMode, CancellationToken ct = default)
    {
        var server = (await repository.ReadAsync(ct)).Servers.SingleOrDefault(s => s.Id == serverId)
            ?? throw new ArgumentException("Choose a registered server.");
        var file = await ServerConfigurationReader.ReadAsync(repository, new(serverId, "GameUserSettings.ini"), ct);
        var latest = (await repository.ReadAsync(ct)).Servers.SingleOrDefault(s => s.Id == serverId);
        if (latest is null || latest.Directory != server.Directory || latest.Edition != server.Edition
            || latest.Settings.RconPort != server.Settings.RconPort)
            throw new InvalidOperationException("The server installation or administration port changed. Reload it before connecting.");
        return FromConfiguration(server, file.Text ?? throw new InvalidDataException("Server administration settings are missing."),
            timeout, responseMode);
    }

    public static RconEndpoint FromConfiguration(ServerDefinition server, string text,
        TimeSpan timeout, RconResponseMode responseMode)
    {
        server.Settings.Validate();
        if (timeout <= TimeSpan.Zero || !Enum.IsDefined(responseMode)) throw new ArgumentException("Choose valid administration connection options.");
        var document = new IniDocument(text);
        string Unique(string key)
        {
            var values = document.GetValues("ServerSettings", key);
            if (values.Count != 1) throw new InvalidDataException("Administration settings must contain a single enabled connection, port and password.");
            return values[0];
        }
        if (!bool.TryParse(Unique("RCONEnabled"), out var enabled) || !enabled)
            throw new InvalidDataException("Enable server administration before connecting.");
        if (!int.TryParse(Unique("RCONPort"), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port != server.Settings.RconPort)
            throw new InvalidDataException("The server administration port does not match its registered port.");
        var password = Unique("ServerAdminPassword");
        if (string.IsNullOrWhiteSpace(password) || password.Contains('\0'))
            throw new InvalidDataException("Set a usable administration password before connecting.");
        return new(port, password, timeout, responseMode);
    }
}
