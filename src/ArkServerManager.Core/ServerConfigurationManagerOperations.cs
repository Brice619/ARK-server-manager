using System.Text;
using System.Text.Json;

namespace ArkServerManager.Core;

public sealed class ConfirmedConfigurationEdit
{
    public ServerConfigurationEdit? Edit { get; init; }
    public bool Confirmed { get; init; }
    public SaveChoice Choice { get; init; } = SaveChoice.SaveForNextStart;
    public override string ToString() => "Confirmed configuration edit; contents omitted";
}
public sealed record ServerConfigurationEditResult(Guid ServerId, string FileName, bool Saved)
{ public SettingsSaveResult? Save { get; init; } }

public sealed class ServerConfigurationManagerOperations(CatalogRepository repository, BackgroundOperations operations,
    Func<ServerDefinition, Func<CancellationToken, Task>, CancellationToken, Task> withStoppedServer,
    PendingConfigurationEdits? pending = null,
    Func<Func<IReadOnlyList<MemberOperationResult>, CancellationToken, Task>, CancellationToken, Task>? withStatusScope = null,
    Func<Guid, CancellationToken, Task>? requireNoConflicts = null,
    Func<Guid, LifecycleAction, CancellationToken, Task<LifecycleOperationResult>>? lifecycle = null)
{
    public BackgroundOperation StartResolution(ManagerRequest request)
    {
        var edit = request.Payload?.Deserialize<PendingConfigurationResolution>() ?? throw new ArgumentException("Review saved configuration recovery.");
        if (!edit.Confirmed || pending is null || withStatusScope is null) throw new ArgumentException("Confirm the selected recovery choice.");
        return operations.Submit(request.Id, "server-configuration-resolve", request.Payload, async (progress, ct) =>
        {
            await withStatusScope(async (statuses, token) =>
            {
                var status = statuses.Single(s => s.ServerId == edit.ServerId);
                if (status.State is not (ServerState.Stopped or ServerState.Failed)) throw new InvalidOperationException("Stop this map before resolving its saved configuration.");
                progress(new("Rechecking the current file and saved edit."));
                var file = await pending.ResolveAsync(request.Id, edit, token);
                if (edit.Choice == ConfigurationResolutionChoice.UseSavedText) await pending.ApplyBeforeStartAsync(edit.ServerId, token, file);
            }, ct);
            return new PendingConfigurationResolutionResult(edit.ServerId, edit.Id,
                edit.Choice == ConfigurationResolutionChoice.UseSavedText ? request.Id : null,
                edit.Choice == ConfigurationResolutionChoice.UseSavedText, edit.Choice == ConfigurationResolutionChoice.KeepCurrentFile);
        });
    }
    public BackgroundOperation StartDiscard(ManagerRequest request)
    {
        var edit = request.Payload?.Deserialize<PendingConfigurationDiscard>() ?? throw new ArgumentException("Choose a saved configuration edit.");
        if (!edit.Confirmed || edit.ServerId == Guid.Empty || edit.Id == Guid.Empty || pending is null || withStatusScope is null)
            throw new ArgumentException("Confirm cancellation of the selected saved edit.");
        return operations.Submit(request.Id, "server-configuration-discard", request.Payload, async (progress, ct) =>
        {
            progress(new("Checking the saved edit before cancellation."));
            await withStatusScope(async (statuses, token) =>
            {
                if (!statuses.Any(s => s.ServerId == edit.ServerId)) throw new InvalidOperationException("The selected map no longer exists.");
                await pending.DiscardAsync(edit, token);
            }, ct);
            return new PendingConfigurationDiscardResult(edit.ServerId, edit.Id, true);
        });
    }
    public BackgroundOperation Start(ManagerRequest request)
    {
        var selection = request.Payload?.Deserialize<ConfirmedConfigurationEdit>()
            ?? throw new ArgumentException("Review the configuration changes.");
        var edit = selection.Edit;
        if (!selection.Confirmed || !Enum.IsDefined(selection.Choice) || selection.Choice == SaveChoice.Cancel || edit is null || edit.ServerId == Guid.Empty
            || edit.FileName is not ("GameUserSettings.ini" or "Game.ini"))
            throw new ArgumentException("Confirm the selected configuration file before saving.");
        if (edit.Text is null || edit.Text.Contains('\0') || Encoding.UTF8.GetByteCount(edit.Text) > 4 * 1024 * 1024
            || edit.SourceSHA256 is null || edit.SourceSHA256.Length != 64 || edit.SourceSHA256.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("Load a supported configuration snapshot before saving.");
        return operations.Submit(request.Id, "server-configuration-save", request.Payload, async (progress, ct) =>
        {
            progress(new("Checking the selected map and saving its configuration."));
            if (pending is not null && withStatusScope is not null && lifecycle is not null && requireNoConflicts is not null)
            {
                var running = false;
                await withStatusScope(async (statuses, token) =>
                {
                    var status = statuses.Single(s => s.ServerId == edit.ServerId);
                    if (status.State is ServerState.Starting or ServerState.Stopping or ServerState.Recovering || status.State == ServerState.Running && !status.Managed)
                        throw new InvalidOperationException("Wait for the current server action or review the externally running server before saving.");
                    running = status.State == ServerState.Running && status.Managed;
                    await requireNoConflicts(edit.ServerId, token);
                    await pending.EnqueueAsync(request.Id, edit, token);
                    if (!running) await pending.ApplyBeforeStartAsync(edit.ServerId, token);
                }, ct);
                var result = new ServerConfigurationEditResult(edit.ServerId, edit.FileName, true);
                if (!running) return result with { Save = new(true, false, false, "Configuration applied.") };
                if (selection.Choice == SaveChoice.SaveForNextStart)
                    return result with { Save = new(true, true, false, "Configuration saved for the next start.") };
                if (!(await lifecycle(edit.ServerId, LifecycleAction.Stop, ct)).Success)
                    return result with { Save = new(true, true, false, "The map did not stop. Configuration remains saved for its next start.") };
                if (!(await lifecycle(edit.ServerId, LifecycleAction.Start, ct)).Success)
                    throw new InvalidOperationException("The map could not restart. Review its pending configuration and current server state.");
                return result with { Save = new(true, false, true, "Configuration applied and selected map restarted.") };
            }
            await ServerConfigurationWriter.WriteAsync(repository, edit, withStoppedServer, ct);
            return new ServerConfigurationEditResult(edit.ServerId, edit.FileName, true);
        });
    }
}
