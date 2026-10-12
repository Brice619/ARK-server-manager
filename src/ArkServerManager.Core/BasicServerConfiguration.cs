using System.Globalization;

namespace ArkServerManager.Core;

/// <summary>Updates only the basic fields represented by the server catalog.</summary>
public static class BasicServerConfiguration
{
    public static string Apply(string text, ServerSettings settings)
    {
        settings.Validate();
        if (settings.Name.Length > 256 || settings.Name.Any(char.IsControl))
            throw new ArgumentException("The server name must fit on one line of up to 256 characters.");
        var ini = new IniDocument(text);
        ini.SetValues("SessionSettings", "SessionName", [settings.Name]);
        ini.SetValues("SessionSettings", "Port", [settings.GamePort.ToString(CultureInfo.InvariantCulture)]);
        ini.SetValues("SessionSettings", "QueryPort", [settings.QueryPort.ToString(CultureInfo.InvariantCulture)]);
        ini.SetValues("/Script/Engine.GameSession", "MaxPlayers", [settings.MaxPlayers.ToString(CultureInfo.InvariantCulture)]);
        ini.SetValues("ServerSettings", "RCONPort", [settings.RconPort.ToString(CultureInfo.InvariantCulture)]);
        return ini.ToString();
    }
}
