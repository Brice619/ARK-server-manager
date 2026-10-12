using System.Globalization;
using System.Net;
using System.Text.Json;

namespace ArkServerManager.Core;

/// <summary>Optional public CFWidget metadata. Native ASA installation does not depend on this service.</summary>
public sealed class PublicAsaModMetadata(HttpClient http)
{
    public async Task<ModMetadata> GetAsync(string id, CancellationToken ct = default)
    {
        if (!int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
            throw new ArgumentException("Enter a positive numeric CurseForge project ID.");
        using var response = await http.GetAsync("https://api.cfwidget.com/" + number.ToString(CultureInfo.InvariantCulture),
            HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.Accepted)
            throw new ModLookupException("Project details are being fetched. Try again shortly, or add by ID.");
        if (!response.IsSuccessStatusCode)
            throw new ModLookupException("Public project details are unavailable. You can still add by ID.");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream(); var chunk = new byte[16384];
        int count;
        while ((count = await input.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > 2 * 1024 * 1024) throw new InvalidDataException("Project details exceed the size limit.");
            buffer.Write(chunk, 0, count);
        }
        buffer.Position = 0;
        using var json = await JsonDocument.ParseAsync(buffer, new JsonDocumentOptions { MaxDepth = 32 }, ct);
        var data = json.RootElement;
        if (data.GetProperty("id").GetInt32() != number || data.GetProperty("game").GetString() != "ark-survival-ascended")
            throw new ModLookupException("This project does not match an ARK: Survival Ascended mod ID.");
        var link = SafeUri(data.GetProperty("urls").GetProperty("curseforge").GetString());
        if (link is null || link.Host != "www.curseforge.com" || !link.AbsolutePath.StartsWith("/ark-survival-ascended/mods/", StringComparison.Ordinal))
            throw new InvalidDataException("Project details link could not be verified.");
        var updated = data.TryGetProperty("download", out var download) && download.ValueKind == JsonValueKind.Object
            && download.TryGetProperty("uploaded_at", out var date) ? date.GetString() : data.GetProperty("created_at").GetString();
        if (!DateTimeOffset.TryParse(updated, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp))
            throw new InvalidDataException("Project date could not be read.");
        var tags = data.TryGetProperty("categories", out var categories)
            ? categories.EnumerateArray().Take(100).Select(item => item.GetString() ?? "").ToArray() : [];
        return new(number.ToString(CultureInfo.InvariantCulture), ArkEdition.ASA, data.GetProperty("title").GetString() ?? id,
            data.GetProperty("summary").GetString() ?? "", data.TryGetProperty("thumbnail", out var thumbnail) ? SafeUri(thumbnail.GetString()) : null,
            link, timestamp, tags);
    }

    private static Uri? SafeUri(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 ? uri : null;
}
