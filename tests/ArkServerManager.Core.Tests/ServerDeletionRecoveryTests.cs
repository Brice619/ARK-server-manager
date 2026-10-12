using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ServerDeletionRecoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-delete-recovery-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(true, DeletionRecoveryAction.RestoreStagedFiles)]
    [InlineData(false, DeletionRecoveryAction.FinishFileRemoval)]
    public void CatalogCommitDeterminesRecoveryRatherThanStalePhase(bool registered, DeletionRecoveryAction expected)
    {
        var operation = Guid.NewGuid(); var id = Guid.NewGuid();
        var original = Path.Combine(root, "map");
        var staged = Path.Combine(root, ".ark-delete-" + operation.ToString("N"));
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(staged, "world.ark"), "recoverable state");
        var server = new ServerDefinition(id, ArkEdition.ASE, "TheIsland", original, new() { Name = "Map" });
        var record = new ServerDeletionRecord(1, operation, id, "Map", original, staged, null, [], ServerDeletionPhase.Staged);
        var assessment = ServerDeletionRecovery.Assess(new(1, [], registered ? [server] : []), record, root);
        Assert.Equal(expected, assessment.Action);
        Assert.Equal("recoverable state", File.ReadAllText(Path.Combine(staged, "world.ark")));
        Assert.False(Directory.Exists(original));
    }

    [Fact]
    public void ChangedRegistrationRequiresReview()
    {
        var operation = Guid.NewGuid(); var id = Guid.NewGuid();
        var original = Path.Combine(root, "map");
        var staged = Path.Combine(root, ".ark-delete-" + operation.ToString("N"));
        Directory.CreateDirectory(staged);
        var server = new ServerDefinition(id, ArkEdition.ASE, "TheIsland", original, new() { Name = "Renamed" });
        var record = new ServerDeletionRecord(1, operation, id, "Map", original, staged, null, [], ServerDeletionPhase.Staged);
        Assert.Equal(DeletionRecoveryAction.ReviewRequired, ServerDeletionRecovery.Assess(new(1, [], [server]), record, root).Action);
        Assert.True(Directory.Exists(staged));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecutesRecoveryUnderScopeAndCanBeRepeated(bool registered)
    {
        var operation = Guid.NewGuid(); var id = Guid.NewGuid();
        var original = Path.Combine(root, "map");
        var staged = Path.Combine(root, ".ark-delete-" + operation.ToString("N"));
        Directory.CreateDirectory(staged);
        await File.WriteAllTextAsync(Path.Combine(staged, "world.ark"), "recoverable state");
        var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
        if (registered)
            await repository.ChangeAsync(c => c.AddServer(new(id, ArkEdition.ASE, "TheIsland", original,
                new() { Name = "Map", GamePort = 7777, QueryPort = 27015, RconPort = 32330 })));
        var journal = Path.Combine(root, "app-settings", "deletions", operation.ToString("N") + ".json");
        await new AtomicJsonStore<ServerDeletionRecord>(journal).SaveAsync(new(1, operation, id, "Map", original,
            staged, null, [], ServerDeletionPhase.Staged));
        var authorizations = 0;
        Task<ServerDeletionRecord> Recover() => ServerDeletionRecovery.RecoverAsync(repository, root, operation,
            (_, change, ct) => { authorizations++; return change(ct); });
        var result = await Recover();
        Assert.Equal(registered ? ServerDeletionPhase.RolledBack : ServerDeletionPhase.FilesRemoved, result.Phase);
        Assert.False(Directory.Exists(staged));
        Assert.Equal(registered, Directory.Exists(original));
        if (registered) Assert.Equal("recoverable state", await File.ReadAllTextAsync(Path.Combine(original, "world.ark")));
        var repeated = await Recover();
        Assert.Equal(result.Phase, repeated.Phase);
        Assert.Equal(2, authorizations);
        Assert.Equal(registered ? 1 : 0, (await repository.ReadAsync()).Servers.Count);
    }

    [Fact]
    public async Task ChangedRegistrationRejectsBeforeRequestingStoppedScope()
    {
        var operation = Guid.NewGuid(); var id = Guid.NewGuid();
        var original = Path.Combine(root, "map");
        var staged = Path.Combine(root, ".ark-delete-" + operation.ToString("N"));
        Directory.CreateDirectory(staged);
        await File.WriteAllTextAsync(Path.Combine(staged, "world.ark"), "keep this state");
        var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
        await repository.ChangeAsync(c => c.AddServer(new(id, ArkEdition.ASE, "TheIsland", original,
            new() { Name = "Renamed", GamePort = 7777, QueryPort = 27015, RconPort = 32330 })));
        var journal = Path.Combine(root, "app-settings", "deletions", operation.ToString("N") + ".json");
        await new AtomicJsonStore<ServerDeletionRecord>(journal).SaveAsync(new(1, operation, id, "Map", original,
            staged, null, [], ServerDeletionPhase.Staged));
        var authorized = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ServerDeletionRecovery.RecoverAsync(repository, root, operation,
            (_, change, ct) => { authorized = true; return change(ct); }));
        Assert.False(authorized);
        Assert.Equal("keep this state", await File.ReadAllTextAsync(Path.Combine(staged, "world.ark")));
        Assert.False(Directory.Exists(original));
    }

    [Fact]
    public async Task ScanReportsPendingAndMalformedRecordsWithoutChangingFiles()
    {
        var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
        Assert.Empty(await ServerDeletionRecovery.ScanAsync(repository, root));
        Assert.False(Directory.Exists(root));
        var operation = Guid.NewGuid(); var id = Guid.NewGuid();
        var original = Path.Combine(root, "map");
        var staged = Path.Combine(root, ".ark-delete-" + operation.ToString("N"));
        Directory.CreateDirectory(staged);
        await File.WriteAllTextAsync(Path.Combine(staged, "world.ark"), "preserve");
        var directory = Path.Combine(root, "app-settings", "deletions");
        await new AtomicJsonStore<ServerDeletionRecord>(Path.Combine(directory, operation.ToString("N") + ".json"))
            .SaveAsync(new(1, operation, id, "Map", original, staged, null, [], ServerDeletionPhase.Staged));
        await File.WriteAllTextAsync(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json"), "invalid json");
        var notices = await ServerDeletionRecovery.ScanAsync(repository, root);
        Assert.Equal(2, notices.Count);
        Assert.Contains(notices, n => n.OperationId == operation && n.Action == DeletionRecoveryAction.FinishFileRemoval);
        Assert.Contains(notices, n => n.Action == DeletionRecoveryAction.ReviewRequired && n.ServerId is null);
        Assert.Equal("preserve", await File.ReadAllTextAsync(Path.Combine(staged, "world.ark")));
        Assert.Equal(2, Directory.GetFiles(directory).Length);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task RecoveryIntentCanResumeBeforeOrAfterFileMutation(bool rollback, bool mutationFinished)
    {
        var operation = Guid.NewGuid(); var id = Guid.NewGuid();
        var original = Path.Combine(root, "map");
        var staged = Path.Combine(root, ".ark-delete-" + operation.ToString("N"));
        var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
        if (rollback)
            await repository.ChangeAsync(c => c.AddServer(new(id, ArkEdition.ASE, "TheIsland", original,
                new() { Name = "Map", GamePort = 7777, QueryPort = 27015, RconPort = 32330 })));
        if (!mutationFinished || rollback)
        {
            var location = mutationFinished ? original : staged;
            Directory.CreateDirectory(location);
            await File.WriteAllTextAsync(Path.Combine(location, "world.ark"), "state");
        }
        var journal = Path.Combine(root, "app-settings", "deletions", operation.ToString("N") + ".json");
        await new AtomicJsonStore<ServerDeletionRecord>(journal).SaveAsync(new(1, operation, id, "Map", original,
            staged, null, [], rollback ? ServerDeletionPhase.RollingBack : ServerDeletionPhase.RemovingFiles));
        var result = await ServerDeletionRecovery.RecoverAsync(repository, root, operation, (_, change, ct) => change(ct));
        Assert.Equal(rollback ? ServerDeletionPhase.RolledBack : ServerDeletionPhase.FilesRemoved, result.Phase);
        Assert.False(Directory.Exists(staged));
        Assert.Equal(rollback, Directory.Exists(original));
        if (rollback) Assert.Equal("state", await File.ReadAllTextAsync(Path.Combine(original, "world.ark")));
        Assert.Empty(await ServerDeletionRecovery.ScanAsync(repository, root));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
