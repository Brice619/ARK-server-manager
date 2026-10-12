using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace ArkServerManager.Core;

public sealed record SnapshotFile(string Path, long Length, string Sha256);
public sealed record SnapshotCapture(Guid Id, DateTimeOffset CapturedAt);
public enum SnapshotScope { Server, SharedCluster }
public enum SnapshotLayout { Unspecified, WorldSavedFolders }
public sealed record SnapshotManifest(Guid ServerId, ArkEdition Edition, long RuntimeHour,
    DateTimeOffset CreatedAt, List<SnapshotFile> Files)
{
    // ServerId is the historical archive owner field; shared archives use the cluster ID.
    // Missing scope in older archives remains Server for backward compatibility.
    public SnapshotScope Scope { get; init; } = SnapshotScope.Server;
    public SnapshotLayout Layout { get; init; }
    public SnapshotCapture? Capture { get; init; }
}

/// <summary>Archives an explicitly selected state directory. The caller must first coordinate a world save.</summary>
public sealed class SnapshotArchive
{
    public async Task<SnapshotManifest> CreateAsync(string sourceDirectory, string destination,
        Guid serverId, ArkEdition edition, long runtimeHour, CancellationToken cancellationToken = default,
        SnapshotScope scope = SnapshotScope.Server, SnapshotLayout layout = SnapshotLayout.Unspecified,
        SnapshotCapture? capture = null)
    {
        if (runtimeHour < 1) throw new ArgumentOutOfRangeException(nameof(runtimeHour));
        if (serverId == Guid.Empty || !Enum.IsDefined(edition) || !Enum.IsDefined(scope) || !Enum.IsDefined(layout)
            || layout == SnapshotLayout.WorldSavedFolders && scope != SnapshotScope.Server)
            throw new ArgumentException("Invalid backup owner, edition or scope.");
        var source = Path.GetFullPath(sourceDirectory);
        var target = Path.GetFullPath(destination);
        if (IsWithin(source, target)) throw new ArgumentException("The archive must be outside the state directory.");
        RejectLinks(source);
        RejectLinks(target);
        var files = layout == SnapshotLayout.WorldSavedFolders
            ? new[] { "SavedArks", "SaveGames" }.Select(folder => Path.Combine(source, folder))
                .Where(Directory.Exists).SelectMany(EnumerateFiles).ToArray()
            : EnumerateFiles(source).ToArray();
        if (layout == SnapshotLayout.WorldSavedFolders && files.Length == 0)
            throw new InvalidDataException("No world/player save files were found in the supported save folders.");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var manifest = new SnapshotManifest(serverId, edition, runtimeHour, DateTimeOffset.UtcNow, []) { Scope = scope, Layout = layout, Capture = capture };
        ValidateManifest(manifest);
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var file in files)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
                        var entry = archive.CreateEntry("state/" + relative, CompressionLevel.Fastest);
                        // Deny writes while copying each file. Multi-file consistency requires caller coordination.
                        await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                        await using var content = entry.Open();
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        var buffer = new byte[81920];
                        long length = 0;
                        int read;
                        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                        {
                            hash.AppendData(buffer, 0, read);
                            await content.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                            length += read;
                        }
                        manifest.Files.Add(new(relative, length, Convert.ToHexString(hash.GetHashAndReset())));
                    }
                    var metadata = archive.CreateEntry("manifest.json");
                    await using var metadataStream = metadata.Open();
                    await JsonSerializer.SerializeAsync(metadataStream, manifest, cancellationToken: cancellationToken);
                }
                await output.FlushAsync(cancellationToken);
                output.Flush(true);
            }
            await VerifyAsync(temporary, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: false);
            return manifest;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<SnapshotManifest> VerifyAsync(string path, CancellationToken cancellationToken = default)
    {
        RejectLinks(Path.GetFullPath(path));
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("Missing backup manifest.");
        await using var metadata = entry.Open();
        var manifest = await JsonSerializer.DeserializeAsync<SnapshotManifest>(metadata, cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Invalid backup manifest.");
        ValidateManifest(manifest);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (!SafeRelativePath(file.Path) || !names.Add(file.Path)) throw new InvalidDataException("Invalid or repeated backup path.");
            var contentEntry = archive.GetEntry("state/" + file.Path) ?? throw new InvalidDataException("Missing backup file.");
            if (contentEntry.Length != file.Length) throw new InvalidDataException("Backup file length mismatch.");
            await using var content = contentEntry.Open();
            var hash = await SHA256.HashDataAsync(content, cancellationToken);
            if (!Convert.ToHexString(hash).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Backup checksum mismatch.");
        }
        if (archive.Entries.Count != manifest.Files.Count + 1) throw new InvalidDataException("Unexpected backup entries.");
        return manifest;
    }

    public async Task<SnapshotManifest> ExtractToNewDirectoryAsync(string path, string destination,
        Guid expectedServerId, ArkEdition expectedEdition, CancellationToken cancellationToken = default,
        SnapshotScope expectedScope = SnapshotScope.Server, long? expectedRuntimeHour = null)
    {
        if (expectedRuntimeHour is <= 0) throw new ArgumentOutOfRangeException(nameof(expectedRuntimeHour));
        RejectLinks(Path.GetFullPath(path));
        // Open once and keep the handle locked against replacement throughout verification and extraction.
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        var metadataEntry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("Missing backup manifest.");
        SnapshotManifest manifest;
        await using (var metadata = metadataEntry.Open())
            manifest = await JsonSerializer.DeserializeAsync<SnapshotManifest>(metadata, cancellationToken: cancellationToken)
                ?? throw new InvalidDataException("Invalid backup manifest.");
        ValidateManifest(manifest);
        if (!Enum.IsDefined(expectedScope) || manifest.ServerId != expectedServerId
            || manifest.Edition != expectedEdition || manifest.Scope != expectedScope)
            throw new InvalidDataException("This backup belongs to another server, cluster, scope or ARK edition.");
        if (expectedRuntimeHour is long hour && manifest.RuntimeHour != hour)
            throw new InvalidDataException("This archive is not the selected restore point.");
        var target = Path.GetFullPath(destination);
        RejectLinks(target);
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Restore staging folder already exists.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (archive.Entries.Count != manifest.Files.Count + 1) throw new InvalidDataException("Unexpected backup entries.");
        foreach (var file in manifest.Files)
            if (!SafeRelativePath(file.Path) || !names.Add(file.Path)) throw new InvalidDataException("Invalid or repeated backup path.");
        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        RejectLinks(parent);
        var staging = Path.Combine(parent, ".restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var file in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = archive.GetEntry("state/" + file.Path) ?? throw new InvalidDataException("Missing backup file.");
                if (entry.Length != file.Length) throw new InvalidDataException("Backup file length mismatch.");
                var outputPath = Path.GetFullPath(Path.Combine(staging, file.Path));
                if (!IsWithin(staging, outputPath)) throw new InvalidDataException("Backup path escapes the staging folder.");
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                await using (var content = entry.Open())
                await using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await content.CopyToAsync(output, cancellationToken);
                await using var extracted = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var checksum = await SHA256.HashDataAsync(extracted, cancellationToken);
                if (!Convert.ToHexString(checksum).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Backup checksum mismatch.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            RejectLinks(target);
            Directory.Move(staging, target);
            return manifest;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    private static void ValidateManifest(SnapshotManifest manifest)
    {
        if (manifest.Capture is { } capture && (capture.Id == Guid.Empty || capture.CapturedAt == default))
            throw new InvalidDataException("Backup capture identity is invalid.");
        if (manifest.ServerId == Guid.Empty || !Enum.IsDefined(manifest.Edition) || !Enum.IsDefined(manifest.Scope)
            || !Enum.IsDefined(manifest.Layout) || manifest.Layout == SnapshotLayout.WorldSavedFolders && manifest.Scope != SnapshotScope.Server || manifest.RuntimeHour < 1
            || manifest.CreatedAt == default || manifest.Files is null)
            throw new InvalidDataException("Backup identity or file inventory is invalid.");
        foreach (var file in manifest.Files)
            if (file is null || !SafeRelativePath(file.Path) || file.Length < 0 || file.Sha256 is null
                || file.Sha256.Length != 64 || file.Sha256.Any(c => !char.IsAsciiHexDigit(c)))
                throw new InvalidDataException("Backup file metadata is invalid.");
        if (manifest.Layout == SnapshotLayout.WorldSavedFolders && manifest.Files.Any(file =>
            !(file.Path.StartsWith("SavedArks/", StringComparison.Ordinal) || file.Path.StartsWith("SaveGames/", StringComparison.Ordinal))))
            throw new InvalidDataException("World backups must not contain configuration or unrelated folders.");
    }

    private static bool SafeRelativePath(string path) => !string.IsNullOrWhiteSpace(path)
        && !Path.IsPathRooted(path) && !path.Contains('\\') && !path.Contains(':')
        && path.Split('/').All(p => p.Length > 0 && p is not "." and not "..");

    private static IEnumerable<string> EnumerateFiles(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            RejectLinks(entry);
            if (Directory.Exists(entry))
                foreach (var file in EnumerateFiles(entry)) yield return file;
            else yield return entry;
        }
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked state folders or files require explicit handling.");
    }

    private static bool IsWithin(string directory, string path)
    {
        var relative = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar);
    }
}
