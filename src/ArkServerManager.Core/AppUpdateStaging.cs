using System.IO.Compression;

namespace ArkServerManager.Core;

public sealed record StagedAppUpdate(string Directory, InstallerPayload Manifest);

public static class AppUpdateStaging
{
    public static async Task<StagedAppUpdate> ExtractAsync(string package, string stagingParent, string runtime, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(package) || !Path.IsPathFullyQualified(stagingParent))
            throw new ArgumentException("Absolute update staging locations are required.");
        RejectLinks(package); RejectLinks(stagingParent);
        if (!Directory.Exists(stagingParent)) throw new DirectoryNotFoundException("Update staging folder is unavailable.");
        var directory = Path.Combine(stagingParent, ".ark-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var archive = ZipFile.OpenRead(package);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            if (archive.Entries.Count > 10000) throw new InvalidDataException("Update package contains too many entries.");
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var name = entry.FullName;
                var fileName = name.TrimEnd('/');
                if (string.IsNullOrEmpty(fileName) || name.Contains('\\') || name.Contains(':') || name.StartsWith('/')
                    || fileName.Split('/').Any(p => p is "" or "." or "..") || !names.Add(fileName)
                    || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new InvalidDataException("Unsupported update package entry.");
                var first = fileName.Split('/')[0];
                if (new[] { "Servers", "Clusters", "GameFiles", "Dependencies", "State", "app-settings", "logs", "catalog.json", "installation.json", AppFileLease.FileName, AppBootstrapLayout.FolderName }.Contains(first, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException("An app update cannot contain managed data or installation settings.");
                var path = Path.Combine(directory, fileName);
                if (name.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
                total = checked(total + entry.Length);
                if (total > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Expanded update package is too large.");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using var input = entry.Open();
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long written = 0; int read;
                while ((read = await input.ReadAsync(buffer, ct)) != 0)
                {
                    written += read;
                    if (written > entry.Length) throw new InvalidDataException("Update entry exceeds its declared size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                if (output.Length != entry.Length) throw new InvalidDataException("Update entry size does not match.");
            }
            var manifest = await VerifiedPayloadInstaller.VerifyAsync(directory, runtime, ct);
            if (OperatingSystem.IsLinux() && runtime == "linux-x64")
                foreach (var relative in new[] { "ArkServerManager.Desktop", "manager/ArkServerManager.Host", "updater/ArkServerManager.Updater" })
                {
                    var path = Path.Combine(directory, relative);
                    if (File.Exists(path)) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            return new(directory, manifest);
        }
        catch
        {
            RejectLinks(directory);
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked update staging locations are unsupported.");
    }
}
