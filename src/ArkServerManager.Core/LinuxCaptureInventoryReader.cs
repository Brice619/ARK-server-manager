using System.Runtime.Versioning;

namespace ArkServerManager.Core;

[SupportedOSPlatform("linux")]
public static class LinuxCaptureInventoryReader
{
    public static IReadOnlyList<StableCaptureEntry> Read(string root, IReadOnlyList<string>? selectedDirectories = null, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var entries = new List<StableCaptureEntry>();
        var rootIdentity = LinuxCaptureMetadata.Read(root);
        if (!rootIdentity.IsDirectory) throw new IOException("Saved-state root is not a directory.");
        Add("", rootIdentity);
        if (selectedDirectories is null) Visit(root, "", rootIdentity, 0);
        else
        {
            foreach (var name in selectedDirectories.Distinct(StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(['/', '\\', ':', '\0']) >= 0)
                    throw new ArgumentException("Choose direct saved-state directories.", nameof(selectedDirectories));
                var path = Path.Combine(root, name);
                if (!Path.Exists(path)) continue;
                var identity = LinuxCaptureMetadata.Read(path);
                if (!identity.IsDirectory) throw new IOException("A saved-state directory was replaced by a file.");
                Add(name, identity); Visit(path, name, identity, 0);
            }
        }
        if (LinuxCaptureMetadata.Read(root) != rootIdentity) throw new IOException("Saved-state directories changed during inventory collection.");
        StableCaptureInventory.RequireUnchanged(entries, entries);
        return entries;

        void Add(string relative, LinuxCaptureFileIdentity identity)
        {
            if (entries.Count >= 250000) throw new IOException("Saved-state inventory exceeds the supported entry count.");
            entries.Add(new(relative, identity.IsDirectory, identity.Bytes, identity.Revision));
        }
        void Visit(string directory, string relative, LinuxCaptureFileIdentity expected, int depth)
        {
            ct.ThrowIfCancellationRequested();
            if (depth > 128) throw new IOException("Saved-state directory nesting exceeds the supported depth.");
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested();
                var childRelative = relative.Length == 0 ? Path.GetFileName(path) : relative + "/" + Path.GetFileName(path);
                var identity = LinuxCaptureMetadata.Read(path);
                Add(childRelative, identity);
                if (identity.IsDirectory) Visit(path, childRelative, identity, depth + 1);
            }
            if (LinuxCaptureMetadata.Read(directory) != expected)
                throw new IOException("Saved-state directory changed during inventory collection.");
        }
    }
}
