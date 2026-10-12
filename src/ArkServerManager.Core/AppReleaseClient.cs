using System.Net;
using System.Text.Json;

namespace ArkServerManager.Core;

public sealed record AppRelease(Version Version, string Tag, Uri Page, AppReleaseAsset Asset);
public sealed record AppReleaseAsset(string Name, Uri Download, long Bytes, string SHA256);

/// <summary>Read-only stable-release discovery from the designated public repository.</summary>
public sealed class AppReleaseClient(HttpClient http)
{
    public const string Repository = "Brice619/ARK-server-manager";
    private const int MaximumMetadataBytes = 2 * 1024 * 1024;

    public async Task<AppRelease?> FindUpdateAsync(Version installed, string runtime, CancellationToken ct = default)
    {
        if (runtime is not ("win-x64" or "linux-x64")) throw new ArgumentException("Unsupported update platform.");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("ARK-Server-Manager/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumMetadataBytes) throw new InvalidDataException("Release metadata is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) != 0)
        {
            if (bytes.Length + read > MaximumMetadataBytes) throw new InvalidDataException("Release metadata is too large.");
            bytes.Write(buffer, 0, read);
        }
        using var json = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        var root = json.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("The automatic update must be a published stable release.");
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var versionText = tag.StartsWith('v') ? tag[1..] : tag;
        if (!Version.TryParse(versionText, out var version) || version.Build < 0 || versionText != version.ToString())
            throw new InvalidDataException("The release version is invalid.");
        if (version <= installed) return null;
        var page = RequiredUri(root.GetProperty("html_url").GetString(), $"https://github.com/{Repository}/releases/tag/{tag}");
        var name = $"ARK-server-manager-{runtime}.zip";
        var matches = root.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == name).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("A unique update package for this platform is unavailable.");
        var asset = matches[0];
        if (asset.GetProperty("state").GetString() != "uploaded") throw new InvalidDataException("The update package is not ready.");
        var size = asset.GetProperty("size").GetInt64();
        if (size <= 0 || size > 1024L * 1024 * 1024) throw new InvalidDataException("The update package size is invalid.");
        var digest = asset.GetProperty("digest").GetString() ?? "";
        if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71 || digest[7..].Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidDataException("The update package checksum is unavailable.");
        var download = RequiredUri(asset.GetProperty("browser_download_url").GetString(),
            $"https://github.com/{Repository}/releases/download/{tag}/{name}");
        return new(version, tag, page, new(name, download, size, digest[7..]));
    }

    private static Uri RequiredUri(string? value, string expected)
    {
        if (value != expected || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
            throw new InvalidDataException("The release link does not belong to the designated repository.");
        return uri;
    }
}
