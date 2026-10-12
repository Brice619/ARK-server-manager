using System.Globalization;
using System.Net;
using System.Text.Json;

namespace ArkServerManager.Core;

public sealed record ModMetadata(string Id, ArkEdition Edition, string Name, string Description,
    Uri? PreviewImage, Uri DetailsPage, DateTimeOffset UpdatedAt, IReadOnlyList<string> Tags);

public sealed class ModLookupException(string message) : Exception(message);

public sealed class SteamWorkshopMetadata(HttpClient http)
{
    private static readonly Uri Endpoint = new("https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/");
    public const uint AseGameId = 346110;

    public async Task<ModMetadata> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var numericId) || numericId == 0)
            throw new ArgumentException("Enter a positive numeric Steam Workshop ID.");
        id = numericId.ToString(CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            { ["itemcount"] = "1", ["publishedfileids[0]"] = id })
        };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new ModLookupException("Steam Workshop is limiting requests. Try again later.");
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var limited = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (limited.Length + count > 2 * 1024 * 1024) throw new InvalidDataException("Workshop metadata exceeds the allowed size.");
            await limited.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        limited.Position = 0;
        using var json = await JsonDocument.ParseAsync(limited, cancellationToken: cancellationToken);
        var files = json.RootElement.GetProperty("response").GetProperty("publishedfiledetails");
        if (files.GetArrayLength() != 1) throw new InvalidDataException("Unexpected Workshop response.");
        var file = files[0];
        if (file.GetProperty("result").GetInt32() != 1)
            throw new ModLookupException("This Workshop item is unavailable, private, or removed.");
        if (file.GetProperty("publishedfileid").GetString() != id)
            throw new InvalidDataException("Workshop returned a different item ID.");
        if (file.GetProperty("consumer_app_id").GetUInt32() != AseGameId)
            throw new ModLookupException("This Workshop item does not belong to ARK Survival Evolved.");
        var tags = file.TryGetProperty("tags", out var tagList)
            ? tagList.EnumerateArray().Select(t => t.GetProperty("tag").GetString() ?? "").ToArray() : [];
        var image = file.TryGetProperty("preview_url", out var preview) ? HttpsUri(preview.GetString()) : null;
        return new(id, ArkEdition.ASE, file.GetProperty("title").GetString() ?? id,
            file.TryGetProperty("description", out var description) ? description.GetString() ?? "" : "",
            image, new Uri("https://steamcommunity.com/sharedfiles/filedetails/?id=" + id),
            DateTimeOffset.FromUnixTimeSeconds(file.GetProperty("time_updated").GetInt64()), tags);
    }

    private static Uri? HttpsUri(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) ? uri : null;
}
