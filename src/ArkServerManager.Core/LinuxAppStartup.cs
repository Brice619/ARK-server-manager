using System.Security.Cryptography;
using System.Text;
namespace ArkServerManager.Core;

public sealed class LinuxAppStartup(string configurationHome, string entryFolder = "autostart")
{
    public static string Entry(string application)
    {
        if (!application.StartsWith('/') || application.Any(char.IsControl) || application.Contains('='))
            throw new ArgumentException("Choose a Linux installation path supported by desktop startup.");
        var root = application.TrimEnd('/');
        if (root.Length == 0 || root.Split('/').Any(part => part is "." or "..")) throw new ArgumentException("Choose a normalized installation path.");
        static string Quote(string value)
        {
            var escaped = value.Replace("\\", "\\\\\\\\", StringComparison.Ordinal).Replace("\"", "\\\\\"", StringComparison.Ordinal)
                .Replace("`", "\\\\`", StringComparison.Ordinal).Replace("$", "\\\\$", StringComparison.Ordinal).Replace("%", "%%", StringComparison.Ordinal);
            return "\"" + escaped + "\"";
        }
        return "[Desktop Entry]\nType=Application\nName=ARK Server Manager\nTerminal=false\nExec="
            + Quote(root + "/Bootstrap/ArkServerManager.Launcher") + " --application " + Quote(root) + "\n";
    }
    private string PathFor(string application)
    {
        if (!Path.IsPathFullyQualified(configurationHome)) throw new ArgumentException("Choose an absolute desktop configuration folder.");
        if (entryFolder is not ("autostart" or "applications")) throw new ArgumentException("Choose a supported desktop entry folder.");
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(application.TrimEnd('/'))))[..16];
        return Path.Combine(configurationHome, entryFolder, "ark-server-manager-" + id + ".desktop");
    }
    public bool Read(string application)
    {
        var expected = Entry(application); var path = PathFor(application); RejectLinks(path);
        if (!File.Exists(path)) return false;
        if (new FileInfo(path).Length > 32768 || File.ReadAllText(path) != expected)
            throw new IOException("This installation's startup entry changed. Review it before replacing it.");
        return true;
    }
    public void Save(string application, bool enabled)
    {
        var entry = Entry(application); var path = PathFor(application); _ = Read(application);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); RejectLinks(path);
        if (!enabled) { if (File.Exists(path)) File.Delete(path); }
        else
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var output = new FileStream(temporary, options)) { var bytes = Encoding.UTF8.GetBytes(entry); output.Write(bytes); output.Flush(true); }
                RejectLinks(path); File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        if (Read(application) != enabled) throw new IOException("The startup setting could not be verified.");
    }
    private static void RejectLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked startup paths require review.");
    }
}
