using System.Globalization;
using System.Net;
using System.Text.Json;

namespace ArkServerManager.Core;

public sealed class CurseForgeMetadata : IDisposable
{
    private readonly HttpClient http;
    private readonly bool ownsClient;
    public const int AsaGameId = 83374;
    public CurseForgeMetadata()
    {
        // A custom API-key header must never follow a redirect to another host.
        http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
        ownsClient = true;
    }
    internal CurseForgeMetadata(HttpClient client) => http = client;

    public async Task<ModMetadata> GetAsync(string id, string apiKey, CancellationToken ct = default)
    {
        if (!int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
            throw new ArgumentException("Enter a positive numeric CurseForge project ID.");
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 4096 || apiKey.Any(char.IsControl) || apiKey != apiKey.Trim())
            throw new ArgumentException("Connect an approved CurseForge API key.");
        using var game = await ReadAsync("games/" + AsaGameId, apiKey, ct);
        var gameData = game.RootElement.GetProperty("data");
        if (gameData.GetProperty("id").GetInt32() != AsaGameId || gameData.GetProperty("slug").GetString() != "ark-survival-ascended")
            throw new InvalidDataException("The ASA provider catalog could not be verified.");
        using var response = await ReadAsync("mods/" + number.ToString(CultureInfo.InvariantCulture), apiKey, ct);
        var data = response.RootElement.GetProperty("data");
        if (data.GetProperty("id").GetInt32() != number || data.GetProperty("gameId").GetInt32() != AsaGameId)
            throw new ModLookupException("This project does not belong to ARK: Survival Ascended.");
        var details = SafeUri(data.GetProperty("links").GetProperty("websiteUrl").GetString());
        if (details is null || details.Host != "www.curseforge.com" || !details.AbsolutePath.StartsWith("/ark-survival-ascended/mods/", StringComparison.Ordinal))
            throw new InvalidDataException("The project details link could not be verified.");
        if (!DateTimeOffset.TryParse(data.GetProperty("dateModified").GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var updated)) throw new InvalidDataException("Unexpected project update date.");
        var tags = data.TryGetProperty("categories", out var categories)
            ? categories.EnumerateArray().Take(100).Select(c => c.GetProperty("name").GetString() ?? "").ToArray() : [];
        Uri? picture = null;
        if (data.TryGetProperty("logo", out var logo) && logo.ValueKind == JsonValueKind.Object
            && logo.TryGetProperty("thumbnailUrl", out var thumbnail)) picture = SafeUri(thumbnail.GetString());
        return new(number.ToString(CultureInfo.InvariantCulture), ArkEdition.ASA,
            data.GetProperty("name").GetString() ?? id, data.GetProperty("summary").GetString() ?? "", picture, details, updated, tags);
    }

    private async Task<JsonDocument> ReadAsync(string resource, string apiKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.curseforge.com/v1/" + resource);
        request.Headers.Add("x-api-key", apiKey); request.Headers.Accept.ParseAdd("application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new ModLookupException("The provider key is not authorized for this catalog.");
        if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new ModLookupException("CurseForge is limiting requests. Try again later.");
        if (response.StatusCode == HttpStatusCode.NotFound) throw new ModLookupException("This project is unavailable or has been removed.");
        if (!response.IsSuccessStatusCode) throw new ModLookupException("The provider request could not be completed.");
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        using var bytes = new MemoryStream(); var chunk = new byte[16384];
        int count;
        while ((count = await source.ReadAsync(chunk, ct)) > 0)
        {
            if (bytes.Length + count > 2 * 1024 * 1024) throw new InvalidDataException("Provider metadata exceeds the size limit.");
            bytes.Write(chunk, 0, count);
        }
        bytes.Position = 0;
        return await JsonDocument.ParseAsync(bytes, new JsonDocumentOptions { MaxDepth = 32 }, ct);
    }

    private static Uri? SafeUri(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 ? uri : null;
    public void Dispose() { if (ownsClient) http.Dispose(); }
}
