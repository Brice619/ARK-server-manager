using System.Text.Json;
namespace ArkServerManager.Core;

public static class LinuxNotificationSetup
{
    public static async Task EnsureAsync(LocalManagerClient client, Action<string> progress, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        if (LinuxNotificationToolPlan.LibrariesPresent()) return;
        var id = Guid.NewGuid();
        var job = await client.CallAsync<BackgroundOperation>("linux-notification-tools-start", true, TimeSpan.FromSeconds(8), ct, id);
        while (true)
        {
            if (job.Id != id || job.Kind != "linux-notification-tools" || !Enum.IsDefined(job.State)) throw new InvalidDataException();
            if (job.State is not (BackgroundOperationState.Queued or BackgroundOperationState.Running)) break;
            progress(job.Progress?.Message ?? "Preparing desktop notification support…");
            await Task.Delay(500, ct);
            job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(id), TimeSpan.FromSeconds(8), ct);
        }
        var result = job.Result?.Deserialize<LinuxBackupToolSetupResult>();
        if (job.State != BackgroundOperationState.Succeeded || result is null || !result.Ready || result.Executable != "libnotify.so.4")
            throw new IOException("Desktop notification support could not be prepared. Recorded package work is retained for review.");
    }
}
