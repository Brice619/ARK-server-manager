using System.Text.Json;

namespace ArkServerManager.Core;

public sealed record ServerWelcomeEdit(Guid ServerId, string SourceSHA256, string Message, int? DurationSeconds, bool Confirmed)
{
    public override string ToString() => $"Welcome edit for {ServerId}; content omitted";
}
public sealed record ServerWelcomeEditResult(Guid ServerId, bool Saved);

public sealed class ServerWelcomeManagerOperations(CatalogRepository repository, BackgroundOperations operations,
    Func<ServerDefinition, Func<CancellationToken, Task>, CancellationToken, Task> withStoppedServer)
{
    public BackgroundOperation Start(ManagerRequest request)
    {
        var edit = request.Payload?.Deserialize<ServerWelcomeEdit>() ?? throw new ArgumentException("Review the welcome settings.");
        if (edit.ServerId == Guid.Empty || !edit.Confirmed)
            throw new ArgumentException("Confirm the selected server's welcome changes before saving.");
        // Validate choices before submitting a job or entering a file-operation scope.
        ServerWelcomeConfiguration.Apply("", edit.Message, edit.DurationSeconds);
        if (edit.SourceSHA256 is null || edit.SourceSHA256.Length != 64 || edit.SourceSHA256.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("Load the server configuration before saving.");
        return operations.Submit(request.Id, "server-welcome-save", request.Payload, async (progress, ct) =>
        {
            progress(new("Checking the selected map and saving its welcome settings."));
            var source = await ServerConfigurationReader.ReadAsync(repository, new(edit.ServerId, "GameUserSettings.ini"), ct);
            if (source.Text is null || !string.Equals(source.SourceSHA256, edit.SourceSHA256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The configuration changed or disappeared. Reload it before saving.");
            var updated = ServerWelcomeConfiguration.Apply(source.Text, edit.Message, edit.DurationSeconds);
            await ServerConfigurationWriter.WriteAsync(repository,
                new(edit.ServerId, "GameUserSettings.ini", edit.SourceSHA256, updated), withStoppedServer, ct);
            return new ServerWelcomeEditResult(edit.ServerId, true);
        });
    }
}
