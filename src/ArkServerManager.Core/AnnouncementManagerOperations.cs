using System.Text.Json;

namespace ArkServerManager.Core;

public sealed record ServerAnnouncementRequest(Guid ServerId, string Message, bool Confirmed)
{
    public override string ToString() => $"ServerAnnouncementRequest {{ ServerId = {ServerId}, Confirmed = {Confirmed}, Message = [omitted] }}";
}
public sealed record ServerAnnouncementResult(Guid ServerId, DateTimeOffset ResponseReceivedAt);

public sealed class AnnouncementManagerOperations(CatalogRepository repository, BackgroundOperations operations)
{
    public BackgroundOperation Start(ManagerRequest request)
    {
        var selected = request.Payload?.Deserialize<ServerAnnouncementRequest>()
            ?? throw new ArgumentException("Review an announcement.");
        if (selected.ServerId == Guid.Empty || !selected.Confirmed)
            throw new ArgumentException("Confirm the server and announcement before sending.");
        ValidateMessage(selected.Message);
        return operations.Submit(request.Id, "server-announcement", request.Payload, async (progress, ct) =>
        {
            var endpoint = await ServerRconConfiguration.ReadAsync(repository, selected.ServerId,
                TimeSpan.FromSeconds(5), RconResponseMode.SinglePacket, ct);
            progress(new("Sending the selected server's announcement request."));
            await new RconClient().ExecuteAsync(endpoint, "Broadcast " + selected.Message, ct);
            // A response proves transport completion, not acceptance or player delivery.
            return new ServerAnnouncementResult(selected.ServerId, DateTimeOffset.UtcNow);
        });
    }

    public static void ValidateMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > 1024 || message.Any(char.IsControl))
            throw new ArgumentException("Enter a single-line announcement of up to 1024 characters.");
        RconProtocol.Encode(2, 2, "Broadcast " + message);
    }
}
