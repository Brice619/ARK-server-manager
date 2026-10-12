using System.Security.Cryptography;
using System.Text.Json;

namespace ArkServerManager.Core;

public sealed record PayloadFile(string Path, long Bytes, string SHA256);
public sealed record InstallerPayload(int SchemaVersion, string Runtime, bool SelfContained, List<PayloadFile> Files);
public sealed record PayloadInstallationResult(string Directory, string Runtime, int Files);

/// <summary>Initial deployment only. Checks consistency; publisher authentication belongs to package delivery.</summary>
public static class VerifiedPayloadInstaller
{
    private const string ManifestName = "payload-manifest.json";
    private static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static async Task<InstallerPayload> VerifyAsync(string payloadDirectory, string runtime, CancellationToken ct = default)
    {
        if (runtime is not ("win-x64" or "linux-x64") || !Path.IsPathFullyQualified(payloadDirectory))
            throw new ArgumentException("Choose an absolute payload folder and supported platform.");
        var source = Path.GetFullPath(payloadDirectory);
        var (manifest, _) = await ReadManifestAsync(source, runtime, ct);
        await VerifyFilesAsync(source, manifest, ct);
        return manifest;
    }

    public static async Task<InstallerPayload> VerifyInstalledAsync(string applicationDirectory, string runtime, CancellationToken ct = default)
    {
        if (runtime is not ("win-x64" or "linux-x64") || !Path.IsPathFullyQualified(applicationDirectory))
            throw new ArgumentException("Choose an absolute installed application folder and supported platform.");
        if (InstalledAppSettingsFile.Read(applicationDirectory) is null)
            throw new InvalidDataException("The installed application settings are missing.");
        var (manifest, _) = await ReadManifestAsync(applicationDirectory, runtime, ct);
        await VerifyFilesAsync(applicationDirectory, manifest, ct, allowUnlisted: true);
        return manifest;
    }

    internal static async Task<InstallerPayload> VerifyRecoveryAsync(string directory, string runtime, CancellationToken ct)
    {
        var (manifest, _) = await ReadManifestAsync(directory, runtime, ct);
        await VerifyFilesAsync(directory, manifest, ct, allowUnlisted: true);
        return manifest;
    }

    public static async Task<PayloadInstallationResult> InstallAsync(string payloadDirectory, string destination,
        string runtime, CancellationToken ct = default)
    {
        if (runtime is not ("win-x64" or "linux-x64")) throw new ArgumentException("Unsupported installer platform.");
        if (!Path.IsPathFullyQualified(payloadDirectory) || !Path.IsPathFullyQualified(destination))
            throw new ArgumentException("Choose absolute installation folders.");
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(payloadDirectory));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        var parent = Path.GetDirectoryName(target) ?? throw new ArgumentException("Choose a dedicated folder.");
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("Choose an existing parent folder.");
        RejectExisting(target); RejectLinkedAncestors(parent);
        var (manifest, manifestBytes) = await ReadManifestAsync(source, runtime, ct);
        await VerifyFilesAsync(source, manifest, ct);
        var staging = Path.Combine(parent, ".ark-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var file in manifest.Files)
            {
                ct.ThrowIfCancellationRequested();
                var inputPath = Path.Combine(source, file.Path);
                RejectLinkedAncestors(inputPath);
                var installedPath = AppBootstrapLayout.IsBootstrapPayloadFile(file.Path)
                    ? AppBootstrapLayout.FolderName + "/" + file.Path[(AppBootstrapLayout.PayloadFolderName.Length + 1)..] : file.Path;
                var outputPath = Path.Combine(staging, installedPath);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                await using (var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                await using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await input.CopyToAsync(output, ct);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(outputPath, File.GetUnixFileMode(inputPath));
                await using var copied = File.OpenRead(outputPath);
                if (copied.Length != file.Bytes || !Convert.ToHexString(await SHA256.HashDataAsync(copied, ct)).Equals(file.SHA256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Copied installer file integrity verification failed.");
            }
            var installedManifest = manifest with { Files = manifest.Files.Where(file => !AppBootstrapLayout.IsBootstrapPayloadFile(file.Path)).ToList() };
            var installedManifestBytes = installedManifest.Files.Count == manifest.Files.Count ? manifestBytes : JsonSerializer.SerializeToUtf8Bytes(installedManifest);
            await File.WriteAllBytesAsync(Path.Combine(staging, ManifestName), installedManifestBytes, ct);
            await VerifyFilesAsync(staging, installedManifest, ct, allowUnlisted: true);
            await File.WriteAllTextAsync(Path.Combine(staging, InstalledAppSettingsFile.FileName),
                JsonSerializer.Serialize(new InstalledAppSettings(1, target)), ct);
            ct.ThrowIfCancellationRequested();
            RejectExisting(target); RejectLinkedAncestors(parent);
            Directory.Move(staging, target);
            return new(target, runtime, manifest.Files.Count);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                RejectLinkedAncestors(staging);
                if (!Paths.Equals(Path.GetDirectoryName(Path.GetFullPath(staging)), parent))
                    throw new IOException("Installation cleanup location changed.");
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private static async Task<(InstallerPayload Manifest, byte[] Bytes)> ReadManifestAsync(string source, string runtime, CancellationToken ct)
    {
        var path = Path.Combine(source, ManifestName);
        RejectLinkedAncestors(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 4 * 1024 * 1024) throw new InvalidDataException("Installer manifest is too large.");
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(bytes, ct)) > 0)
        {
            if (buffer.Length + read > 4 * 1024 * 1024) throw new InvalidDataException("Installer manifest is too large.");
            buffer.Write(bytes, 0, read);
        }
        var contents = buffer.ToArray();
        var json = contents.AsMemory();
        if (contents.Length >= 3 && contents[0] == 239 && contents[1] == 187 && contents[2] == 191) json = json[3..];
        var manifest = JsonSerializer.Deserialize<InstallerPayload>(json.Span)
            ?? throw new InvalidDataException("Installer manifest is empty.");
        ValidateManifest(manifest, runtime);
        return (manifest, contents);
    }

    internal static void ValidateManifest(InstallerPayload manifest, string runtime)
    {
        if (runtime is not ("win-x64" or "linux-x64")) throw new InvalidDataException("Unsupported payload platform.");
        if (manifest.SchemaVersion != 1 || manifest.Runtime != runtime || !manifest.SelfContained || manifest.Files is null or { Count: 0 })
            throw new InvalidDataException("Unsupported installer manifest.");
        var names = new HashSet<string>(Paths);
        foreach (var file in manifest.Files)
            if (file is null || string.IsNullOrWhiteSpace(file.Path) || Path.IsPathRooted(file.Path)
                || file.Path.Contains('\\') || file.Path.Contains(':') || file.Path.Split('/').Any(p => p is "" or "." or "..")
                || !names.Add(file.Path) || Paths.Equals(file.Path, ManifestName) || Paths.Equals(file.Path, InstalledAppSettingsFile.FileName)
                || file.Bytes < 0 || file.SHA256 is null || file.SHA256.Length != 64 || file.SHA256.Any(c => !char.IsAsciiHexDigit(c)))
                throw new InvalidDataException("Invalid installer file inventory.");
        var suffix = runtime == "win-x64" ? ".exe" : "";
        foreach (var required in new[] { "ArkServerManager.Desktop" + suffix, "ArkServerManager.Desktop.runtimeconfig.json",
            "System.Private.CoreLib.dll", "manager/ArkServerManager.Host" + suffix, "manager/ArkServerManager.Host.dll",
            "manager/ArkServerManager.Host.runtimeconfig.json", "manager/System.Private.CoreLib.dll" })
            if (!names.Contains(required)) throw new InvalidDataException("Required installer component is missing.");
    }

    private static async Task VerifyFilesAsync(string directory, InstallerPayload manifest, CancellationToken ct, bool allowUnlisted = false)
    {
        var expected = new HashSet<string>(manifest.Files.Select(f => f.Path), Paths) { ManifestName };
        if (!allowUnlisted)
            foreach (var item in EnumerateRegularFiles(directory))
                if (!expected.Contains(Path.GetRelativePath(directory, item).Replace('\\', '/')))
                    throw new InvalidDataException("An unlisted file is present in the installer payload.");
        foreach (var file in manifest.Files)
        {
            var path = Path.Combine(directory, file.Path);
            RejectLinkedAncestors(path);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length != file.Bytes || !Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).Equals(file.SHA256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Installer file integrity verification failed.");
        }
    }

    private static IEnumerable<string> EnumerateRegularFiles(string directory)
    {
        RejectLinkedAncestors(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            RejectLinkedAncestors(entry);
            if (Directory.Exists(entry)) { foreach (var file in EnumerateRegularFiles(entry)) yield return file; }
            else yield return entry;
        }
    }

    private static void RejectExisting(string path)
    { if (File.Exists(path) || Directory.Exists(path)) throw new IOException("The installation folder already exists."); }

    private static void RejectLinkedAncestors(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked installation locations require separate handling.");
    }
}
