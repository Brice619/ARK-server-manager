using System.Text.Json;
using ArkServerManager.Core;

// This entry point runs from a separate staged tool folder, after the app has closed.
try
{
    if (args is ["--handoff", var handoffApp, var handoffPayload, var handoffRecovery, var handoffRuntime,
        var parentText, var parentStartText, var resultPath] && int.TryParse(parentText, out var parentId) && long.TryParse(parentStartText, out var parentTicks))
        return await LaunchHandoff.RunAsync(handoffApp, handoffPayload, handoffRecovery, handoffRuntime, parentId, parentTicks, resultPath);
    AppUpdateRecord record;
    if (args is ["--apply", var application, var payload, var recoveryParent, var runtime])
    {
        if (Path.GetFullPath(AppContext.BaseDirectory).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(application)) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("The updater must run outside the installed app folder.");
        using var ownership = AppFileLease.OpenForUpdate(application);
        record = await AppUpdatePreparation.PrepareAsync(application, payload, recoveryParent, runtime, (work, ct) => work(ct));
        record = await AppUpdateTransaction.ApplyAsync(Path.Combine(record.RecoveryDirectory, "update.json"), (work, ct) => work(ct));
    }
    else if (args is ["--restore", var restoreApplication, var journal])
    {
        if (Path.GetFullPath(AppContext.BaseDirectory).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(restoreApplication)) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("The updater must run outside the installed app folder.");
        using var ownership = AppFileLease.OpenForUpdate(restoreApplication);
        // The transaction additionally validates the app path in its saved journal.
        await using var input = File.OpenRead(journal);
        if (input.Length > 4 * 1024 * 1024) throw new InvalidDataException("Update journal is too large.");
        var identity = await JsonSerializer.DeserializeAsync<AppUpdateRecord>(input)
            ?? throw new InvalidDataException("Update recovery identity is missing.");
        if (!string.Equals(Path.GetFullPath(restoreApplication), identity.ApplicationDirectory,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("The requested app does not match the recovery journal.");
        input.Close();
        record = await AppUpdateTransaction.RestoreAsync(journal, (work, ct) => work(ct));
    }
    else { Console.Error.WriteLine("Updater requires a prepared app payload or recovery journal."); return 2; }
    Console.WriteLine(JsonSerializer.Serialize(new { record.Id, record.Phase, record.ApplicationDirectory, record.RecoveryDirectory }));
    return 0;
}
catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or JsonException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("App update could not complete: " + error.Message);
    return 1;
}
