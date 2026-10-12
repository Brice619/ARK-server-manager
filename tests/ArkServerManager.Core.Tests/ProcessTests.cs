using System.Text.Json;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public class ProcessTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-process-test-" + Guid.NewGuid().ToString("N"));

    private ProcessLaunch Launch(params string[] arguments)
    {
        Directory.CreateDirectory(root);
        var fixture = Path.Combine(AppContext.BaseDirectory, "fixture", "ArkServerManager.ProcessFixture.dll");
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        if (!OperatingSystem.IsWindows() && !File.Exists(dotnet)) dotnet = "/usr/bin/dotnet";
        return new(dotnet, root, [fixture, .. arguments], Path.Combine(root, "console.log"), ["secret-value"]);
    }

    [Fact]
    public async Task ChildEnvironmentOverridesAreIsolatedAndOutputSecretsAreRedacted()
    {
        var variable = "ARK_FIXTURE_" + Guid.NewGuid().ToString("N");
        var launch = Launch("--environment", variable) with
        { EnvironmentOverrides = new Dictionary<string, string?> { [variable] = "secret-value" } };
        await using var child = ManagedProcess.Start(launch);
        Assert.Equal(0, (await child.Completion.WaitAsync(TimeSpan.FromSeconds(10))).ExitCode);
        Assert.Contains(child.ConsoleTail, line => line.Text == "environment=[redacted]");
        Assert.Null(Environment.GetEnvironmentVariable(variable));
        Assert.DoesNotContain("secret-value", await File.ReadAllTextAsync(launch.LogFile));
    }

    [Fact]
    public async Task ChildEnvironmentCanRemoveInheritedValueWithoutChangingParent()
    {
        var original = Environment.GetEnvironmentVariable("PATH");
        Assert.NotNull(original);
        await using var child = ManagedProcess.Start(Launch("--environment", "PATH") with
        { EnvironmentOverrides = new Dictionary<string, string?> { ["PATH"] = null } });
        Assert.Equal(0, (await child.Completion.WaitAsync(TimeSpan.FromSeconds(10))).ExitCode);
        Assert.Contains(child.ConsoleTail, line => line.Text == "environment=<missing>");
        Assert.Equal(original, Environment.GetEnvironmentVariable("PATH"));
    }

    [Theory]
    [InlineData("BAD=KEY", "value")]
    [InlineData("BAD\0KEY", "value")]
    [InlineData("KEY", "bad\0value")]
    public void InvalidEnvironmentIsRejectedBeforeCreatingConsoleLog(string key, string value)
    {
        var launch = Launch() with { EnvironmentOverrides = new Dictionary<string, string?> { [key] = value } };
        Assert.Throws<ArgumentException>(() => ManagedProcess.Start(launch));
        Assert.False(File.Exists(launch.LogFile));
    }

    [Fact]
    public async Task CapturesBothStreamsAndExitCodeWithoutShellInterpretation()
    {
        var arguments = new[] { "space in value", "quote\"inside", "$(literal)", "--fail", "secret-value" };
        await using var child = ManagedProcess.Start(Launch(arguments));
        var result = await child.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(7, result.ExitCode);
        Assert.False(result.RequestedStop);
        var output = child.ConsoleTail.First(l => !l.StandardError).Text;
        var actual = JsonSerializer.Deserialize<string[]>(output)!;
        Assert.Equal(arguments[..4], actual[..4]);
        Assert.Equal("[redacted]", actual[4]);
        Assert.Contains(child.ConsoleTail, l => l.StandardError && l.Text == "fixture error");
        Assert.DoesNotContain("secret-value", await File.ReadAllTextAsync(Path.Combine(root, "console.log")));
    }

    [Fact]
    public async Task GracefulStopIsDistinguishedFromCrash()
    {
        await using var child = ManagedProcess.Start(Launch("--wait"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!child.ConsoleTail.Any(l => l.Text == "ready")) await Task.Delay(10, timeout.Token);
        Assert.True(await child.RequestStopAsync(ct => child.SendStandardInputAsync("stop", ct), TimeSpan.FromSeconds(5), timeout.Token));
        Assert.True((await child.Completion).RequestedStop);
        Assert.Equal(0, (await child.Completion).ExitCode);
    }

    [Fact]
    public async Task ConsoleConfirmationRequiresFreshOutputAndTimeoutLeavesProcessRunning()
    {
        await using var child = ManagedProcess.Start(Launch("--wait"));
        await child.WaitForConsoleAsync(0, line => line.Text == "ready", TimeSpan.FromSeconds(10));
        await child.SendStandardInputAsync("save complete");
        await child.WaitForConsoleAsync(0, line => line.Text == "save complete", TimeSpan.FromSeconds(3));
        var checkpoint = child.ConsoleCheckpoint;
        await Assert.ThrowsAsync<TimeoutException>(() => child.WaitForConsoleAsync(checkpoint,
            line => line.Text == "save complete", TimeSpan.FromMilliseconds(100)));
        Assert.True(child.IsAlive);
        var confirmation = child.WaitForConsoleAsync(checkpoint, line => line.Text == "save complete", TimeSpan.FromSeconds(3));
        await child.SendStandardInputAsync("save complete");
        Assert.True((await confirmation).Sequence > checkpoint);
        Assert.True(await child.RequestStopAsync(ct => child.SendStandardInputAsync("stop", ct), TimeSpan.FromSeconds(3)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveAndExitSequenceNeedsNewConfirmationFromOwnedProcess(bool confirmNewSave)
    {
        await using var child = ManagedProcess.Start(Launch("--wait"));
        await child.WaitForConsoleAsync(0, line => line.Text == "ready", TimeSpan.FromSeconds(10));
        // A previous save must not authorize this shutdown.
        await child.SendStandardInputAsync("fixture save finished");
        await child.WaitForConsoleAsync(0, line => line.Text == "fixture save finished", TimeSpan.FromSeconds(3));
        var calls = new List<string>();
        var shutdown = new ArkGracefulShutdown(async (_, command, ct) =>
        {
            calls.Add(command);
            if (command == "SaveWorld" && confirmNewSave) await child.SendStandardInputAsync("fixture save finished", ct);
            if (command == "DoExit") await child.SendStandardInputAsync("stop", ct);
            return "command accepted";
        });
        var endpoint = new RconEndpoint(32330, "fixture", TimeSpan.FromSeconds(1), RconResponseMode.SinglePacket);
        Task Request(CancellationToken ct) => shutdown.RequestWithConsoleConfirmationAsync(child, endpoint,
            line => !line.StandardError && line.Text == "fixture save finished", TimeSpan.FromMilliseconds(500), ct);
        if (confirmNewSave)
        {
            Assert.True(await child.RequestStopAsync(Request, TimeSpan.FromSeconds(3)));
            Assert.Equal(new[] { "SaveWorld", "DoExit" }, calls);
            Assert.True((await child.Completion).RequestedStop);
        }
        else
        {
            await Assert.ThrowsAsync<TimeoutException>(() => child.RequestStopAsync(Request, TimeSpan.FromSeconds(3)));
            Assert.Equal(new[] { "SaveWorld" }, calls);
            Assert.True(child.IsAlive);
            Assert.True(await child.RequestStopAsync(ct => child.SendStandardInputAsync("stop", ct), TimeSpan.FromSeconds(3)));
        }
    }

    [Fact]
    public async Task SupervisorRecordsRuntimeCrashAndStopsAfterTwoFailedRestarts()
    {
        var attempts = 0;
        ManagedProcess? running = null;
        var incidents = new System.Collections.Concurrent.ConcurrentQueue<ServerIncident>();
        var serverId = Guid.NewGuid();
        var activity = new ActivityJournal(Path.Combine(root, "activity.jsonl"));
        await using var supervisor = new ServerSupervisor(
            () => Launch(Interlocked.Increment(ref attempts) == 1 ? "--wait" : "--fail"),
            async (process, ct) =>
            {
                if (attempts > 1) { await process.Completion.WaitAsync(ct); throw new IOException("Startup failed."); }
                while (!process.ConsoleTail.Any(l => l.Text == "ready")) await Task.Delay(10, ct);
                running = process;
            },
            (process, ct) => process.SendStandardInputAsync("stop", ct),
            incident => { incidents.Enqueue(incident); return activity.RecordIncidentAsync(serverId, incident); }, TimeSpan.FromSeconds(5));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await supervisor.StartAsync(timeout.Token);
        Assert.Equal(ServerState.Running, supervisor.State);
        await running!.SendStandardInputAsync("crash", timeout.Token);
        while (supervisor.State != ServerState.Failed) await Task.Delay(10, timeout.Token);
        Assert.Equal(3, attempts);
        Assert.Single(incidents, i => i.Stage == "runtime");
        Assert.Equal(2, incidents.Count(i => i.Stage == "recovery"));
        Assert.All(incidents, i => Assert.Equal(7, i.ExitCode));
        var persisted = await activity.ReadRecentAsync(100, serverId, timeout.Token);
        Assert.Equal(3, persisted.Entries.Count);
        Assert.Single(persisted.Entries, i => i.Stage == "runtime");
        Assert.Equal(2, persisted.Entries.Count(i => i.Stage == "recovery"));
        Assert.All(persisted.Entries, i => Assert.Equal(7, i.ExitCode));
    }

    [Fact]
    public async Task AutomaticRecoveryAppliesQueuedSettingsBeforeLaunchingReplacement()
    {
        var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
        var id = Guid.NewGuid();
        var settings = new ServerSettings { Name = "Original", GamePort = 7777, QueryPort = 27015, RconPort = 32330 };
        await repository.ChangeAsync(c => c.AddServer(new(id, ArkEdition.ASE, "TheIsland", root, settings)));
        var writes = new List<string>();
        var workflow = new SettingsChangeWorkflow(repository, Path.Combine(root, "pending.json"),
            (_, _) => throw new InvalidOperationException("No manual stop expected"),
            (_, _) => throw new InvalidOperationException("No manual start expected"),
            (_, selected, _) => { writes.Add(selected.Name); return Task.CompletedTask; });
        using var fileScope = new SemaphoreSlim(1, 1);
        ManagedProcess? running = null;
        await using var supervisor = new ServerSupervisor(async ct =>
            {
                await workflow.ApplyBeforeStartAsync(id, ct);
                var current = (await repository.ReadAsync(ct)).Servers.Single(s => s.Id == id);
                return Launch("--wait", current.Settings.Name);
            },
            async (process, ct) =>
            {
                while (!process.ConsoleTail.Any(l => l.Text == "ready")) await Task.Delay(10, ct);
                running = process;
            },
            (process, ct) => process.SendStandardInputAsync("stop", ct),
            _ => Task.CompletedTask, TimeSpan.FromSeconds(5), recoveryGate: fileScope);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await supervisor.StartAsync(timeout.Token);
        var original = running;
        var changed = new ServerSettings { Name = "Queued replacement", GamePort = 7777, QueryPort = 27015, RconPort = 32330 };
        await workflow.SaveAsync(id, changed, true, SaveChoice.SaveForNextStart, timeout.Token);
        Assert.Empty(writes);
        await original!.SendStandardInputAsync("crash", timeout.Token);
        while (running == original || supervisor.State != ServerState.Running) await Task.Delay(10, timeout.Token);
        Assert.Equal(new[] { "Queued replacement" }, writes);
        Assert.Null(await workflow.ReadPendingAsync(id, timeout.Token));
        Assert.Contains(supervisor.ConsoleTail, line => line.Text.Contains("Queued replacement"));
        Assert.True(await supervisor.StopAsync(timeout.Token));
    }

    [Fact]
    public async Task RecoveryWaitsForFileScopeWithoutBlockingStop()
    {
        using var fileScope = new SemaphoreSlim(1, 1);
        var attempts = 0;
        ManagedProcess? running = null;
        await using var supervisor = new ServerSupervisor(
            () => { Interlocked.Increment(ref attempts); return Launch("--wait"); },
            async (process, ct) =>
            {
                while (!process.ConsoleTail.Any(l => l.Text == "ready")) await Task.Delay(10, ct);
                running = process;
            },
            (process, ct) => process.SendStandardInputAsync("stop", ct),
            _ => Task.CompletedTask, TimeSpan.FromSeconds(5), recoveryGate: fileScope);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await supervisor.StartAsync(timeout.Token);
        await fileScope.WaitAsync(timeout.Token);
        try
        {
            await running!.SendStandardInputAsync("crash", timeout.Token);
            await running.Completion.WaitAsync(timeout.Token);
            // A user Stop already owns the file scope. Recovery must not hold the process lock while waiting.
            Assert.True(await supervisor.StopAsync(timeout.Token));
            Assert.Equal(1, attempts);
        }
        finally { fileScope.Release(); }
        Assert.Equal(ServerState.Stopped, supervisor.State);
    }

    [Fact]
    public async Task RecoveryPreparesFreshLaunchAndRetriesPreparationFailure()
    {
        var preparations = 0;
        ManagedProcess? running = null;
        var incidents = new System.Collections.Concurrent.ConcurrentQueue<ServerIncident>();
        await using var supervisor = new ServerSupervisor(
            async ct =>
            {
                await Task.Delay(10, ct);
                if (Interlocked.Increment(ref preparations) == 2)
                    throw new IOException("Updated launch settings could not be read.");
                return Launch("--wait");
            },
            async (process, ct) =>
            {
                while (!process.ConsoleTail.Any(l => l.Text == "ready")) await Task.Delay(10, ct);
                running = process;
            },
            (process, ct) => process.SendStandardInputAsync("stop", ct),
            incident => { incidents.Enqueue(incident); return Task.CompletedTask; }, TimeSpan.FromSeconds(5));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await supervisor.StartAsync(timeout.Token);
        var original = running;
        await original!.SendStandardInputAsync("crash", timeout.Token);
        while (running == original || supervisor.State != ServerState.Running) await Task.Delay(10, timeout.Token);
        Assert.Equal(3, preparations);
        Assert.Single(incidents, i => i.Stage == "recovery" && i.Message.Contains("Updated launch settings"));
        Assert.True(await supervisor.StopAsync(timeout.Token));
        Assert.Equal(3, preparations);
    }

    [Fact]
    public async Task SupervisorRecoversAndGracefulStopDoesNotRestartIt()
    {
        var attempts = 0;
        ManagedProcess? running = null;
        await using var supervisor = new ServerSupervisor(
            () => { Interlocked.Increment(ref attempts); return Launch("--wait"); },
            async (process, ct) =>
            {
                while (!process.ConsoleTail.Any(l => l.Text == "ready")) await Task.Delay(10, ct);
                running = process;
            },
            (process, ct) => process.SendStandardInputAsync("stop", ct),
            _ => throw new IOException("Incident disk full"), TimeSpan.FromSeconds(5));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await supervisor.StartAsync(timeout.Token);
        var original = running;
        await running!.SendStandardInputAsync("crash", timeout.Token);
        while (running == original || supervisor.State != ServerState.Running) await Task.Delay(10, timeout.Token);
        Assert.Equal(2, attempts);
        Assert.IsType<IOException>(supervisor.IncidentRecordingFailure);
        Assert.True(await supervisor.StopAsync(timeout.Token));
        Assert.Equal(ServerState.Stopped, supervisor.State);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ShutdownTimeoutReportsFailureWithoutKillingProcess()
    {
        await using var child = ManagedProcess.Start(Launch("--wait"));
        Assert.False(await child.RequestStopAsync(_ => Task.CompletedTask, TimeSpan.FromMilliseconds(100)));
        Assert.True(child.IsAlive);
        Assert.True(await child.RequestStopAsync(ct => child.SendStandardInputAsync("stop", ct), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ClusterCoordinatorRestartsActualChildrenWithFreshPendingSettings()
    {
        var repository = new CatalogRepository(Path.Combine(root, "catalog.json"));
        var cluster = Guid.NewGuid();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        ServerSettings Settings(string name, int offset) => new()
        { Name = name, GamePort = 7777 + offset, QueryPort = 27015 + offset, RconPort = 32330 + offset };
        await repository.ChangeAsync(c =>
        {
            c.AddCluster(new(cluster, "Cluster", ArkEdition.ASE, Settings("Cluster", 0)));
            c.AddServer(new(first, ArkEdition.ASE, "TheIsland", root, Settings("First", 0), cluster));
            c.AddServer(new(second, ArkEdition.ASE, "ScorchedEarth_P", root, Settings("Second", 10), cluster));
        });
        var runtimeMap = new Dictionary<Guid, SupervisedServerRuntime>();
        var workflow = new SettingsChangeWorkflow(repository, Path.Combine(root, "pending.json"),
            (_, _) => throw new InvalidOperationException("No immediate stop expected"),
            (_, _) => throw new InvalidOperationException("No immediate start expected"),
            (_, _, _) => Task.CompletedTask);
        var coordinator = new ServerLifecycleCoordinator(repository, id =>
        {
            var runtime = new SupervisedServerRuntime(id, definition => new ServerSupervisor(
                () => Launch("--wait", definition.Settings.Name) with { LogFile = Path.Combine(root, id + ".log") },
                async (process, ct) =>
                {
                    while (!process.ConsoleTail.Any(l => l.Text == "ready")) await Task.Delay(10, ct);
                },
                (process, ct) => process.SendStandardInputAsync("stop", ct),
                _ => Task.CompletedTask, TimeSpan.FromSeconds(5)));
            runtimeMap.Add(id, runtime);
            return runtime;
        }, workflow.ApplyBeforeStartAsync);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            Assert.True((await coordinator.ExecuteAsync(first, LifecycleAction.Start, timeout.Token)).Success);
            await workflow.SaveAsync(first, Settings("Changed", 0), true, SaveChoice.SaveForNextStart, timeout.Token);
            Assert.True((await coordinator.ExecuteAsync(second, LifecycleAction.Restart, timeout.Token)).Success);
            Assert.Contains(runtimeMap[first].ConsoleTail, line => line.Text.Contains("Changed"));
            Assert.Contains(runtimeMap[second].ConsoleTail, line => line.Text.Contains("Second"));
            Assert.All(await coordinator.StatusAsync(timeout.Token), member => Assert.Equal(ServerState.Running, member.State));
            Assert.True((await coordinator.ExecuteAsync(first, LifecycleAction.Stop, timeout.Token)).Success);
            Assert.All(await coordinator.StatusAsync(timeout.Token), member => Assert.Equal(ServerState.Stopped, member.State));
        }
        finally { foreach (var runtime in runtimeMap.Values) await runtime.DisposeAsync(); }
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
