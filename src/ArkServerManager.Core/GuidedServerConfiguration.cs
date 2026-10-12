using System.Globalization;

namespace ArkServerManager.Core;

public sealed class GuidedServerConfigurationRequest
{
    public ServerDefinition? Server { get; init; }
    public bool? PvE { get; init; }
    public string AdministratorPassword { get; init; } = "";
    public string JoinPassword { get; init; } = "";
    public string WelcomeMessage { get; init; } = "";
    public int? WelcomeDurationSeconds { get; init; }
    public string? UserSettingsTemplate { get; init; }
    public string? GameSettingsTemplate { get; init; }
    public override string ToString() => $"Guided configuration for {Server?.Id}; passwords redacted";
}

public static class GuidedServerConfiguration
{
    public static void ValidateIdentity(ServerDefinition server, string userSettings)
    {
        server.Settings.Validate();
        var ini = new IniDocument(userSettings);
        Require("SessionSettings", "SessionName", server.Settings.Name);
        Require("SessionSettings", "Port", server.Settings.GamePort.ToString(CultureInfo.InvariantCulture));
        Require("SessionSettings", "QueryPort", server.Settings.QueryPort.ToString(CultureInfo.InvariantCulture));
        Require("/Script/Engine.GameSession", "MaxPlayers", server.Settings.MaxPlayers.ToString(CultureInfo.InvariantCulture));
        void Require(string section, string key, string expected)
        {
            var values = ini.GetValues(section, key);
            if (values.Count != 1 || values[0] != expected)
                throw new InvalidDataException("Prepared server name, player limit or ports do not match the reviewed setup.");
        }
    }

    public static InitialServerConfigurationRequest Build(GuidedServerConfigurationRequest request)
    {
        var server = request.Server ?? throw new ArgumentException("Choose a new server setup.");
        if (server.Id == Guid.Empty || request.PvE is null || !Path.IsPathFullyQualified(server.Directory))
            throw new ArgumentException("Choose the server identity, location and PvE or PvP mode.");
        server.Settings.Validate();
        ServerMapIdentifier.ForLaunch(server.Edition, server.Map);
        if (server.Settings.Name.Length > 256 || server.Settings.Name.Any(char.IsControl))
            throw new ArgumentException("The server name must fit on one line.");
        if (server.Mods.Any(id => !ulong.TryParse(id, System.Globalization.NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number == 0)
            || server.Mods.Select(id => ulong.Parse(id, CultureInfo.InvariantCulture)).Distinct().Count() != server.Mods.Count)
            throw new ArgumentException("Choose distinct numeric mod IDs.");
        foreach (var template in new[] { request.UserSettingsTemplate, request.GameSettingsTemplate })
            if (template is not null && (template.Contains('\0') || ConfigurationSyntax.Check(template).Count > 0))
                throw new ArgumentException("The preset configuration has invalid syntax.");
        var templates = PresetConfigurationEntries.Apply(request.UserSettingsTemplate ?? "\n", request.GameSettingsTemplate ?? "", server.Settings.Values);
        var ini = new IniDocument(templates.UserSettings);
        // A missing engine settings version makes native ASE regenerate this entire file on first launch.
        const string aseUserSettings = "/Script/ShooterGame.ShooterGameUserSettings";
        if (server.Edition == ArkEdition.ASE && ini.GetValues(aseUserSettings, "Version").Count == 0)
            ini.SetValues(aseUserSettings, "Version", ["5"]);
        var gameIni = new IniDocument(templates.GameSettings);
        // The selected mod list is authoritative, including an empty list replacing template mods.
        ini.SetValues("ServerSettings", "ActiveMods", server.Edition == ArkEdition.ASE && server.Mods.Count > 0 ? [string.Join(",", server.Mods)] : []);
        gameIni.SetValues("ModInstaller", "ModIDS", server.Edition == ArkEdition.ASE ? server.Mods : []);
        if (request.WelcomeMessage is null || request.WelcomeMessage.Length > 4096 || request.WelcomeMessage.Any(char.IsControl))
            throw new ArgumentException("The welcome message must fit on one line.");
        if (request.WelcomeMessage.Length > 0)
            ini.SetValues("MessageOfTheDay", "Message", [request.WelcomeMessage]);
        else ini.SetValues("MessageOfTheDay", "Message", []);
        if (request.WelcomeDurationSeconds is <= 0)
            throw new ArgumentException("Choose a positive welcome-message duration in seconds.");
        if (request.WelcomeDurationSeconds is int duration)
            ini.SetValues("MessageOfTheDay", "Duration", [duration.ToString(CultureInfo.InvariantCulture)]);
        else ini.SetValues("MessageOfTheDay", "Duration", []);
        ini.SetValues("SessionSettings", "SessionName", [server.Settings.Name]);
        ini.SetValues("SessionSettings", "Port", [server.Settings.GamePort.ToString(CultureInfo.InvariantCulture)]);
        ini.SetValues("SessionSettings", "QueryPort", [server.Settings.QueryPort.ToString(CultureInfo.InvariantCulture)]);
        ini.SetValues("/Script/Engine.GameSession", "MaxPlayers", [server.Settings.MaxPlayers.ToString(CultureInfo.InvariantCulture)]);
        ini.SetValues("ServerSettings", "ServerPVE", [request.PvE.Value ? "True" : "False"]);
        return new InitialServerConfigurationRequest
        {
            ServerId = server.Id,
            UserSettings = ServerAccessConfiguration.Apply(ini.ToString(), server.Settings.RconPort,
                request.AdministratorPassword, request.JoinPassword),
            GameSettings = server.Mods.Count > 0 || new IniDocument(templates.GameSettings).GetValues("ModInstaller", "ModIDS").Count > 0
                ? gameIni.ToString() : templates.GameSettings
        };
    }
}
