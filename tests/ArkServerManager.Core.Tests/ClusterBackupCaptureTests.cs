using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ClusterBackupCaptureTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-cluster-capture-" + Guid.NewGuid().ToString("N"));
    private sealed class Runtime : IServerRuntime
    {
        public ServerState State { get; private set; }
        public Task StartAsync(ServerDefinition _, CancellationToken ct) { State = ServerState.Running; return Task.CompletedTask; }
        public Task<bool> StopAsync(CancellationToken ct) { State = ServerState.Stopped; return Task.FromResult(true); }
    }
    private sealed class Set(IReadOnlyDictionary<string, string> sources, bool mismatch = false) : IStableBackupSet
    {
        public SnapshotCapture Capture { get; } = new(Guid.NewGuid(), DateTimeOffset.UtcNow);
        public bool Disposed;
        public IStableServerState GetState(string sourceDirectory)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            return new View(sourceDirectory, sources[sourceDirectory], mismatch ? new(Guid.NewGuid(), Capture.CapturedAt) : Capture);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        private sealed class View(string source, string directory, SnapshotCapture capture) : IStableServerState
        {
            public SnapshotCapture Capture => capture;
            public string SourceDirectory => source;
            public string Directory => directory;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    private async Task<(Guid Cluster, ServerDefinition First, ServerDefinition Second, ServerLifecycleCoordinator Coordinator)> SetupAsync()
    {
        var cluster = Guid.NewGuid();
        ServerSettings Settings(int offset) => new() { Name = "Map", GamePort = 7777 + offset, QueryPort = 27015 + offset, RconPort = 32330 + offset };
        var first = new ServerDefinition(Guid.NewGuid(), ArkEdition.ASE, "TheIsland", Path.Combine(root, "first"), Settings(0), cluster);
        var second = first with { Id = Guid.NewGuid(), Directory = Path.Combine(root, "second"), Map = "ScorchedEarth_P", Settings = Settings(10) };
        foreach (var server in new[] { first, second }) Directory.CreateDirectory(Path.Combine(server.Directory, "ShooterGame", "Saved"));
        var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
        await repository.ChangeAsync(c => { c.AddCluster(new(cluster, "Cluster", ArkEdition.ASE, Settings(0))); c.AddServer(first); c.AddServer(second); });
        return (cluster, first, second, new(repository, _ => new Runtime(), (_, _) => Task.CompletedTask));
    }
    private static Task<InstallationStopCheck> Stopped(ServerDefinition server, CancellationToken ct)
        => Task.FromResult(new InstallationStopCheck(server.Directory, InstallationStopState.Stopped, [], [], DateTimeOffset.UtcNow));

    [Fact]
    public async Task MissingMapRefusesBeforeCreatingSharedStorageOrSavingOtherMaps()
    {
        var (cluster, first, second, coordinator) = await SetupAsync();
        Directory.Delete(second.Directory, true);
        var saves = 0; var captures = 0;
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => coordinator.AcquireClusterBackupStateAsync(cluster, root,
            (_, _) => { saves++; return Task.CompletedTask; },
            (_, _) => { captures++; throw new InvalidOperationException("Must not capture"); }, Stopped));
        Assert.Equal(0, saves); Assert.Equal(0, captures);
        Assert.False(Directory.Exists(second.Directory));
        Assert.False(Directory.Exists(Path.Combine(root, "Clusters")));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Assert.True((await coordinator.ExecuteMemberAsync(first.Id, LifecycleAction.Stop, deadline.Token)).Success);
    }

    [Fact]
    public async Task MissingSavedStateRefusesWithoutRecreatingIt()
    {
        var (cluster, first, second, coordinator) = await SetupAsync();
        var saved = Path.Combine(second.Directory, "ShooterGame", "Saved");
        Directory.Delete(saved, true);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => coordinator.AcquireClusterBackupStateAsync(cluster, root,
            (_, _) => throw new InvalidOperationException("Must not save"),
            (_, _) => throw new InvalidOperationException("Must not capture"), Stopped));
        Assert.False(Directory.Exists(saved));
        Assert.False(Directory.Exists(Path.Combine(root, "Clusters")));
    }

    [Fact]
    public async Task SavesAllRunningMapsBeforeSingleCaptureAndProtectsMapsPlusTransfersUntilRelease()
    {
        var (cluster, first, second, coordinator) = await SetupAsync();
        await coordinator.ExecuteAsync(first.Id, LifecycleAction.Start);
        var saved = new HashSet<Guid>(); Set? captured = null;
        var lease = await coordinator.AcquireClusterBackupStateAsync(cluster, root,
            (server, _) => { saved.Add(server.Id); return Task.CompletedTask; },
            (sources, _) =>
            {
                Assert.Equal(2, saved.Count); Assert.Equal(3, sources.Count);
                captured = new(sources.ToDictionary(source => source, source => Path.Combine(root, "captured", sources.ToList().IndexOf(source).ToString())));
                return Task.FromResult<IStableBackupSet>(captured);
            }, Stopped);
        Assert.Equal(Path.Combine(first.Directory, "ShooterGame", "Saved"), lease.GetServerState(first.Id).SourceDirectory);
        Assert.Equal(Path.Combine(second.Directory, "ShooterGame", "Saved"), lease.GetServerState(second.Id).SourceDirectory);
        Assert.Equal(lease.SharedStorage.Transfers, lease.GetSharedState().SourceDirectory);
        Assert.NotEqual(lease.SharedStorage.Transfers, lease.GetSharedState().Directory);
        await lease.GetServerState(first.Id).DisposeAsync();
        Assert.False(captured!.Disposed);
        var stop = coordinator.ExecuteAsync(first.Id, LifecycleAction.Stop);
        Assert.False(stop.IsCompleted);
        await lease.DisposeAsync();
        Assert.True(captured.Disposed); Assert.True((await stop).Success);
        Assert.Throws<ObjectDisposedException>(() => lease.GetSharedState());
    }

    [Fact]
    public async Task StoppedSiblingIsVerifiedAndIncludedWithoutStartingIt()
    {
        var (cluster, first, second, coordinator) = await SetupAsync();
        await coordinator.ExecuteMemberAsync(first.Id, LifecycleAction.Start);
        var saves = new List<Guid>(); var checkedIds = new List<Guid>();
        await using var lease = await coordinator.AcquireClusterBackupStateAsync(cluster, root,
            (server, _) => { saves.Add(server.Id); return Task.CompletedTask; },
            (sources, _) => Task.FromResult<IStableBackupSet>(new Set(sources.ToDictionary(source => source, source => source))),
            (server, ct) => { checkedIds.Add(server.Id); return Stopped(server, ct); });
        Assert.Equal(first.Id, Assert.Single(saves)); Assert.Equal(second.Id, Assert.Single(checkedIds));
        Assert.Equal(second.Id, lease.Catalog.Servers.Single(s => s.Id == second.Id).Id);
    }

    [Fact]
    public async Task UnverifiedSiblingRefusesCaptureAndReleasesLifecycleGate()
    {
        var (cluster, first, second, coordinator) = await SetupAsync();
        await coordinator.ExecuteMemberAsync(first.Id, LifecycleAction.Start);
        var saved = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.AcquireClusterBackupStateAsync(cluster, root,
            (_, _) => { saved = true; return Task.CompletedTask; },
            (_, _) => throw new InvalidOperationException("Must not capture"),
            (server, _) => Task.FromResult(new InstallationStopCheck(server.Directory, InstallationStopState.Unverified, [], [], DateTimeOffset.UtcNow))));
        Assert.False(saved);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Assert.True((await coordinator.ExecuteMemberAsync(first.Id, LifecycleAction.Stop, deadline.Token)).Success);
    }
    [Fact]
    public async Task MemberAndSharedArchivesCarryOneCaptureIdentityAndExcludeConfiguration()
    {
        var (cluster, first, second, coordinator) = await SetupAsync();
        foreach (var server in new[] { first, second })
        {
            var saved = Path.Combine(server.Directory, "ShooterGame", "Saved");
            Directory.CreateDirectory(Path.Combine(saved, "SavedArks"));
            Directory.CreateDirectory(Path.Combine(saved, "Config"));
            await File.WriteAllTextAsync(Path.Combine(saved, "SavedArks", "world.ark"), server.Map);
            await File.WriteAllTextAsync(Path.Combine(saved, "Config", "Game.ini"), "settings stay separate");
        }
        var transfers = Path.Combine(root, "Clusters", cluster.ToString("N"), "Transfers");
        Directory.CreateDirectory(transfers);
        await File.WriteAllTextAsync(Path.Combine(transfers, "survivor"), "shared fixture");
        await using var lease = await coordinator.AcquireClusterBackupStateAsync(cluster, root,
            (_, _) => throw new InvalidOperationException("All maps are stopped"),
            (_, _) => throw new InvalidOperationException("Stopped capture does not use VSS"), Stopped);
        var archive = new SnapshotArchive();
        var manifests = new List<SnapshotManifest>();
        foreach (var server in new[] { first, second })
        {
            await using var view = lease.GetServerState(server.Id);
            var target = Path.Combine(server.Directory, "backups", "hour-00000001.zip");
            await archive.CreateAsync(view.Directory, target, server.Id, server.Edition, 1,
                layout: SnapshotLayout.WorldSavedFolders, capture: view.Capture);
            var verified = await archive.VerifyAsync(target);
            Assert.Equal("SavedArks/world.ark", Assert.Single(verified.Files).Path);
            manifests.Add(verified);
        }
        var shared = await new SharedClusterSnapshotService().CreateAsync(lease.Catalog, cluster, root, 1,
            (_, _) => Task.FromResult(lease.GetSharedState()));
        manifests.Add(await archive.VerifyAsync(Path.Combine(lease.SharedStorage.Backups, "hour-00000001.zip")));
        Assert.Equal(SnapshotScope.SharedCluster, shared.Scope);
        Assert.Equal("survivor", Assert.Single(shared.Files).Path);
        Assert.All(manifests, manifest => Assert.Equal(lease.Capture, manifest.Capture));
    }

    [Fact]
    public async Task RejectsViewsFromDifferentCapturesAndReleasesTheWholeSet()
    {
        var (cluster, first, second, coordinator) = await SetupAsync();
        await coordinator.ExecuteAsync(first.Id, LifecycleAction.Start);
        Set? bad = null;
        await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.AcquireClusterBackupStateAsync(cluster, root,
            (_, _) => Task.CompletedTask, (sources, _) =>
            {
                bad = new(sources.ToDictionary(source => source, source => source), mismatch: true);
                return Task.FromResult<IStableBackupSet>(bad);
            }, Stopped));
        Assert.True(bad!.Disposed);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Assert.True((await coordinator.ExecuteAsync(first.Id, LifecycleAction.Stop, deadline.Token)).Success);
    }

    [Fact]
    public async Task ClusterWorkKeepsLifecycleBlockedAfterCaptureReleaseUntilCheckpointFinishes()
    {
        var (cluster, first, second, coordinator) = await SetupAsync();
        await coordinator.ExecuteAsync(first.Id, LifecycleAction.Start);
        var checkpoint = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Set? captured = null;
        var work = coordinator.WithClusterBackupWorkAsync(cluster, root, (_, _) => Task.CompletedTask,
            (sources, _) => Task.FromResult<IStableBackupSet>(captured = new(sources.ToDictionary(s => s, s => s))), Stopped,
            async (_, acquire, ct) =>
            {
                await using (var lease = await acquire(ct)) Assert.Equal(cluster, lease.ClusterId);
                Assert.True(captured!.Disposed);
                checkpoint.SetResult(); await finish.Task.WaitAsync(ct);
            });
        await checkpoint.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var stop = coordinator.ExecuteMemberAsync(second.Id, LifecycleAction.Stop);
        Assert.False(stop.IsCompleted);
        finish.SetResult(); await work;
        Assert.True((await stop.WaitAsync(TimeSpan.FromSeconds(3))).Success);
    }

    [Fact]
    public async Task FailedClusterWorkDisposesForgottenCaptureAndRejectsExpiredCallback()
    {
        var (cluster, first, second, coordinator) = await SetupAsync();
        await coordinator.ExecuteAsync(first.Id, LifecycleAction.Start);
        Set? captured = null;
        Func<CancellationToken, Task<CoordinatedClusterBackupState>>? expired = null;
        await Assert.ThrowsAsync<IOException>(() => coordinator.WithClusterBackupWorkAsync(cluster, root,
            (_, _) => Task.CompletedTask,
            (sources, _) => Task.FromResult<IStableBackupSet>(captured = new(sources.ToDictionary(s => s, s => s))), Stopped,
            async (document, acquire, ct) => { expired = acquire; _ = await acquire(ct); throw new IOException("Checkpoint failure"); }));
        Assert.True(captured!.Disposed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => expired!(CancellationToken.None));
        Assert.True((await coordinator.ExecuteMemberAsync(second.Id, LifecycleAction.Stop)
            .WaitAsync(TimeSpan.FromSeconds(3))).Success);
    }

    [Theory]
    [InlineData("published")]
    [InlineData("complete-journal")]
    [InlineData("partial-journal")]
    public async Task CoordinatedPublicationRecoversWithoutMixingCaptureIdentities(string mode)
    {
        var (cluster, first, second, coordinator) = await SetupAsync();
        foreach (var server in new[] { first, second })
        {
            var worlds = Path.Combine(server.Directory, "ShooterGame", "Saved", "SavedArks");
            Directory.CreateDirectory(worlds);
            await File.WriteAllTextAsync(Path.Combine(worlds, "world.ark"), "original " + server.Id);
        }
        CoordinatedRestorePoint? original = null;
        var publisher = new CoordinatedArchivePublisher();
        await coordinator.WithClusterBackupWorkAsync(cluster, root, (_, _) => throw new InvalidOperationException("Stopped"),
            (_, _) => throw new InvalidOperationException("Stopped"), Stopped,
            async (catalog, acquire, ct) => { original = await publisher.PublishAsync(catalog, cluster, root, 1, acquire, ct); });
        Assert.NotNull(original);
        var storage = ManagedClusterStorage.Resolve(await new CatalogRepository(Path.Combine(root, "catalog.json")).ReadAsync(), cluster, root);
        var index = Path.Combine(storage.Backups, "Coordinated", "hour-00000001.json");
        var oldName = $"hour-00000001-{original.Capture.Id:N}.zip";
        if (mode != "published") File.Move(index, index + ".pending");
        if (mode == "partial-journal") File.Delete(Path.Combine(second.Directory, "backups", "Coordinated", oldName));
        foreach (var server in new[] { first, second })
            await File.WriteAllTextAsync(Path.Combine(server.Directory, "ShooterGame", "Saved", "SavedArks", "world.ark"), "newer state");
        var acquisitions = 0; CoordinatedRestorePoint? recovered = null;
        await coordinator.WithClusterBackupWorkAsync(cluster, root, (_, _) => throw new InvalidOperationException("Stopped"),
            (_, _) => throw new InvalidOperationException("Stopped"), Stopped, async (catalog, acquire, ct) =>
            {
                recovered = await publisher.PublishAsync(catalog, cluster, root, 1,
                    token => { acquisitions++; return acquire(token); }, ct);
            });
        Assert.NotNull(recovered); Assert.True(File.Exists(index)); Assert.False(File.Exists(index + ".pending"));
        Assert.Equal(mode == "partial-journal" ? 1 : 0, acquisitions);
        if (mode == "partial-journal")
        {
            Assert.NotEqual(original.Capture, recovered.Capture);
            Assert.False(File.Exists(Path.Combine(first.Directory, "backups", "Coordinated", oldName)));
            Assert.False(File.Exists(Path.Combine(storage.Backups, "Coordinated", oldName)));
        }
        else Assert.Equal(original.Capture, recovered.Capture);
        foreach (var folder in new[] { Path.Combine(first.Directory, "backups", "Coordinated"),
            Path.Combine(second.Directory, "backups", "Coordinated"), Path.Combine(storage.Backups, "Coordinated") })
        {
            var manifest = await new SnapshotArchive().VerifyAsync(Assert.Single(Directory.GetFiles(folder, "*.zip")));
            Assert.Equal(recovered.Capture, manifest.Capture);
        }
        var unrelated = Path.Combine(first.Directory, "backups", "Coordinated", "untracked.zip");
        await File.WriteAllTextAsync(unrelated, "unrelated bytes");
        var current = await new CatalogRepository(Path.Combine(root, "catalog.json")).ReadAsync();
        await CoordinatedArchiveRetention.ExpireAsync(current, cluster, root, 1);
        await CoordinatedArchiveRetention.ExpireAsync(current, cluster, root, 1);
        Assert.False(File.Exists(index)); Assert.True(File.Exists(unrelated));
        Assert.Equal("unrelated bytes", await File.ReadAllTextAsync(unrelated));
        Assert.Empty(Directory.GetFiles(Path.Combine(second.Directory, "backups", "Coordinated"), "*.zip"));
        Assert.Empty(Directory.GetFiles(Path.Combine(storage.Backups, "Coordinated"), "*.zip"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MembershipChangesReconcilePendingSetsAndPreservePublishedPoints(bool published, bool adding)
    {
        var (cluster, first, second, coordinator) = await SetupAsync();
        foreach (var server in new[] { first, second })
        {
            var worlds = Path.Combine(server.Directory, "ShooterGame", "Saved", "SavedArks");
            Directory.CreateDirectory(worlds);
            await File.WriteAllTextAsync(Path.Combine(worlds, "world.ark"), "original world");
        }
        var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
        var publisher = new CoordinatedArchivePublisher();
        CoordinatedRestorePoint? original = null;
        await coordinator.WithClusterBackupWorkAsync(cluster, root, (_, _) => throw new InvalidOperationException("Stopped"),
            (_, _) => throw new InvalidOperationException("Stopped"), Stopped,
            async (catalog, acquire, ct) => original = await publisher.PublishAsync(catalog, cluster, root, 1, acquire, ct));
        var storage = ManagedClusterStorage.Resolve(await repository.ReadAsync(), cluster, root);
        var index = Path.Combine(storage.Backups, "Coordinated", "hour-00000001.json");
        if (!published) File.Move(index, index + ".pending");
        if (adding)
        {
            var third = first with { Id = Guid.NewGuid(), Directory = Path.Combine(root, "third"),
                Settings = first.Settings with { GamePort = 7797, QueryPort = 27035, RconPort = 32350 } };
            var worlds = Path.Combine(third.Directory, "ShooterGame", "Saved", "SavedArks");
            Directory.CreateDirectory(worlds);
            await File.WriteAllTextAsync(Path.Combine(worlds, "world.ark"), "new member world");
            await repository.ChangeAsync(c => c.AddServer(third));
        }
        else
        {
            await repository.ChangeAsync(c => c.RemoveServer(second.Id));
            Directory.Delete(second.Directory, true);
        }
        var acquisitions = 0; CoordinatedRestorePoint? result = null;
        await coordinator.WithClusterBackupWorkAsync(cluster, root, (_, _) => throw new InvalidOperationException("Stopped"),
            (_, _) => throw new InvalidOperationException("Stopped"), Stopped, async (catalog, acquire, ct) =>
            {
                result = await publisher.PublishAsync(catalog, cluster, root, 1,
                    token => { acquisitions++; return acquire(token); }, ct);
            });
        Assert.NotNull(original); Assert.NotNull(result);
        Assert.False(File.Exists(index + ".pending")); Assert.True(File.Exists(index));
        Assert.Equal(published ? 0 : 1, acquisitions);
        if (published) { Assert.Equal(original.Capture, result.Capture); Assert.Equal(original.Maps, result.Maps); }
        else
        {
            Assert.NotEqual(original.Capture, result.Capture);
            Assert.Equal((await repository.ReadAsync()).Servers.Select(s => s.Id).OrderBy(id => id), result.Maps);
            var oldName = $"hour-00000001-{original.Capture.Id:N}.zip";
            Assert.False(File.Exists(Path.Combine(first.Directory, "backups", "Coordinated", oldName)));
            Assert.False(File.Exists(Path.Combine(storage.Backups, "Coordinated", oldName)));
        }
        var manifest = await new SnapshotArchive().VerifyAsync(Path.Combine(storage.Backups, "Coordinated",
            $"hour-00000001-{result.Capture.Id:N}.zip"));
        Assert.Equal(result.Capture, manifest.Capture);
    }

    [Fact]
    public async Task SharedStateChangesRequireEveryMemberStoppedAndHoldTheLifecycleGate()
    {
        var (cluster, first, second, coordinator) = await SetupAsync();
        await coordinator.ExecuteMemberAsync(second.Id, LifecycleAction.Start);
        var writes = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.WithStoppedClusterAsync(cluster, root,
            (_, _) => { writes++; return Task.CompletedTask; }, Stopped));
        Assert.Equal(0, writes);
        await coordinator.ExecuteMemberAsync(second.Id, LifecycleAction.Stop);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = coordinator.WithStoppedClusterAsync(cluster, root,
            async (catalog, ct) => { Assert.Equal(2, catalog.Servers.Count); entered.SetResult(); await finish.Task.WaitAsync(ct); writes++; }, Stopped);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var start = coordinator.ExecuteMemberAsync(first.Id, LifecycleAction.Start);
        Assert.False(start.IsCompleted);
        finish.SetResult(); await operation;
        Assert.Equal(1, writes); Assert.True((await start.WaitAsync(TimeSpan.FromSeconds(3))).Success);
    }

    [Fact]
    public async Task UnverifiedStoppedClusterMemberPreventsSharedStateChanges()
    {
        var (cluster, first, second, coordinator) = await SetupAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.WithStoppedClusterAsync(cluster, root,
            (_, _) => throw new InvalidOperationException("Must not modify shared state"),
            (server, _) => Task.FromResult(new InstallationStopCheck(server.Directory, InstallationStopState.Unverified, [], [], DateTimeOffset.UtcNow))));
        Assert.True((await coordinator.ExecuteMemberAsync(first.Id, LifecycleAction.Start).WaitAsync(TimeSpan.FromSeconds(3))).Success);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("published")]
    [InlineData("retention")]
    [InlineData("schedule")]
    public async Task OversizedRecoveryMetadataRefusesBeforeCaptureOrMutation(string mode)
    {
        var (cluster, first, _, coordinator) = await SetupAsync();
        var catalog = await new CatalogRepository(Path.Combine(root, "catalog.json")).ReadAsync();
        var storage = ManagedClusterStorage.Prepare(catalog, cluster, root);
        var folder = Path.Combine(storage.Backups, "Coordinated"); Directory.CreateDirectory(folder);
        var record = Path.Combine(folder, mode == "schedule" ? "schedule.json" : "hour-00000001.json" + (mode == "pending" ? ".pending" : ""));
        var bytes = new string(' ', 1024 * 1024 + 1); await File.WriteAllTextAsync(record, bytes);
        var unrelated = Path.Combine(folder, "untracked.zip"); await File.WriteAllTextAsync(unrelated, "preserve");
        var acquisitions = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.WithClusterBackupWorkAsync(cluster, root,
            (_, _) => throw new InvalidOperationException("Stopped"), (_, _) => throw new InvalidOperationException("Stopped"), Stopped,
            async (current, acquire, ct) =>
            {
                if (mode == "retention") await CoordinatedArchiveRetention.ExpireAsync(current, cluster, root, 1, ct);
                else if (mode == "schedule") await new CoordinatedBackupService(current, cluster, root,
                    (_, _) => { acquisitions++; throw new InvalidOperationException("Must not publish"); }, _ => Task.CompletedTask)
                    .TickAsync(new(Guid.NewGuid(), TimeSpan.FromHours(1).Ticks), DateTimeOffset.UtcNow, ct);
                else await new CoordinatedArchivePublisher().PublishAsync(current, cluster, root, 1,
                    token => { acquisitions++; return acquire(token); }, ct);
            }));
        Assert.Equal(0, acquisitions); Assert.Equal(bytes, await File.ReadAllTextAsync(record));
        Assert.Equal("preserve", await File.ReadAllTextAsync(unrelated));
        Assert.True((await coordinator.ExecuteMemberAsync(first.Id, LifecycleAction.Start)).Success);
    }

    [Fact]
    public async Task MalformedPendingPublicationKeepsCheckpointAndReportsRetryWithoutCapture()
    {
        var (cluster, _, _, coordinator) = await SetupAsync();
        var catalog = await new CatalogRepository(Path.Combine(root, "catalog.json")).ReadAsync();
        var storage = ManagedClusterStorage.Prepare(catalog, cluster, root);
        var folder = Path.Combine(storage.Backups, "Coordinated"); Directory.CreateDirectory(folder);
        var pending = Path.Combine(folder, "hour-00000001.json.pending");
        await File.WriteAllTextAsync(pending, "{interrupted record");
        var acquisitions = 0; var notices = new List<string>();
        var session = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        CoordinatedBackupSchedule? state = null;
        async Task Tick(DateTimeOffset time) => await coordinator.WithClusterBackupWorkAsync(cluster, root,
            (_, _) => throw new InvalidOperationException("Stopped"), (_, _) => throw new InvalidOperationException("Stopped"), Stopped,
            async (current, acquire, ct) =>
            {
                state = await new CoordinatedBackupService(current, cluster, root,
                    (hour, token) => new CoordinatedArchivePublisher().PublishAsync(current, cluster, root, hour,
                        captureToken => { acquisitions++; return acquire(captureToken); }, token),
                    message => { notices.Add(message); return Task.CompletedTask; })
                    .TickAsync(new(session, TimeSpan.FromHours(1).Ticks), time, ct);
            });
        await Tick(now);
        Assert.NotNull(state); Assert.Equal(new long[] { 1 }, state.PendingHours);
        Assert.Equal(now.AddMinutes(1), state.RetryAt); Assert.NotNull(state.LastError);
        Assert.Equal(TimeSpan.FromHours(1).Ticks, state.RuntimeTicks); Assert.Empty(state.History);
        await Tick(now.AddSeconds(30));
        Assert.Single(notices); Assert.Equal(0, acquisitions);
        Assert.Equal("{interrupted record", await File.ReadAllTextAsync(pending));
        Assert.False(File.Exists(Path.Combine(folder, "hour-00000001.json")));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("missing-map")]
    [InlineData("wrong-shared-capture")]
    [InlineData("changed-members")]
    public async Task WholeClusterPreparationRequiresEveryArchiveAndMatchingMembership(string mode)
    {
        var (cluster, first, second, coordinator) = await SetupAsync();
        var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
        foreach (var member in new[] { first, second })
        {
            var world = Path.Combine(member.Directory, "ShooterGame", "Saved", "SavedArks"); Directory.CreateDirectory(world);
            await File.WriteAllTextAsync(Path.Combine(world, "world.ark"), "current world");
        }
        CoordinatedRestorePoint? point = null;
        await coordinator.WithClusterBackupWorkAsync(cluster, root, (_, _) => throw new InvalidOperationException("Stopped"),
            (_, _) => throw new InvalidOperationException("Stopped"), Stopped,
            async (catalog, acquire, ct) => point = await new CoordinatedArchivePublisher().PublishAsync(catalog, cluster, root, 1, acquire, ct));
        Assert.NotNull(point);
        var catalog = await repository.ReadAsync(); var storage = ManagedClusterStorage.Resolve(catalog, cluster, root);
        var folder = Path.Combine(storage.Backups, "Coordinated");
        await new AtomicJsonStore<CoordinatedBackupSchedule>(Path.Combine(folder, "schedule.json")).SaveAsync(
            new(1, cluster, ArkEdition.ASE, TimeSpan.FromHours(1).Ticks, null, [], null, null, [point]));
        var name = $"hour-00000001-{point.Capture.Id:N}.zip";
        if (mode == "missing-map") File.Delete(Path.Combine(second.Directory, "backups", "Coordinated", name));
        if (mode == "changed-members") await repository.ChangeAsync(c => c.RemoveServer(second.Id));
        if (mode == "wrong-shared-capture")
        {
            File.Delete(Path.Combine(folder, name));
            await new SnapshotArchive().CreateAsync(storage.Transfers, Path.Combine(folder, name), cluster, ArkEdition.ASE, 1,
                scope: SnapshotScope.SharedCluster, capture: new(Guid.NewGuid(), point.Capture.CapturedAt));
        }
        PreparedClusterRestore? prepared = null;
        async Task Prepare() => await coordinator.WithStoppedClusterAsync(cluster, root,
            async (current, ct) => prepared = await CoordinatedClusterRestoreSelection.PrepareAsync(current, root,
                new(cluster, 1, point.Capture.Id, true), ct), Stopped);
        if (mode == "valid")
        {
            await Prepare(); Assert.NotNull(prepared); Assert.Equal(2, prepared.Maps.Count); Assert.Equal(point.Capture, prepared.Shared!.Point.Capture);
            var operation = Guid.NewGuid(); ClusterRestoreRecord? staged = null;
            await coordinator.WithStoppedClusterAsync(cluster, root, async (current, ct) =>
                staged = await ClusterRestoreStaging.StageAsync(current, root, new(cluster, 1, point.Capture.Id, true), operation, ct), Stopped);
            Assert.NotNull(staged); Assert.Equal(ClusterRestorePhase.Staged, staged.Phase);
            await coordinator.WithStoppedClusterAsync(cluster, root,
                (current, ct) => ClusterRestoreStaging.ValidateStagedAsync(current, cluster, root, operation, ct), Stopped);
            foreach (var member in new[] { first, second })
                Assert.Equal("current world", await File.ReadAllTextAsync(Path.Combine(member.Directory,
                    ".cluster-restore-stage-" + operation.ToString("N"), "SavedArks", "world.ark")));
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.WithStoppedClusterAsync(cluster, root,
                (current, ct) => ClusterRestoreStaging.StageAsync(current, root, new(cluster, 1, point.Capture.Id, true), operation, ct), Stopped));
            await File.WriteAllTextAsync(Path.Combine(first.Directory, ".cluster-restore-stage-" + operation.ToString("N"),
                "SavedArks", "world.ark"), "changed staged world");
            await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.WithStoppedClusterAsync(cluster, root,
                (current, ct) => ClusterRestoreStaging.ValidateStagedAsync(current, cluster, root, operation, ct), Stopped));
            await coordinator.WithStoppedClusterAsync(cluster, root,
                (current, ct) => ClusterRestoreStaging.AbandonAsync(current, cluster, root, operation, ct), Stopped);
            await coordinator.WithStoppedClusterAsync(cluster, root,
                (current, ct) => ClusterRestoreStaging.AbandonAsync(current, cluster, root, operation, ct), Stopped);
            foreach (var member in new[] { first, second })
                Assert.False(Directory.Exists(Path.Combine(member.Directory, ".cluster-restore-stage-" + operation.ToString("N"))));
            Assert.False(Directory.Exists(Path.Combine(storage.Directory, ".cluster-restore-stage-" + operation.ToString("N"))));
            Assert.Equal(ClusterRestorePhase.RolledBack, Assert.Single(await ClusterRestoreRecords.ReadAsync(await repository.ReadAsync(), cluster, root)).Phase);
            var replacement = Guid.NewGuid();
            foreach (var member in new[] { first, second })
            {
                await File.WriteAllTextAsync(Path.Combine(member.Directory, "ShooterGame", "Saved", "SavedArks", "world.ark"), "newer world");
                var config = Path.Combine(member.Directory, "ShooterGame", "Saved", "Config"); Directory.CreateDirectory(config);
                await File.WriteAllTextAsync(Path.Combine(config, "fixture.ini"), "current settings");
            }
            await File.WriteAllTextAsync(Path.Combine(storage.Transfers, "player"), "newer shared");
            await coordinator.WithStoppedClusterAsync(cluster, root, async (current, ct) =>
            {
                await ClusterRestoreStaging.StageAsync(current, root, new(cluster, 1, point.Capture.Id, true), replacement, ct);
                var replaced = await ClusterRestoreReplacement.ReplaceAsync(current, cluster, root, replacement, ct);
                Assert.Equal(ClusterRestorePhase.Restored, replaced.Phase); Assert.Equal(5, replaced.OriginalFolders!.Count);
            }, Stopped);
            foreach (var member in new[] { first, second })
            {
                Assert.Equal("current settings", await File.ReadAllTextAsync(Path.Combine(member.Directory, "ShooterGame", "Saved", "Config", "fixture.ini")));
                Assert.Equal("newer world", await File.ReadAllTextAsync(Path.Combine(member.Directory,
                    ".cluster-restore-previous-" + replacement.ToString("N"), "SavedArks", "world.ark")));
            }
            Assert.Equal("newer shared", await File.ReadAllTextAsync(Path.Combine(storage.Directory,
                ".cluster-restore-previous-" + replacement.ToString("N"), "Transfers", "player")));
            Assert.False(File.Exists(Path.Combine(storage.Transfers, "player")));
            var currentCatalog = await repository.ReadAsync();
            var readiness = new ClusterRunningTimeTracker();
            readiness.Observe(first.Id, cluster, ServerState.Running);
            await ClusterRestoreCleanup.AfterVerifiedStartAsync(currentCatalog, cluster, root, readiness.AllRunning);
            Assert.True(Directory.Exists(Path.Combine(first.Directory, ".cluster-restore-previous-" + replacement.ToString("N"))));
            readiness.Observe(second.Id, cluster, ServerState.Failed);
            await ClusterRestoreCleanup.AfterVerifiedStartAsync(currentCatalog, cluster, root, readiness.AllRunning);
            Assert.True(Directory.Exists(Path.Combine(storage.Directory, ".cluster-restore-previous-" + replacement.ToString("N"))));
            readiness.Observe(second.Id, cluster, ServerState.Running);
            await ClusterRestoreCleanup.AfterVerifiedStartAsync(currentCatalog, cluster, root, readiness.AllRunning);
            Assert.False(Directory.Exists(Path.Combine(first.Directory, ".cluster-restore-previous-" + replacement.ToString("N"))));
            Assert.False(Directory.Exists(Path.Combine(storage.Directory, ".cluster-restore-previous-" + replacement.ToString("N"))));
            Assert.Equal(ClusterRestorePhase.Cleaned, (await ClusterRestoreRecords.ReadAsync(currentCatalog, cluster, root)).Single(r => r.OperationId == replacement).Phase);
        }
        else if (mode == "changed-members") await Assert.ThrowsAsync<InvalidOperationException>(Prepare);
        else if (mode == "wrong-shared-capture") await Assert.ThrowsAsync<InvalidDataException>(Prepare);
        else await Assert.ThrowsAnyAsync<IOException>(Prepare);
        foreach (var member in new[] { first, second })
            Assert.Equal("current world", await File.ReadAllTextAsync(Path.Combine(member.Directory, "ShooterGame", "Saved", "SavedArks", "world.ark")));
        Assert.False(Directory.Exists(Path.Combine(storage.Backups, "shared-restore-transactions")));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
