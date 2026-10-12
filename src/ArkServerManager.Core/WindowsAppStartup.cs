using Microsoft.Win32;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
namespace ArkServerManager.Core;

public static class WindowsAppStartup
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static string ValueName(string application) => "ARKServerManager-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(application)).ToUpperInvariant())))[..16];
    public static string Command(string application)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(application));
        if (root.Any(char.IsControl) || root.Contains('"')) throw new ArgumentException("Unsupported startup path.");
        return "\"" + AppBootstrapLayout.ExecutableFor(root, "win-x64") + "\" --application \"" + root + "\"";
    }
    [SupportedOSPlatform("windows")]
    public static bool Read(string application)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        var current = key?.GetValue(ValueName(application));
        if (current is null) return false;
        if (current is not string command || command != Command(application)) throw new IOException("This installation's startup entry changed. Review it before replacing it.");
        return true;
    }
    [SupportedOSPlatform("windows")]
    public static void Save(string application, bool enabled)
    {
        if (InstalledAppSettingsFile.Read(application) is null || !File.Exists(AppBootstrapLayout.ExecutableFor(application, "win-x64")))
            throw new IOException("Install the app and recovery launcher before enabling startup.");
        _ = Read(application);
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        if (enabled) key.SetValue(ValueName(application), Command(application), RegistryValueKind.String);
        else key.DeleteValue(ValueName(application), throwOnMissingValue: false);
        if (Read(application) != enabled) throw new IOException("The startup setting could not be verified.");
    }
}
