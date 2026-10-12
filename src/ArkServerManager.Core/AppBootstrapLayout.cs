namespace ArkServerManager.Core;

public static class AppBootstrapLayout
{
    public const string FolderName = "Bootstrap";
    public const string PayloadFolderName = "bootstrap-payload";
    public static bool IsBootstrapPayloadFile(string relativePath) => relativePath.StartsWith(PayloadFolderName + "/", StringComparison.Ordinal);
    public static string DirectoryFor(string application) => Path.Combine(Path.GetFullPath(application), FolderName);
    public static string ExecutableFor(string application, string runtime) => Path.Combine(DirectoryFor(application), runtime switch
    {
        "win-x64" => "ArkServerManager.Launcher.exe",
        "linux-x64" => "ArkServerManager.Launcher",
        _ => throw new ArgumentException("Choose a supported launcher platform.")
    });
    public static bool IsProtectedLauncherDirectory(string application, string launcherDirectory)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(launcherDirectory)).Equals(Path.TrimEndingDirectorySeparator(DirectoryFor(application)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
