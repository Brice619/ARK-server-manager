using System.Text.Json;
using System.Collections.Concurrent;
namespace ArkServerManager.Core;
public sealed record ServerRepairSelection(Guid ServerId, string Directory, bool StartAfterRepair, bool Confirmed);
public sealed record ServerRepairResult(Guid ServerId, bool FilesValidated, bool Started);
public enum ServerRepairPhase { Stopping, Validating, Validated, Starting, Completed }
public sealed record ServerRepairRecord(int SchemaVersion, Guid OperationId, Guid ServerId, string Directory, bool StartAfterRepair, ServerRepairPhase Phase);
public sealed record ServerRepairStatus(Guid ServerId, ServerRepairRecord? Record);
public sealed class ServerRepairManagerOperations(CatalogRepository repository, BackgroundOperations operations, ServerLifecycleCoordinator lifecycle,
    Func<ServerDefinition, CancellationToken, Task<InstallationStopCheck>> verifyStopped,
    Func<ServerDefinition, Action<OperationProgress>, CancellationToken, Task<bool>> repair, string? managementRoot = null)
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Gates = new();
    private AtomicJsonStore<ServerRepairRecord> Store(Guid id) => new(Path.Combine(managementRoot!, "app-settings", "repair-" + id.ToString("N") + ".json"));
    public async Task<ServerRepairStatus> ReadAsync(Guid id, CancellationToken ct = default)
    {
        if (id == Guid.Empty) throw new ArgumentException("Choose a server.");
        var record = managementRoot is null ? null : await Store(id).LoadAsync(ct);
        if (record is not null && (record.SchemaVersion != 1 || record.ServerId != id || record.OperationId == Guid.Empty
            || !Enum.IsDefined(record.Phase) || !Path.IsPathFullyQualified(record.Directory))) throw new InvalidDataException("Repair record needs review.");
        return new(id, record);
    }
    public BackgroundOperation Start(ManagerRequest request)
    {
        var selection = request.Payload?.Deserialize<ServerRepairSelection>() ?? throw new ArgumentException("Review server-file validation.");
        if (!selection.Confirmed || selection.ServerId == Guid.Empty) throw new ArgumentException("Confirm server-file validation.");
        return operations.Submit(request.Id, "server-files-repair", request.Payload, async (progress, ct) =>
        {
            var gate = Gates.GetOrAdd(selection.ServerId, _ => new(1, 1));
            await gate.WaitAsync(ct);
            try
            {
            var server = (await repository.ReadAsync(ct)).Servers.Single(server => server.Id == selection.ServerId);
            if (server.Directory != selection.Directory) throw new InvalidOperationException("The installation changed. Review it again.");
            var record = new ServerRepairRecord(1, request.Id, server.Id, server.Directory, selection.StartAfterRepair, ServerRepairPhase.Stopping);
            async Task Checkpoint(ServerRepairPhase phase)
            {
                record = record with { Phase = phase };
                if (managementRoot is not null) await Store(server.Id).SaveAsync(record, CancellationToken.None);
            }
            await Checkpoint(ServerRepairPhase.Stopping);
            progress(new("Saving and stopping only the selected server before file validation."));
            var stopped = await lifecycle.ExecuteMemberAsync(server.Id, LifecycleAction.Stop, ct);
            if (!stopped.Success) throw new InvalidOperationException("The selected server could not stop. Its files were left untouched.");
            await lifecycle.WithStoppedMemberAsync(server, async token =>
            {
                await Checkpoint(ServerRepairPhase.Validating);
                progress(new("Checking server files with SteamCMD. Missing or damaged package files will be downloaded."));
                if (!await repair(server, progress, token)) throw new IOException("Server-file validation did not complete. The server remains stopped.");
                await Checkpoint(ServerRepairPhase.Validated);
            }, verifyStopped, ct);
            if (selection.StartAfterRepair)
            {
                await Checkpoint(ServerRepairPhase.Starting);
                progress(new("Starting the selected server after file validation."));
                var started = await lifecycle.ExecuteMemberAsync(server.Id, LifecycleAction.Start, ct);
                if (!started.Success) throw new InvalidOperationException("Files were validated, but the selected server could not start. Review its activity log.");
            }
            await Checkpoint(ServerRepairPhase.Completed);
            return new ServerRepairResult(server.Id, true, selection.StartAfterRepair);
            }
            finally { gate.Release(); }
        });
    }
}
