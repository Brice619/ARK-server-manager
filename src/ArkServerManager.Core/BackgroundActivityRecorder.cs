using System.Text.Json;

namespace ArkServerManager.Core;

public static class BackgroundActivityRecorder
{
    public static async Task RecordAsync(ActivityJournal journal, BackgroundOperation operation)
    {
        var time = operation.FinishedAt ?? DateTimeOffset.UtcNow;
        var severity = operation.State == BackgroundOperationState.Failed ? ActivitySeverity.Error
            : operation.State == BackgroundOperationState.Canceled ? ActivitySeverity.Warning : ActivitySeverity.Information;
        var message = operation.Error ?? $"{operation.Kind} operation {operation.State.ToString().ToLowerInvariant()}.";
        Guid? owner=null;var category=operation.Kind;
        if(operation.State==BackgroundOperationState.Succeeded&&operation.Result is {} value&&value.ValueKind==JsonValueKind.Object&&value.TryGetProperty("ServerId",out var id)&&id.TryGetGuid(out var server)&&server!=Guid.Empty)
        {
            owner=server;
            if(operation.Kind=="server-configuration-save"){category="settings";message="Settings saved";}
            if(operation.Kind=="server-mod-order-save"){category="mods";message="Mod list saved";}
        }
        IReadOnlyList<MemberOperationResult> failures = [];
        if (operation.State == BackgroundOperationState.Succeeded && operation.Kind is "server-lifecycle" or "server-member-lifecycle" or "server-shutdown" or "server-automatic-startup")
        {
            try
            {
                var result = operation.Result?.Deserialize<LifecycleOperationResult>();
                if (result?.Members is null || result.Members.Any(m => m is null || m.ServerId == Guid.Empty || !Enum.IsDefined(m.State)))
                    throw new InvalidDataException("Member results are unavailable.");
                failures = result.Members.Where(m => !m.Success).ToArray();
                severity = failures.Count == 0 ? ActivitySeverity.Information : ActivitySeverity.Error;
                message = failures.Count == 0 ? $"Server action completed for {result.Members.Count} managed servers."
                    : $"Server action finished with {failures.Count} of {result.Members.Count} managed servers reporting failure.";
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or NotSupportedException)
            {
                severity = ActivitySeverity.Error;
                message = "The server job finished, but its member results could not be read. Check server status before trying again.";
            }
        }
        await journal.AppendAsync(new(Guid.NewGuid(), time, category, severity, message, ServerId:owner, OperationId: operation.Id));
        foreach (var member in failures)
            await journal.AppendAsync(new(Guid.NewGuid(), time, "server", ActivitySeverity.Error,
                member.Error ?? "The server action failed. Check its status and console.", member.ServerId,
                operation.Id, Stage: "lifecycle"));
    }
}
