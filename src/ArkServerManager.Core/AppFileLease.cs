namespace ArkServerManager.Core;

/// <summary>Installed app and manager hold shared leases; an independent updater requires exclusive ownership.</summary>
public static class AppFileLease
{
    public const string FileName = ".app-use.lock";

    public static FileStream? OpenUsageIfInstalled(string applicationDirectory)
    {
        var settings = Path.Combine(Path.GetFullPath(applicationDirectory), InstalledAppSettingsFile.FileName);
        if (!File.Exists(settings)) return null;
        AppUpdatePreparation.RejectLinks(settings);
        return Open(applicationDirectory, FileShare.ReadWrite);
    }

    public static FileStream OpenForUpdate(string applicationDirectory)
    {
        if (InstalledAppSettingsFile.Read(applicationDirectory) is null)
            throw new InvalidDataException("The app must be installed before it can update.");
        return Open(applicationDirectory, FileShare.None);
    }

    private static FileStream Open(string applicationDirectory, FileShare sharing)
    {
        var path = Path.Combine(Path.GetFullPath(applicationDirectory), FileName);
        AppUpdatePreparation.RejectLinks(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, sharing);
    }
}
