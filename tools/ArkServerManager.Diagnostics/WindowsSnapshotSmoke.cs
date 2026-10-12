using ArkServerManager.Core;
using System.Runtime.Versioning;

[SupportedOSPlatform("windows")]
internal static class WindowsSnapshotSmoke
{
    public static async Task<object> RunAsync(string helper, string output, bool elevate, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(output) || Directory.Exists(output) || File.Exists(output))
            throw new ArgumentException("Choose a new absolute evidence folder.");
        Directory.CreateDirectory(output);
        var saved = Path.Combine(output, "source", "Saved");
        var transfers = Path.Combine(output, "source", "Transfers");
        Directory.CreateDirectory(Path.Combine(saved, "SavedArks")); Directory.CreateDirectory(transfers);
        var world = Path.Combine(saved, "SavedArks", "world.ark");
        var player = Path.Combine(transfers, "player.arkprofile");
        await File.WriteAllTextAsync(world, "world-before", ct);
        await File.WriteAllTextAsync(player, "player-before", ct);
        await using var broker = new WindowsSnapshotBroker(helper);
        var capability = await broker.InitializeAsync(elevate, ct);
        SnapshotCapture first;
        string capturedWorld, capturedTransfers;
        var serverId = Guid.NewGuid(); var clusterId = Guid.NewGuid();
        var archive = new SnapshotArchive();
        var worldZip = Path.Combine(output, "world.zip"); var sharedZip = Path.Combine(output, "shared.zip");
        await using (var set = await broker.AcquireSetAsync([saved, transfers], ct))
        {
            first = set.Capture;
            await using var worldView = set.GetState(saved);
            await using var sharedView = set.GetState(transfers);
            capturedWorld = worldView.Directory; capturedTransfers = sharedView.Directory;
            await File.WriteAllTextAsync(world, "world-after", ct);
            await File.WriteAllTextAsync(player, "player-after", ct);
            Require(await File.ReadAllTextAsync(Path.Combine(capturedWorld, "SavedArks", "world.ark"), ct) == "world-before", "World view changed after capture.");
            Require(await File.ReadAllTextAsync(Path.Combine(capturedTransfers, "player.arkprofile"), ct) == "player-before", "Transfer view changed after capture.");
            var worldManifest = await archive.CreateAsync(capturedWorld, worldZip, serverId, ArkEdition.ASE, 1, ct,
                layout: SnapshotLayout.WorldSavedFolders, capture: first);
            var sharedManifest = await archive.CreateAsync(capturedTransfers, sharedZip, clusterId, ArkEdition.ASE, 1, ct,
                scope: SnapshotScope.SharedCluster, capture: first);
            Require(worldManifest.Capture == sharedManifest.Capture, "Archives do not share their capture identity.");
        }
        await RequireReleasedAsync([capturedWorld, capturedTransfers], ct);
        SnapshotCapture second;
        await using (var set = await broker.AcquireSetAsync([saved, transfers], ct))
        {
            second = set.Capture; Require(second.Id != first.Id, "The second capture reused the first identity.");
            await using var view = set.GetState(saved);
            await using var shared = set.GetState(transfers);
            capturedWorld = view.Directory; capturedTransfers = shared.Directory;
            Require(await File.ReadAllTextAsync(Path.Combine(capturedWorld, "SavedArks", "world.ark"), ct) == "world-after", "Second capture did not contain current world state.");
            Require(await File.ReadAllTextAsync(Path.Combine(capturedTransfers, "player.arkprofile"), ct) == "player-after", "Second capture did not contain current transfer state.");
        }
        await RequireReleasedAsync([capturedWorld, capturedTransfers], ct);
        var restoredWorld = Path.Combine(output, "restored-world"); var restoredShared = Path.Combine(output, "restored-shared");
        await archive.ExtractToNewDirectoryAsync(worldZip, restoredWorld, serverId, ArkEdition.ASE, ct, expectedRuntimeHour: 1);
        await archive.ExtractToNewDirectoryAsync(sharedZip, restoredShared, clusterId, ArkEdition.ASE, ct,
            expectedScope: SnapshotScope.SharedCluster, expectedRuntimeHour: 1);
        Require(await File.ReadAllTextAsync(Path.Combine(restoredWorld, "SavedArks", "world.ark"), ct) == "world-before", "Archived world did not restore correctly.");
        Require(await File.ReadAllTextAsync(Path.Combine(restoredShared, "player.arkprofile"), ct) == "player-before", "Archived transfer state did not restore correctly.");
        return new { Passed = true, capability.Elevated, FirstCapture = first, SecondCapture = second,
            ImmutableViews = true, ReleasesConfirmed = true, ArchivesRestored = true, LiveArkConsistency = "Not tested" };
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }

    private static async Task RequireReleasedAsync(string[] directories, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (directories.Any(Directory.Exists)) await Task.Delay(100, deadline.Token);
    }
}
