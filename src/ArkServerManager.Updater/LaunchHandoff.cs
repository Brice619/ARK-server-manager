using System.Diagnostics;
using ArkServerManager.Core;

internal static class LaunchHandoff
{
    private sealed record ManagerReady(string Status, int ProtocolVersion);
    public static async Task<int> RunAsync(string application, string payload, string recoveryParent,
        string runtime, int parentId, long parentStartTicks, string resultPath)
    {
        if (!Path.IsPathFullyQualified(resultPath) || File.Exists(resultPath) || File.Exists(resultPath + ".ready"))
            throw new ArgumentException("A new absolute update result path is required.");
        if (Path.GetFullPath(AppContext.BaseDirectory).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(application)) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("The updater must run outside the installed app folder.");
        for (var path = resultPath; path is not null; path = Path.GetDirectoryName(path))
            if (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked update handoff locations are unsupported.");
        using var parent = Process.GetProcessById(parentId);
        if (parent.StartTime.ToUniversalTime().Ticks != parentStartTicks || parent.HasExited)
            throw new InvalidOperationException("The launching app identity changed.");
        await VerifiedPayloadInstaller.VerifyInstalledAsync(application, runtime);
        await VerifiedPayloadInstaller.VerifyAsync(payload, runtime);
        await using (var ready = new FileStream(resultPath + ".ready", FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            await ready.WriteAsync(new byte[] { 1 }); ready.Flush(true);
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await parent.WaitForExitAsync(deadline.Token);
        AppUpdateLaunchOutcome outcome;
        try
        {
            using var updateOwnership = await OpenOwnershipAsync(application);
            var record = await AppUpdatePreparation.PrepareAsync(application, payload, recoveryParent, runtime, (work, ct) => work(ct));
            var journal = Path.Combine(record.RecoveryDirectory, "update.json");
            await AppUpdateLaunchRecovery.PublishAsync(record, resultPath);
            await AppUpdateTransaction.ApplyAsync(journal, (work, ct) => work(ct));
            outcome = new(true, "ARK Server Manager was updated.", journal);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or OperationCanceledException)
        {
            // Do not launch a partially replaced application or interfere with a live background manager.
            await VerifiedPayloadInstaller.VerifyInstalledAsync(application, runtime);
            outcome = new(false, "The app update could not complete. You are using the installed version.", null);
        }
        await new AtomicJsonStore<AppUpdateLaunchOutcome>(resultPath).SaveAsync(outcome);
        var executable = Path.Combine(application, OperatingSystem.IsWindows() ? "ArkServerManager.Desktop.exe" : "ArkServerManager.Desktop");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = application };
        start.ArgumentList.Add("--update-result"); start.ArgumentList.Add(resultPath);
        Process? launched = null;
        var startupFailed = false;
        try
        {
            launched = Process.Start(start);
            startupFailed = launched is null;
            if (launched is not null && outcome.Updated && outcome.RecoveryJournal is not null)
            {
                var until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
                while (DateTimeOffset.UtcNow < until)
                {
                    var state = await new AtomicJsonStore<AppUpdateRecord>(outcome.RecoveryJournal).LoadAsync();
                    if (state?.Phase == AppUpdatePhase.Confirmed) break;
                    if (launched.HasExited) { startupFailed = launched.ExitCode != 0; break; }
                    await Task.Delay(100);
                }
            }
        }
        catch (System.ComponentModel.Win32Exception) { startupFailed = true; }
        finally { launched?.Dispose(); }
        if (startupFailed && !outcome.Updated) throw new IOException("The installed app could not reopen.");
        if (startupFailed && outcome.Updated && outcome.RecoveryJournal is not null)
        {
            using (var ownership = await OpenOwnershipAsync(application))
                await AppUpdateTransaction.RestoreAsync(outcome.RecoveryJournal, (work, ct) => work(ct));
            outcome = new(false, "The updated app could not start. The previous version was restored.", outcome.RecoveryJournal);
            await new AtomicJsonStore<AppUpdateLaunchOutcome>(resultPath).SaveAsync(outcome);
            using var restored = Process.Start(start) ?? throw new IOException("The restored app could not reopen.");
        }
        return 0;
    }

    private static async Task<FileStream> OpenOwnershipAsync(string application)
    {
        var settings = InstalledAppSettingsFile.Read(application)!;
        var managerAccepted = false;
        try
        {
            var client = new LocalManagerClient(settings.ServerRoot);
            var ready = await client.CallAsync<ManagerReady>("ping", null, TimeSpan.FromMilliseconds(500));
            if (ready.Status != "ready" || ready.ProtocolVersion != LocalManagerProtocol.Version)
                throw new IOException("The background manager is incompatible with this updater.");
            var response = await client.CallAsync<IdleManagerShutdownResult>("app-update-idle-shutdown", null, TimeSpan.FromSeconds(10));
            if (!response.Accepted) throw new IOException(response.Message);
            managerAccepted = true;
        }
        catch (TimeoutException) { /* Exclusive ownership remains the final check if no manager responds. */ }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            try { return AppFileLease.OpenForUpdate(application); }
            catch (IOException) when (managerAccepted && !deadline.IsCancellationRequested)
            { await Task.Delay(100, deadline.Token); }
        }
    }
}
