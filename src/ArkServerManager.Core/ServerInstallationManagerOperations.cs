using System.Text.Json;

namespace ArkServerManager.Core;

public sealed record ServerInstallationRequest(Guid ServerId, ArkEdition Edition, ServerPlatform Platform,
    bool InstallationConsent, bool Resume);
public sealed record ServerInstallationInventoryEntry(Guid ServerId, ArkEdition Edition, ServerPlatform Platform,
    ServerInstallationRecord? Record, BackgroundOperation? Job);
public sealed record ServerInstallationInventory(IReadOnlyList<ServerInstallationInventoryEntry> Entries, int UnreadableRecords);

public sealed class ServerInstallationManagerOperations(string managementRoot, BackgroundOperations operations,
    SemaphoreSlim fileOperationGate, Func<CancellationToken, Task<ServerInstallationPreparation>> createPreparation,
    Func<ServerInstallationRecord, Func<CancellationToken, Task>, CancellationToken, Task> withStoppedFilesAndInstaller)
{
    private readonly object routingGate = new();
    private readonly Dictionary<Guid, (ServerInstallationRequest Selection, Guid RequestId)> latest = [];

    public async Task<ServerInstallationInventory> InventoryAsync(CancellationToken ct = default)
    {
        var saved = await ServerInstallationPreparation.ReadInventoryAsync(managementRoot, ct);
        var entries = saved.Records.ToDictionary(r => r.ServerId,
            r => new ServerInstallationInventoryEntry(r.ServerId, r.Edition, r.Platform, r, null));
        lock (routingGate)
        {
            foreach (var (id, request) in latest)
            {
                var job = operations.Find(request.RequestId);
                if (job is null) continue;
                entries[id] = new(id, request.Selection.Edition, request.Selection.Platform,
                    entries.GetValueOrDefault(id)?.Record, job);
            }
        }
        return new(entries.Values.OrderBy(e => e.ServerId).ToArray(), saved.UnreadableRecords);
    }

    public object Start(ManagerRequest request)
    {
        var selection = request.Payload?.Deserialize<ServerInstallationRequest>()
            ?? throw new ArgumentException("Choose a new server installation.");
        if (selection.ServerId == Guid.Empty || !selection.InstallationConsent)
            throw new ArgumentException("Confirm the selected server-file installation.");
        var nativePlatform = OperatingSystem.IsWindows() ? ServerPlatform.Windows : ServerPlatform.Linux;
        if (selection.Platform != nativePlatform) throw new NotSupportedException("Choose the native server package for this computer.");
        SteamCmdInstaller.Arguments(new(selection.Edition, selection.Platform,
            Path.Combine(Path.GetFullPath(managementRoot), "servers", ".ark-create-" + selection.ServerId.ToString("N")), true));
        lock (routingGate)
        {
        foreach (var id in latest.Where(p => operations.Find(p.Value.RequestId) is null).Select(p => p.Key).ToArray()) latest.Remove(id);
        if (latest.TryGetValue(selection.ServerId, out var previous) && previous.RequestId != request.Id
            && operations.Find(previous.RequestId)?.State is BackgroundOperationState.Queued or BackgroundOperationState.Running)
            throw new InvalidOperationException("This setup already has a running installation. Reconnect to that job.");
        var submitted = operations.Submit(request.Id, "server-installation", request.Payload, async (progress, token) =>
        {
            await fileOperationGate.WaitAsync(token);
            try
            {
                progress(new("Checking required installation tools."));
                var preparation = await createPreparation(token);
                progress(new(selection.Resume ? "Checking and resuming recorded server files." : "Reusing installed game files, or downloading a missing game package."));
                // Do not stream raw tool output into the user-facing progress summary.
                ServerInstallationRecord result;
                if (selection.Resume)
                    result = await preparation.ResumeAsync(managementRoot, selection.ServerId, true,
                        async (record, change, ct) =>
                        {
                            if (record.Edition != selection.Edition || record.Platform != selection.Platform)
                                throw new InvalidOperationException("The recorded game or platform changed. Review this setup.");
                            await withStoppedFilesAndInstaller(record, change, ct);
                        }, ct: token);
                else
                {
                    ServerInstallationRecord? prepared = null;
                    var root = Path.GetFullPath(managementRoot);
                    var scope = new ServerInstallationRecord(1, selection.ServerId, selection.Edition, selection.Platform,
                        Path.Combine(root, "servers", ".ark-create-" + selection.ServerId.ToString("N")),
                        Path.Combine(root, "servers", selection.ServerId.ToString("N")), ServerInstallationPhase.Prepared);
                    await withStoppedFilesAndInstaller(scope, async ct =>
                    {
                        if (prepared is not null) throw new InvalidOperationException("Installation was authorized more than once.");
                        prepared = await preparation.PrepareAsync(managementRoot, selection.ServerId, selection.Edition, selection.Platform, true, ct: ct);
                    }, token);
                    result = prepared ?? throw new InvalidOperationException("Installation was not authorized by stopped-file checks.");
                }
                progress(new("Server files checked. Configuration and server creation remain next."));
                return result;
            }
            finally { fileOperationGate.Release(); }
        });
        latest[selection.ServerId] = (selection, request.Id);
        return submitted;
        }
    }
}
