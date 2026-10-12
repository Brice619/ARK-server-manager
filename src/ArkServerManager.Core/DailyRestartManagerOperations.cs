using System.Text.Json;

namespace ArkServerManager.Core;
public sealed class DailyRestartManagerOperations(DailyRestartSchedules schedules, BackgroundOperations operations)
{
    public BackgroundOperation Start(ManagerRequest request)
    {
        var edit = request.Payload?.Deserialize<DailyRestartSave>() ?? throw new ArgumentException("Choose a restart schedule.");
        if (!edit.Confirmed || edit.ServerId == Guid.Empty) throw new ArgumentException("Confirm the restart schedule.");
        return operations.Submit(request.Id, "server-restart-schedule", request.Payload, async (progress, ct) =>
        {
            progress(new("Saving the selected map's restart schedule."));
            await schedules.SaveAsync(edit.ServerId, edit.Time, edit.Enabled, true, ct);
            return await schedules.ReadAsync(edit.ServerId, ct);
        });
    }
}
