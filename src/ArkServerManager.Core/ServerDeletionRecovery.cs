using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArkServerManager.Core;

public enum DeletionRecoveryAction { None, RestoreStagedFiles, FinishFileRemoval, ReviewRequired }
public sealed record DeletionRecoveryAssessment(DeletionRecoveryAction Action, string Message);
public sealed record DeletionRecoveryNotice(Guid? OperationId, Guid? ServerId, string? ServerName,
    DeletionRecoveryAction Action, string Message);
public sealed record DeletionRecoverySelection(Guid OperationId, Guid ServerId, string ConfirmedName, DeletionRecoveryAction Action);
public sealed record RecordedDeletionStatus(ServerDeletionRecord Record, DeletionRecoveryAssessment Assessment);

/// <summary>Assesses and recovers deletion state under an explicitly supplied stopped-file scope.</summary>
public static class ServerDeletionRecovery
{
    public static async Task<RecordedDeletionStatus?> ReadRecordedAsync(CatalogRepository repository,
        string managementRoot, Guid operationId, CancellationToken ct = default)
    {
        if (operationId == Guid.Empty || !Path.IsPathFullyQualified(managementRoot))
            throw new ArgumentException("Choose a recorded deletion and management folder.");
        var path = Path.Combine(Path.GetFullPath(managementRoot), "app-settings", "deletions", operationId.ToString("N") + ".json");
        RejectLinks(path);
        if (!File.Exists(path)) return null;
        var record = await ReadRecordAsync(path, ct);
        if (record.OperationId != operationId) throw new InvalidDataException("The deletion record identity does not match.");
        return new(record, Assess(await repository.ReadAsync(ct), record, managementRoot));
    }

    public static async Task<IReadOnlyList<DeletionRecoveryNotice>> ScanAsync(CatalogRepository repository,
        string managementRoot, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(managementRoot)) throw new ArgumentException("Choose an installed management folder.");
        var directory = Path.Combine(Path.GetFullPath(managementRoot), "app-settings", "deletions");
        RejectLinks(directory);
        if (!Directory.Exists(directory)) return [];
        var catalog = await repository.ReadAsync(ct);
        var notices = new List<DeletionRecoveryNotice>();
        var count = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            ct.ThrowIfCancellationRequested();
            if (++count > 1024) throw new InvalidDataException("Too many deletion recovery records. Review the recovery folder.");
            Guid? operation = null;
            try
            {
                var name = Path.GetFileName(path);
                if (!name.EndsWith(".json", StringComparison.Ordinal) || !Guid.TryParseExact(name[..^5], "N", out var id)
                    || id == Guid.Empty || name != id.ToString("N") + ".json")
                    throw new InvalidDataException("An unrecognized entry exists in the deletion recovery folder.");
                operation = id;
                var record = await ReadRecordAsync(path, ct);
                if (record.OperationId != id) throw new InvalidDataException("Recovery identity does not match its filename.");
                var assessment = Assess(catalog, record, managementRoot);
                if (assessment.Action != DeletionRecoveryAction.None)
                    notices.Add(new(id, record.ServerId, record.ServerName, assessment.Action, assessment.Message));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
                or ArgumentException or NotSupportedException)
            {
                notices.Add(new(operation, null, null, DeletionRecoveryAction.ReviewRequired,
                    "A deletion recovery record could not be validated. Its files have been left untouched."));
            }
        }
        return notices.OrderBy(n => n.OperationId).ToArray();
    }

    public static async Task<ServerDeletionRecord> RecoverAsync(CatalogRepository repository, string managementRoot,
        Guid operationId, Func<ServerDeletionRecord, Func<CancellationToken, Task>, CancellationToken, Task> withStoppedFiles,
        CancellationToken ct = default, DeletionRecoverySelection? selection = null)
    {
        if (operationId == Guid.Empty || !Path.IsPathFullyQualified(managementRoot)) throw new ArgumentException("Choose an installed recovery operation.");
        ArgumentNullException.ThrowIfNull(withStoppedFiles);
        var journal = Path.Combine(Path.GetFullPath(managementRoot), "app-settings", "deletions", operationId.ToString("N") + ".json");
        var expected = await ReadRecordAsync(journal, ct);
        if (expected.OperationId != operationId) throw new InvalidDataException("The recovery journal identity does not match its filename.");
        var initial = Assess(await repository.ReadAsync(ct), expected, managementRoot);
        if (initial.Action == DeletionRecoveryAction.ReviewRequired) throw new InvalidOperationException(initial.Message);
        CheckSelection(expected, initial, selection);
        var result = expected;
        var invoked = false;
        await withStoppedFiles(expected, async token =>
        {
            if (invoked) throw new InvalidOperationException("Recovery was authorized more than once.");
            invoked = true;
            await repository.ChangeAsync(async (catalog, commitToken) =>
            {
                var current = await ReadRecordAsync(journal, commitToken);
                if (current.OperationId != expected.OperationId || current.ServerId != expected.ServerId
                    || current.OriginalDirectory != expected.OriginalDirectory || current.StagedDirectory != expected.StagedDirectory
                    || current.ServerName != expected.ServerName || current.PreservedClusterId != expected.PreservedClusterId
                    || current.ProtectedSharedPaths is null || !current.ProtectedSharedPaths.SequenceEqual(expected.ProtectedSharedPaths))
                    throw new InvalidOperationException("Recovery locations changed. Review the operation again.");
                var assessment = Assess(new(1, [.. catalog.Clusters], [.. catalog.Servers]), current, managementRoot);
                if (assessment.Action == DeletionRecoveryAction.ReviewRequired) throw new InvalidOperationException(assessment.Message);
                CheckSelection(current, assessment, selection);
                commitToken.ThrowIfCancellationRequested();
                result = current;
                var store = new AtomicJsonStore<ServerDeletionRecord>(journal);
                if (assessment.Action == DeletionRecoveryAction.RestoreStagedFiles)
                {
                    RejectLinkedTree(current.StagedDirectory);
                    await store.SaveAsync(current with { Phase = ServerDeletionPhase.RollingBack }, commitToken);
                    commitToken.ThrowIfCancellationRequested();
                    Directory.Move(current.StagedDirectory, current.OriginalDirectory);
                    result = current with { Phase = ServerDeletionPhase.RolledBack };
                }
                else if (assessment.Action == DeletionRecoveryAction.FinishFileRemoval)
                {
                    RejectLinkedTree(current.StagedDirectory);
                    await store.SaveAsync(current with { Phase = ServerDeletionPhase.RemovingFiles }, commitToken);
                    commitToken.ThrowIfCancellationRequested();
                    Directory.Delete(current.StagedDirectory, recursive: true);
                    result = current with { Phase = ServerDeletionPhase.FilesRemoved };
                }
                else if (current.Phase == ServerDeletionPhase.RollingBack)
                    result = current with { Phase = ServerDeletionPhase.RolledBack };
                else if (current.Phase == ServerDeletionPhase.RemovingFiles)
                    result = current with { Phase = ServerDeletionPhase.FilesRemoved };
                // Complete the journal after changing files even if the observer cancels at that boundary.
                await store.SaveAsync(result, CancellationToken.None);
            }, token);
        }, ct);
        if (!invoked) throw new InvalidOperationException("The stopped-file scope did not authorize recovery.");
        return result;
    }

    private static void CheckSelection(ServerDeletionRecord record, DeletionRecoveryAssessment assessment,
        DeletionRecoverySelection? selection)
    {
        if (selection is null) return;
        if (selection.OperationId != record.OperationId || selection.ServerId != record.ServerId
            || selection.ConfirmedName != record.ServerName || selection.Action != assessment.Action
            || selection.Action is not (DeletionRecoveryAction.RestoreStagedFiles or DeletionRecoveryAction.FinishFileRemoval))
            throw new InvalidOperationException("Recovery details changed. Review the selected action again.");
    }

    private static async Task<ServerDeletionRecord> ReadRecordAsync(string path, CancellationToken ct)
    {
        RejectLinks(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var buffer = new MemoryStream();
        var bytes = new byte[4096];
        int read;
        while ((read = await input.ReadAsync(bytes, ct)) > 0)
        {
            if (buffer.Length + read > 64 * 1024) throw new InvalidDataException("The recovery record is too large.");
            buffer.Write(bytes, 0, read);
        }
        return JsonSerializer.Deserialize<ServerDeletionRecord>(buffer.ToArray(), new JsonSerializerOptions
            { MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("The recovery record is empty.");
    }

    private static void RejectLinkedTree(string directory)
    {
        RejectLinks(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            RejectLinks(entry);
            if (Directory.Exists(entry)) RejectLinkedTree(entry);
        }
    }
    public static DeletionRecoveryAssessment Assess(CatalogDocument catalog, ServerDeletionRecord record, string managementRoot)
    {
        if (record.SchemaVersion != 1 || record.OperationId == Guid.Empty || record.ServerId == Guid.Empty
            || string.IsNullOrWhiteSpace(record.ServerName) || !Enum.IsDefined(record.Phase)
            || record.ProtectedSharedPaths is null || !Path.IsPathFullyQualified(record.OriginalDirectory)
            || !Path.IsPathFullyQualified(record.StagedDirectory))
            throw new InvalidDataException("The deletion recovery record is invalid.");
        var original = Path.TrimEndingDirectorySeparator(Path.GetFullPath(record.OriginalDirectory));
        var staged = Path.TrimEndingDirectorySeparator(Path.GetFullPath(record.StagedDirectory));
        if (!Path.IsPathFullyQualified(managementRoot) || Equal(original, Path.GetPathRoot(original)))
            throw new InvalidDataException("Recovery requires dedicated absolute folders.");
        if (!Equal(Path.GetDirectoryName(original), Path.GetDirectoryName(staged))
            || Path.GetFileName(staged) != ".ark-delete-" + record.OperationId.ToString("N"))
            throw new InvalidDataException("Deletion recovery locations do not match their recorded operation.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(managementRoot));
        if (Contains(original, root) || Contains(staged, root)) throw new InvalidDataException("Recovery would include the management folder.");
        if (record.PreservedClusterId is not null && record.ProtectedSharedPaths.Count == 0)
            throw new InvalidDataException("Shared cluster paths are unavailable for recovery.");
        foreach (var shared in record.ProtectedSharedPaths)
        {
            if (!Path.IsPathFullyQualified(shared)) throw new InvalidDataException("Shared recovery paths must be absolute.");
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(shared));
            if (Equal(full, Path.GetPathRoot(full))) throw new InvalidDataException("A shared recovery path cannot be a filesystem root.");
            if (Overlap(original, full) || Overlap(staged, full)) throw new InvalidDataException("Recovery overlaps shared cluster data.");
        }
        foreach (var other in catalog.Servers.Where(s => s.Id != record.ServerId))
        {
            var otherPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(other.Directory));
            if (Overlap(original, otherPath) || Overlap(staged, otherPath)) throw new InvalidDataException("Recovery overlaps another server.");
        }
        RejectLinks(original); RejectLinks(staged);
        if (File.Exists(original) || File.Exists(staged)) return new(DeletionRecoveryAction.ReviewRequired, "A recovery folder has been replaced by a file.");
        var server = catalog.Servers.SingleOrDefault(s => s.Id == record.ServerId);
        var hasOriginal = Directory.Exists(original); var hasStaged = Directory.Exists(staged);
        if (server is not null)
        {
            if (!Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(server.Directory)), original)
                || server.ClusterId != record.PreservedClusterId || server.Settings.Name != record.ServerName)
                return new(DeletionRecoveryAction.ReviewRequired, "The server registration changed after deletion began.");
            if (!hasOriginal && hasStaged && record.Phase is ServerDeletionPhase.Prepared or ServerDeletionPhase.Staged or ServerDeletionPhase.RollingBack)
                return new(DeletionRecoveryAction.RestoreStagedFiles, "The registration remains; restore its staged files before normal management resumes.");
            if (hasOriginal && !hasStaged && record.Phase is ServerDeletionPhase.Prepared or ServerDeletionPhase.RolledBack or ServerDeletionPhase.RollingBack)
                return new(DeletionRecoveryAction.None, "The server files and registration remain unchanged.");
        }
        else
        {
            if (!hasOriginal && hasStaged && record.Phase is ServerDeletionPhase.Staged or ServerDeletionPhase.CatalogRemoved or ServerDeletionPhase.RemovingFiles)
                return new(DeletionRecoveryAction.FinishFileRemoval, "The registration was removed; complete removal of its staged files.");
            if (!hasOriginal && !hasStaged && record.Phase is ServerDeletionPhase.CatalogRemoved or ServerDeletionPhase.FilesRemoved or ServerDeletionPhase.RemovingFiles)
                return new(DeletionRecoveryAction.None, "The server registration and files are removed.");
        }
        return new(DeletionRecoveryAction.ReviewRequired, "Deletion state is inconsistent. Review the recorded folders before changing files.");
    }

    private static bool Equal(string? a, string? b) => string.Equals(a, b,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool Contains(string parent, string child) => Equal(parent, child)
        || child.StartsWith(parent + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool Overlap(string a, string b) => Contains(a, b) || Contains(b, a);
    private static void RejectLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked recovery locations require separate validation.");
    }
}
