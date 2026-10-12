using System.Text.Json;
namespace ArkServerManager.Core;

public sealed record AppUpdateRecoveryPointer(int SchemaVersion, Guid OperationId, string ApplicationDirectory, string Journal, string Runtime, string ResultPath);
public sealed record AppUpdateLaunchRecoveryStatus(AppUpdateRecoveryPointer Pointer, AppUpdateRecord Record);

/// <summary>Durable handoff identity for a launcher outside the files being replaced.</summary>
public static class AppUpdateLaunchRecovery
{
    public static bool RequiresRollback(AppUpdatePhase phase) => phase switch
    {
        AppUpdatePhase.Prepared or AppUpdatePhase.Applying or AppUpdatePhase.RollingBack => true,
        AppUpdatePhase.Applied or AppUpdatePhase.RolledBack or AppUpdatePhase.Confirmed => false,
        _ => throw new InvalidDataException("Unknown update recovery phase.")
    };
    /// <summary>Caller must execute independently of the installed application being repaired.</summary>
    public static async Task<AppUpdateLaunchRecoveryStatus?> RecoverBeforeLaunchAsync(string application, CancellationToken ct = default)
    {
        var initial = await ReadAsync(application, ct);
        if (initial is null || !RequiresRollback(initial.Record.Phase)) return initial;
        using var ownership = AppFileLease.OpenForUpdate(application);
        var current = await ReadAsync(application, ct) ?? throw new IOException("Update recovery identity disappeared.");
        if (current.Pointer != initial.Pointer) throw new IOException("Update recovery identity changed. Review it before launching.");
        if (!RequiresRollback(current.Record.Phase)) return current;
        var restored = await AppUpdateTransaction.RestoreAsync(current.Pointer.Journal, (work, token) => work(token), ct);
        await VerifiedPayloadInstaller.VerifyInstalledAsync(application, current.Pointer.Runtime, ct);
        await new AtomicJsonStore<AppUpdateLaunchOutcome>(current.Pointer.ResultPath, privateFile: true).SaveAsync(
            new(false, "An interrupted app update was recovered. The previous installed version was restored.", current.Pointer.Journal), ct);
        return new(current.Pointer, restored);
    }
    public static string PointerPath(string application) => Path.Combine(Path.GetFullPath(application), "app-settings", "update-launch-recovery.json");
    public static async Task PublishAsync(AppUpdateRecord record, string resultPath, CancellationToken ct = default)
    {
        var pointer = new AppUpdateRecoveryPointer(1, record.Id, record.ApplicationDirectory, Path.Combine(record.RecoveryDirectory, "update.json"), record.Replacement.Runtime, resultPath);
        Validate(record.ApplicationDirectory, pointer, record);
        var path = PointerPath(record.ApplicationDirectory); RejectLinks(path); RejectLinks(pointer.Journal); RejectLinks(resultPath);
        await new AtomicJsonStore<AppUpdateRecoveryPointer>(path, privateFile: true).SaveAsync(pointer, ct);
    }
    public static async Task<AppUpdateLaunchRecoveryStatus?> ReadAsync(string application, CancellationToken ct = default)
    {
        var path = PointerPath(application); RejectLinks(path);
        var pointer = await CoordinatedBackupRecords.ReadBoundedAsync<AppUpdateRecoveryPointer>(path, ct);
        if (pointer is null) return null;
        if (pointer.Journal is null || !Path.IsPathFullyQualified(pointer.Journal)) throw new InvalidDataException("Update recovery journal requires an absolute path.");
        RejectLinks(pointer.Journal);
        await using var input = await AtomicRecordReader.OpenAsync(pointer.Journal, ct);
        if (input.Length > 4 * 1024 * 1024) throw new InvalidDataException("Update recovery journal is too large.");
        var record = await JsonSerializer.DeserializeAsync<AppUpdateRecord>(input, cancellationToken: ct) ?? throw new InvalidDataException("Update recovery journal is empty.");
        Validate(application, pointer, record);
        RejectLinks(pointer.ResultPath);
        return new(pointer, record);
    }
    public static void Validate(string application, AppUpdateRecoveryPointer pointer, AppUpdateRecord record)
    {
        static bool Same(string left, string right) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        if (pointer.SchemaVersion != 1 || pointer.OperationId == Guid.Empty || record.SchemaVersion != 1 || record.Id != pointer.OperationId
            || !Enum.IsDefined(record.Phase) || pointer.Runtime is not ("win-x64" or "linux-x64") || record.Replacement is null || record.Replacement.Runtime != pointer.Runtime
            || new[] { application, pointer.ApplicationDirectory, pointer.Journal, pointer.ResultPath, record.ApplicationDirectory, record.RecoveryDirectory }.Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            || !Same(application, pointer.ApplicationDirectory) || !Same(application, record.ApplicationDirectory)
            || !Same(pointer.Journal, Path.Combine(record.RecoveryDirectory, "update.json"))
            || !Same(Path.GetDirectoryName(pointer.ResultPath)!, Path.GetDirectoryName(record.RecoveryDirectory)!))
            throw new InvalidDataException("Update launch recovery does not match the installed application and journal.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(application));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (Same(root, record.RecoveryDirectory) || Path.GetFullPath(record.RecoveryDirectory).StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("Update recovery must remain outside the application files.");
    }
    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked update recovery locations are unsupported.");
    }
}
