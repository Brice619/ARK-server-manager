using System.Text.Json;

namespace ArkServerManager.Core;

public sealed record BasicSettingsValues(string Name, int MaxPlayers, int GamePort, int QueryPort, int RconPort)
{
    public static BasicSettingsValues From(ServerSettings settings)
        => new(settings.Name, settings.MaxPlayers, settings.GamePort, settings.QueryPort, settings.RconPort);
    public ServerSettings ApplyTo(ServerSettings settings) => settings.Copy() with
        { Name = Name, MaxPlayers = MaxPlayers, GamePort = GamePort, QueryPort = QueryPort, RconPort = RconPort };
}

public sealed record BasicSettingsReview(Guid ServerId, string Directory, ArkEdition Edition, string Map,
    Guid? ClusterId, BasicSettingsValues Settings, string? SourceSHA256 = null,
    BasicSettingsValues? QueuedSettings = null, bool QueuedConfigurationChanged = false);
public sealed record BasicSettingsEdit(BasicSettingsReview Expected, BasicSettingsValues Desired, SaveChoice Choice, bool Confirmed);
public sealed record BasicSettingsEditResult(Guid ServerId, SettingsSaveResult Save);

/// <summary>Basic fields only; unrelated gameplay settings and credentials never come from a desktop payload.</summary>
public sealed class BasicSettingsManagerOperations(CatalogRepository repository, BackgroundOperations operations,
    SettingsChangeWorkflow workflow, Func<ServerDefinition, CancellationToken, Task<bool>> isRunning)
{
    public async Task<BasicSettingsReview> ReadAsync(Guid id, CancellationToken ct = default)
    {
        var server = (await repository.ReadAsync(ct)).Servers.Single(s => s.Id == id);
        var source = await ServerConfigurationReader.ReadAsync(repository, new(id, "GameUserSettings.ini"), ct);
        if (source.Text is null || source.SourceSHA256 is null) throw new InvalidDataException("The server configuration is missing.");
        var pending = await workflow.ReadPendingAsync(id, ct);
        return new(server.Id, server.Directory, server.Edition, server.Map, server.ClusterId, BasicSettingsValues.From(server.Settings), source.SourceSHA256,
            pending is null ? null : BasicSettingsValues.From(pending.Settings),
            pending?.SourceSHA256 is string hash && !string.Equals(hash, source.SourceSHA256, StringComparison.OrdinalIgnoreCase));
    }

    public BackgroundOperation Start(ManagerRequest request)
    {
        var edit = request.Payload?.Deserialize<BasicSettingsEdit>() ?? throw new ArgumentException("Review the server settings.");
        if (!edit.Confirmed || edit.Expected is null || edit.Expected.Settings is null || edit.Desired is null
            || edit.Expected.ServerId == Guid.Empty || !Enum.IsDefined(edit.Choice) || edit.Choice == SaveChoice.Cancel)
            throw new ArgumentException("Confirm a supported settings save action.");
        BasicServerConfiguration.Apply("", edit.Desired.ApplyTo(new ServerSettings()));
        if (edit.Expected.SourceSHA256 is not string hash || hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("Load current server settings before saving.");
        return operations.Submit(request.Id, "server-basic-settings-save", request.Payload, async (progress, ct) =>
        {
            progress(new("Checking the reviewed server settings."));
            var server = (await repository.ReadAsync(ct)).Servers.Single(s => s.Id == edit.Expected.ServerId);
            if (server.Directory != edit.Expected.Directory || server.Edition != edit.Expected.Edition
                || server.Map != edit.Expected.Map || server.ClusterId != edit.Expected.ClusterId
                || BasicSettingsValues.From(server.Settings) != edit.Expected.Settings)
                throw new InvalidOperationException("The reviewed server changed. Reload its settings before saving.");
            var running = await isRunning(server, ct);
            var source = await ServerConfigurationReader.ReadAsync(repository, new(server.Id, "GameUserSettings.ini"), ct);
            if (!string.Equals(source.SourceSHA256, edit.Expected.SourceSHA256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The configuration changed. Reload settings before saving.");
            var saved = await workflow.SaveAsync(server.Id, edit.Desired.ApplyTo(server.Settings), running, edit.Choice, ct, server, edit.Expected.SourceSHA256);
            return new BasicSettingsEditResult(server.Id, saved);
        });
    }
}
