using System.Text.Json;

namespace ArkServerManager.Core;

public sealed record PresetApplicationSave(PresetApplicationReview Expected, SaveChoice Choice, bool Confirmed);
public sealed record PresetApplicationSaveResult(Guid ServerId, Guid PresetId, SettingsSaveResult Save);

public sealed class PresetApplicationManagerOperations(PresetApplicationReviewService review,
    PresetApplicationQueue queue, BackgroundOperations operations,
    Func<Func<IReadOnlyList<MemberOperationResult>, CancellationToken, Task>, CancellationToken, Task> withStatusScope,
    Func<Guid, CancellationToken, Task> requireNoConflictingChanges,
    Func<Guid, LifecycleAction, CancellationToken, Task<LifecycleOperationResult>> lifecycle,
    Func<Guid, CancellationToken, Task>? requireDiscardAllowed = null)
{
    public BackgroundOperation StartDiscard(ManagerRequest request)
    {
        var edit = request.Payload?.Deserialize<PresetApplicationDiscard>() ?? throw new ArgumentException("Choose a saved preset.");
        if (!edit.Confirmed || edit.ServerId == Guid.Empty) throw new ArgumentException("Confirm cancellation of the saved preset.");
        return operations.Submit(request.Id, "presets-application-discard", request.Payload, async (progress, ct) =>
        {
            progress(new("Checking that the saved preset has not begun applying."));
            await withStatusScope(async (statuses, token) =>
            {
                if (!statuses.Any(s => s.ServerId == edit.ServerId)) throw new ArgumentException("The selected server no longer exists.");
                if (requireDiscardAllowed is not null) await requireDiscardAllowed(edit.ServerId, token);
                await queue.DiscardUnstartedAsync(edit, token);
            }, ct);
            return new PresetApplicationDiscardResult(edit.ServerId, true);
        });
    }

    public BackgroundOperation Start(ManagerRequest request)
    {
        var edit = request.Payload?.Deserialize<PresetApplicationSave>() ?? throw new ArgumentException("Review a preset before saving.");
        if (!edit.Confirmed || edit.Expected is null || !Enum.IsDefined(edit.Choice) || edit.Choice == SaveChoice.Cancel)
            throw new ArgumentException("Confirm how to save the preset changes.");
        return operations.Submit(request.Id, "presets-application-save", request.Payload, async (progress, ct) => await ApplyAsync(edit, progress, ct));
    }

    public async Task<PresetApplicationSaveResult> ApplyAsync(PresetApplicationSave edit, Action<OperationProgress> progress, CancellationToken ct)
    {
        if (!edit.Confirmed || edit.Expected is null || !Enum.IsDefined(edit.Choice) || edit.Choice == SaveChoice.Cancel)
            throw new ArgumentException("Confirm how to save the preset changes.");
            progress(new("Checking the reviewed preset and selected server."));
            var running = false;
            await withStatusScope(async (statuses, token) =>
            {
                var status = statuses.Single(s => s.ServerId == edit.Expected.ServerId);
                if (status.State is ServerState.Starting or ServerState.Stopping or ServerState.Recovering)
                    throw new InvalidOperationException("Wait for the selected server's current action before saving a preset.");
                if (status.State == ServerState.Running && !status.Managed)
                    throw new InvalidOperationException("The server is running outside this app. Review it before saving a preset.");
                running = status.State == ServerState.Running && status.Managed;
                await requireNoConflictingChanges(edit.Expected.ServerId, token);
                var application = await review.RebuildReviewedAsync(edit.Expected, token);
                if (!running) await queue.VerifyStoppedAsync(application.Server, token);
                await queue.EnqueueAsync(application, token);
                if (!running) await queue.ApplyBeforeStartAsync(edit.Expected.ServerId, token);
            }, ct);
            if (!running)
                return new PresetApplicationSaveResult(edit.Expected.ServerId, edit.Expected.PresetId, new(true, false, false, "Preset applied."));
            if (edit.Choice == SaveChoice.SaveForNextStart)
                return new PresetApplicationSaveResult(edit.Expected.ServerId, edit.Expected.PresetId, new(true, true, false, "Preset saved for the next start."));
            progress(new("Stopping only the selected server before applying its preset."));
            if (!(await lifecycle(edit.Expected.ServerId, LifecycleAction.Stop, ct)).Success)
                return new PresetApplicationSaveResult(edit.Expected.ServerId, edit.Expected.PresetId,
                    new(true, true, false, "The server did not stop. Its preset remains saved for the next start."));
            progress(new("Starting the selected server with its saved preset."));
            if (!(await lifecycle(edit.Expected.ServerId, LifecycleAction.Start, ct)).Success)
                throw new InvalidOperationException("The selected server could not start. Review the server and its saved preset before retrying.");
            return new PresetApplicationSaveResult(edit.Expected.ServerId, edit.Expected.PresetId, new(true, false, true, "Preset applied and server restarted."));
    }
}
