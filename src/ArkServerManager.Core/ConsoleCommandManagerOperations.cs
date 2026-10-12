using System.Text.Json;

namespace ArkServerManager.Core;

public sealed record ServerConsoleCommand(Guid ServerId, string Command)
{
    public override string ToString() => $"ServerConsoleCommand {{ ServerId = {ServerId}, Command = [omitted] }}";
}
public sealed record ServerConsoleResponse(Guid ServerId, DateTimeOffset ReceivedAt, string Response)
{
    public override string ToString() => $"ServerConsoleResponse {{ ServerId = {ServerId}, ReceivedAt = {ReceivedAt}, Response = [omitted] }}";
}

public sealed class ConsoleCommandManagerOperations(CatalogRepository repository, BackgroundOperations operations)
{
    public static void ValidateCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > 2048 || command.Any(char.IsControl))
            throw new ArgumentException("Enter a single administrator command of up to 2048 characters.");
        RconProtocol.Encode(2, 2, command);
    }

    public BackgroundOperation Start(ManagerRequest request)
    {
        var selection = request.Payload?.Deserialize<ServerConsoleCommand>() ?? throw new ArgumentException("Choose a command and server.");
        if (selection.ServerId == Guid.Empty) throw new ArgumentException("Choose a registered server.");
        ValidateCommand(selection.Command);
        return operations.Submit(request.Id, "server-console-command", request.Payload, async (progress, ct) =>
        {
            var endpoint = await ServerRconConfiguration.ReadAsync(repository, selection.ServerId,
                TimeSpan.FromSeconds(5), RconResponseMode.SinglePacket, ct);
            progress(new("Sending the selected server's administrator command."));
            var response = await new RconClient().ExecuteAsync(endpoint, selection.Command, ct);
            // The response remains an in-memory job result; activity logging never records its contents.
            return new ServerConsoleResponse(selection.ServerId, DateTimeOffset.UtcNow, response);
        });
    }
}
