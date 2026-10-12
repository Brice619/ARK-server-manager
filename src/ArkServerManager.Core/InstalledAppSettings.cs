using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArkServerManager.Core;

public sealed record InstalledAppSettings(int SchemaVersion, string ServerRoot);

/// <summary>Small installer-owned bootstrap document beside the desktop executable.</summary>
public static class InstalledAppSettingsFile
{
    public const string FileName = "installation.json";
    private const int MaximumBytes = 16 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static InstalledAppSettings? Read(string applicationDirectory)
    {
        var path = Path.Combine(Path.GetFullPath(applicationDirectory), FileName);
        if (!File.Exists(path)) return null;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The installation settings file must not be a link.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumBytes) throw new InvalidDataException("Installation settings are too large.");
        var bytes = new byte[MaximumBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = stream.Read(bytes, length, bytes.Length - length);
            if (read == 0) break;
            length += read;
        }
        if (length > MaximumBytes) throw new InvalidDataException("Installation settings are too large.");
        var settings = JsonSerializer.Deserialize<InstalledAppSettings>(bytes.AsSpan(0, length), Options)
            ?? throw new InvalidDataException("Installation settings are empty.");
        if (settings.SchemaVersion != 1 || string.IsNullOrWhiteSpace(settings.ServerRoot)
            || !Path.IsPathFullyQualified(settings.ServerRoot))
            throw new InvalidDataException("The installed server folder is invalid.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(settings.ServerRoot));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The installed server folder is unavailable.");
        return settings with { ServerRoot = root };
    }
}
