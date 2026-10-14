using System.Runtime.InteropServices;
using System.Runtime.Versioning;
namespace ArkServerManager.Core;

[SupportedOSPlatform("windows")]
public static class WindowsAppShortcuts
{
    public static void Create(string application, bool desktop)
    {
        var paths = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ARK Server Manager.lnk") };
        if (desktop) paths.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ARK Server Manager.lnk"));
        CreateAt(application, paths);
    }
    public static void CreateAt(string application, IReadOnlyList<string> paths)
    {
        if (paths.Count is < 1 or > 2 || paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Count)
            throw new ArgumentException("Choose distinct app shortcut paths.");
        var launcher = AppBootstrapLayout.ExecutableFor(application, "win-x64");
        _ = WindowsAppStartup.Command(application);
        if (InstalledAppSettingsFile.Read(application) is null || !File.Exists(launcher)) throw new IOException("The installed recovery launcher is missing.");
        var arguments = "--application \"" + Path.TrimEndingDirectorySeparator(Path.GetFullPath(application)) + "\"";
        object? shell = null; var shortcuts = new List<object>();
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("Windows shortcut support is unavailable."));
            foreach (var path in paths)
            {
                if (!Path.IsPathFullyQualified(path)) throw new IOException("The user shortcut folder is unavailable.");
                for (var current = path; current is not null; current = Path.GetDirectoryName(current))
                    if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked shortcut locations require review.");
                dynamic shortcut = ((dynamic)shell!).CreateShortcut(path);
                shortcuts.Add(shortcut);
                if (File.Exists(path) && (!string.Equals((string)shortcut.TargetPath, launcher, StringComparison.OrdinalIgnoreCase) || (string)shortcut.Arguments != arguments))
                    throw new IOException("An existing ARK Server Manager shortcut points elsewhere. It was preserved.");
            }
            foreach (dynamic shortcut in shortcuts)
            {
                shortcut.TargetPath = launcher; shortcut.Arguments = arguments; shortcut.WorkingDirectory = Path.GetFullPath(application);
                shortcut.IconLocation = launcher + ",0"; shortcut.Description = "Open ARK Server Manager"; shortcut.Save();
            }
            foreach (var path in paths)
            {
                dynamic saved = ((dynamic)shell!).CreateShortcut(path);
                try
                {
                    if (!File.Exists(path) || !string.Equals((string)saved.TargetPath, launcher, StringComparison.OrdinalIgnoreCase) || (string)saved.Arguments != arguments)
                        throw new IOException("The saved launcher shortcut could not be verified.");
                }
                finally { Marshal.FinalReleaseComObject((object)saved); }
            }
        }
        catch (COMException error) { throw new IOException("Windows could not create the app shortcut.", error); }
        finally
        {
            foreach (var shortcut in shortcuts) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
    }
}
