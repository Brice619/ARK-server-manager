using System.Globalization;

namespace ArkServerManager.Core;

public sealed record ServerPlayerQueryRequest(Guid ServerId);
public sealed record ReportedServerPlayer(string Name, string AccountId);
public sealed record ServerPlayerQueryResult(Guid ServerId, DateTimeOffset MeasuredAt, IReadOnlyList<ReportedServerPlayer> Players)
{
    // Single-packet transport is not yet live-validated for complete multi-packet player lists.
    public bool CompleteListConfirmed { get; init; }
    public override string ToString() => $"ServerPlayerQueryResult {{ ServerId = {ServerId}, MeasuredAt = {MeasuredAt}, ReportedCount = {Players.Count} }}";
}

public static class ServerPlayerQuery
{
    public static async Task<ServerPlayerQueryResult> ReadAsync(CatalogRepository repository,
        ServerPlayerQueryRequest request, CancellationToken ct = default)
    {
        var endpoint = await ServerRconConfiguration.ReadAsync(repository, request.ServerId,
            TimeSpan.FromSeconds(5), RconResponseMode.SinglePacket, ct);
        var response = await new RconClient().ExecuteAsync(endpoint, "ListPlayers", ct);
        return Parse(request.ServerId, response, DateTimeOffset.UtcNow);
    }

    public static ServerPlayerQueryResult Parse(Guid serverId, string response, DateTimeOffset measuredAt)
    {
        if (serverId == Guid.Empty || measuredAt == default) throw new ArgumentException("Choose a server and observation time.");
        if (response.Length > RconProtocol.MaximumPacketSize || response.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t'))
            throw new InvalidDataException("The player response could not be read.");
        if (response.Trim().Equals("No Players Connected", StringComparison.OrdinalIgnoreCase))
            return new(serverId, measuredAt, []) { CompleteListConfirmed = true };
        var players = new List<ReportedServerPlayer>();
        var indexes = new HashSet<int>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in response.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dot = line.IndexOf('.');
            var comma = line.LastIndexOf(',');
            if (dot < 1 || comma <= dot + 1
                || !int.TryParse(line.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                || !indexes.Add(index)) throw new InvalidDataException("The player response format is not recognized.");
            var name = line[(dot + 1)..comma].Trim();
            var account = line[(comma + 1)..].Trim();
            if (name.Length is < 1 or > 256 || account.Length is < 1 or > 256
                || account.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ':' and not '-' and not '_')
                || !identities.Add(account)) throw new InvalidDataException("The player response format is not recognized.");
            players.Add(new(name, account));
        }
        if (players.Count == 0) throw new InvalidDataException("The server did not return a recognized player list.");
        return new(serverId, measuredAt, players);
    }
}
