using System.Security.Cryptography;

namespace ArkServerManager.Core;

/// <summary>Downloads into a new staging file; never modifies the installed app or server data.</summary>
public static class AppReleaseDownload
{
    public static async Task DownloadAsync(HttpClient http, AppRelease release, string destination, CancellationToken ct = default)
    {
        var asset = release.Asset;
        var expected = $"https://github.com/{AppReleaseClient.Repository}/releases/download/{release.Tag}/{asset.Name}";
        if (asset.Download.AbsoluteUri != expected || asset.Bytes <= 0 || asset.Bytes > 1024L * 1024 * 1024
            || asset.SHA256.Length != 64 || asset.SHA256.Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidDataException("Invalid update download metadata.");
        if (!Path.IsPathFullyQualified(destination)) throw new ArgumentException("An absolute staging path is required.");
        RejectLinks(destination);
        using var response = await http.GetAsync(asset.Download, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var final = response.RequestMessage?.RequestUri;
        if (final is not null && (final.Scheme != "https" || final.UserInfo.Length != 0
            || final.Host is not ("github.com" or "release-assets.githubusercontent.com")))
            throw new InvalidDataException("Unexpected update download destination.");
        if (response.Content.Headers.ContentLength is long size && size != asset.Bytes)
            throw new InvalidDataException("Update download size does not match its release.");
        var owned = false;
        try
        {
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                owned = true;
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, ct)) != 0)
                {
                    total += read;
                    if (total > asset.Bytes) throw new InvalidDataException("Update download exceeds its declared size.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                if (total != asset.Bytes || !Convert.ToHexString(hash.GetHashAndReset()).Equals(asset.SHA256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Update download checksum verification failed.");
                await output.FlushAsync(ct);
                output.Flush(flushToDisk: true);
            }
        }
        catch
        {
            if (owned) { RejectLinks(destination); File.Delete(destination); }
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
