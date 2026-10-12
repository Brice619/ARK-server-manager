using System.Globalization;

namespace ArkServerManager.Core;

/// <summary>Applies explicit access choices to the manager's existing INI administration contract.</summary>
public static class ServerAccessConfiguration
{
    public static string Apply(string existingUserSettings, int rconPort, string administratorPassword, string joinPassword)
    {
        ArgumentNullException.ThrowIfNull(existingUserSettings);
        if (rconPort is < 1 or > 65535) throw new ArgumentException("Choose a valid administration port.");
        ValidatePasswords(administratorPassword, joinPassword);
        var document = new IniDocument(existingUserSettings);
        document.SetValues("ServerSettings", "RCONEnabled", ["True"]);
        document.SetValues("ServerSettings", "RCONPort", [rconPort.ToString(CultureInfo.InvariantCulture)]);
        document.SetValues("ServerSettings", "ServerAdminPassword", [administratorPassword]);
        document.SetValues("ServerSettings", "ServerPassword", [joinPassword]);
        return document.ToString();
    }

    public static void ValidatePasswords(string administratorPassword, string joinPassword)
    {
        ValidatePassword(administratorPassword, required: true);
        ValidatePassword(joinPassword, required: false);
    }

    private static void ValidatePassword(string value, bool required)
    {
        // Do not include rejected values in an exception, job status or diagnostic message.
        if (value is null || (required && string.IsNullOrWhiteSpace(value)) || value.Length > 1024
            || value.Any(char.IsControl) || value != value.Trim())
            throw new ArgumentException("Passwords must fit on one line without leading or trailing whitespace. An administrator password is required.");
    }
}
