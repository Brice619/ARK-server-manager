using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public class UpdateTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-update-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task QueuedUpdateRefusesChangedInstallationUntilNewVersionCheck()
    {
        var id = Guid.NewGuid(); var updates = 0;
        var current = new ServerDefinition(id, ArkEdition.ASE, "TheIsland", Path.Combine(root, "original"), new() { Name = "Fixture" });
        var workflow = new ServerUpdateWorkflow(Path.Combine(root, "updates.json"),
            (_, _) => Task.FromResult(RunningUpdateChoice.UpdateWhenStopped), (_, _) => throw new InvalidOperationException(),
            (_, _, _) => { updates++; return Task.FromResult(true); }, (_, _) => Task.CompletedTask,
            (_, _) => Task.FromResult(current));
        await workflow.DetectedAsync(id, 1, 2, true, DateTimeOffset.UtcNow);
        current = current with { Directory = Path.Combine(root, "replacement") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.OnStoppedAsync(id));
        Assert.Equal(0, updates);
        await workflow.DetectedAsync(id, 1, 2, true, DateTimeOffset.UtcNow);
        Assert.True((await workflow.OnStoppedAsync(id)).Updated);
        Assert.Equal(1, updates);
    }

    [Fact]
    public async Task StopAndRestartCallbacksCanReenterWithoutUpdatingTwice()
    {
        var id = Guid.NewGuid(); var updates = 0; var starts = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        ServerUpdateWorkflow workflow = null!;
        workflow = new(Path.Combine(root, "updates.json"),
            (_, _) => Task.FromResult(RunningUpdateChoice.StopUpdateRestart),
            async (_, ct) => { Assert.True((await workflow.OnStoppedAsync(id, ct)).Updated); return true; },
            (_, _, _) => { updates++; return Task.FromResult(true); },
            async (_, ct) => { Assert.False((await workflow.OnStoppedAsync(id, ct)).Queued); starts++; });
        var result = await workflow.DetectedAsync(id, 1, 2, true, DateTimeOffset.UtcNow, timeout.Token);
        Assert.True(result.Restarted); Assert.Equal(1, updates); Assert.Equal(1, starts);
    }

    [Fact]
    public void ReadsPublicBuildFromNestedSteamMetadataAndInstalledManifest()
    {
        var output = "Connecting anonymously...\n\"376030\" { \"common\" { \"name\" \"ARK Server\" \"oslist\" \"windows,linux\" } \"depots\" { \"branches\" { \"public\" { \"buildid\" \"123\" } \"test\" { \"buildid\" \"999\" } } } }\nDisconnected";
        var build = SteamBuildMetadata.ReadAvailable(output, 376030);
        Assert.Equal(123ul, build.BuildId);
        Assert.Equal("windows,linux", build.Platforms);
        Assert.Equal(122ul, SteamBuildMetadata.ReadInstalled("\"AppState\" { \"appid\" \"376030\" \"buildid\" \"122\" }", 376030));
        Assert.Throws<InvalidDataException>(() => SteamBuildMetadata.ReadAvailable(output, 2430930));
        Assert.Throws<InvalidDataException>(() => SteamBuildMetadata.ReadInstalled("\"AppState\" { \"appid\" \"1\" \"buildid\" \"122\" }", 376030));
    }

    [Fact]
    public async Task DeclinedUpdatePersistsUntilStopWithoutRepeatedPrompts()
    {
        var id = Guid.NewGuid();
        var events = new List<string>();
        ServerUpdateWorkflow Workflow() => new(Path.Combine(root, "updates.json"),
            (_, _) => { events.Add("prompt"); return Task.FromResult(RunningUpdateChoice.UpdateWhenStopped); },
            (_, _) => throw new InvalidOperationException("Should not stop"),
            (_, _, _) => { events.Add("update"); return Task.FromResult(true); },
            (_, _) => throw new InvalidOperationException("Should not start"));
        Assert.True((await Workflow().DetectedAsync(id, 1, 2, true, DateTimeOffset.UtcNow)).Queued);
        Assert.True((await Workflow().DetectedAsync(id, 1, 2, true, DateTimeOffset.UtcNow)).Queued);
        Assert.Equal(new[] { "prompt" }, events);
        Assert.True((await Workflow().OnStoppedAsync(id)).Updated);
        Assert.Equal(new[] { "prompt", "update" }, events);
        Assert.False((await Workflow().OnStoppedAsync(id)).Updated);
    }

    [Fact]
    public async Task AlreadyInstalledBuildClearsStaleQueuedWork()
    {
        var id = Guid.NewGuid();
        var workflow = new ServerUpdateWorkflow(Path.Combine(root, "updates.json"),
            (_, _) => Task.FromResult(RunningUpdateChoice.UpdateWhenStopped), (_, _) => throw new InvalidOperationException(),
            (_, _, _) => throw new InvalidOperationException("No stale update should run"), (_, _) => throw new InvalidOperationException());
        await workflow.DetectedAsync(id, 1, 2, true, DateTimeOffset.UtcNow);
        await workflow.DetectedAsync(id, 2, 2, true, DateTimeOffset.UtcNow);
        Assert.False((await workflow.OnStoppedAsync(id)).Queued);
    }

    [Fact]
    public async Task RunningApprovalStopsUpdatesAndRestartsInOrderWhileStoppedUpdatesNeverPrompt()
    {
        var events = new List<string>();
        var workflow = new ServerUpdateWorkflow(Path.Combine(root, "updates.json"),
            (_, _) => { events.Add("prompt"); return Task.FromResult(RunningUpdateChoice.StopUpdateRestart); },
            (_, _) => { events.Add("stop"); return Task.FromResult(true); },
            (_, _, _) => { events.Add("update"); return Task.FromResult(true); },
            (_, _) => { events.Add("start"); return Task.CompletedTask; });
        var result = await workflow.DetectedAsync(Guid.NewGuid(), 1, 2, true, DateTimeOffset.UtcNow);
        Assert.True(result.Restarted);
        Assert.Equal(new[] { "prompt", "stop", "update", "start" }, events);
        events.Clear();
        await workflow.DetectedAsync(Guid.NewGuid(), 1, 2, false, DateTimeOffset.UtcNow);
        Assert.Equal(new[] { "update" }, events);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
