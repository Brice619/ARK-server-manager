using System.Globalization;

namespace ArkServerManager.Core;

/// <summary>Changes only the welcome message and duration while preserving unrelated INI content.</summary>
public static class ServerWelcomeConfiguration
{
    public static string Apply(string userSettings, string message, int? durationSeconds)
    {
        ArgumentNullException.ThrowIfNull(userSettings);
        if (message is null || message.Length > 4096 || message.Any(char.IsControl))
            throw new ArgumentException("The welcome message must fit on one line.");
        if (durationSeconds is <= 0)
            throw new ArgumentException("Choose a positive welcome-message duration in seconds.");
        var ini = new IniDocument(userSettings);
        ini.SetValues("MessageOfTheDay", "Message", [message]);
        ini.SetValues("MessageOfTheDay", "Duration", durationSeconds is int duration
            ? [duration.ToString(CultureInfo.InvariantCulture)] : []);
        return ini.ToString();
    }
}
