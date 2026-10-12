using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Compression;
using System.Diagnostics;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class PayloadInstallerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-payload-" + Guid.NewGuid().ToString("N"));

    private async Task<string> FixtureAsync()
    {
        var source = Path.Combine(root, "payload");
        var files = new List<PayloadFile>();
        foreach (var name in new[] { "ArkServerManager.Desktop.exe", "ArkServerManager.Desktop.runtimeconfig.json",
            "System.Private.CoreLib.dll", "manager/ArkServerManager.Host.exe", "manager/ArkServerManager.Host.dll",
            "manager/ArkServerManager.Host.runtimeconfig.json", "manager/System.Private.CoreLib.dll" })
        {
            var path = Path.Combine(source, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = System.Text.Encoding.UTF8.GetBytes("fixture " + name);
            await File.WriteAllBytesAsync(path, bytes);
            files.Add(new(name, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
        }
        await File.WriteAllTextAsync(Path.Combine(source, "payload-manifest.json"),
            JsonSerializer.Serialize(new InstallerPayload(1, "win-x64", true, files)));
        return source;
    }

    [Fact]
    public async Task PublishesCompletePayloadAndChosenRootWithoutOverwriting()
    {
        var source = await FixtureAsync();
        var destination = Path.Combine(root, "installed");
        var result = await VerifiedPayloadInstaller.InstallAsync(source, destination, "win-x64");
        Assert.Equal(7, result.Files);
        Assert.Equal(destination, InstalledAppSettingsFile.Read(destination)!.ServerRoot);
        var desktop = Path.Combine(destination, "ArkServerManager.Desktop.exe");
        var original = await File.ReadAllBytesAsync(desktop);
        await Assert.ThrowsAsync<IOException>(() => VerifiedPayloadInstaller.InstallAsync(source, destination, "win-x64"));
        Assert.Equal(original, await File.ReadAllBytesAsync(desktop));
        Assert.Empty(Directory.GetDirectories(root, ".ark-install-*"));
    }

    [Fact]
    public async Task CorruptionDoesNotPublishDestination()
    {
        var source = await FixtureAsync();
        var path = Path.Combine(source, "ArkServerManager.Desktop.exe");
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[0] ^= 1;
        await File.WriteAllBytesAsync(path, bytes);
        var destination = Path.Combine(root, "installed");
        await Assert.ThrowsAsync<InvalidDataException>(() => VerifiedPayloadInstaller.InstallAsync(source, destination, "win-x64"));
        Assert.False(Directory.Exists(destination));
        Assert.Empty(Directory.GetDirectories(root, ".ark-install-*"));
    }

    [Fact]
    public async Task UpdateStagesVerifiedPayloadWithoutCreatingInstallationSettings()
    {
        var source = await FixtureAsync();
        var zip = Path.Combine(root, "release.zip");
        ZipFile.CreateFromDirectory(source, zip);
        var update = await AppUpdateStaging.ExtractAsync(zip, root, "win-x64");
        Assert.Equal(7, update.Manifest.Files.Count);
        Assert.False(File.Exists(Path.Combine(update.Directory, "installation.json")));
        Assert.True(File.Exists(Path.Combine(update.Directory, "manager", "ArkServerManager.Host.exe")));
        Assert.Equal(7, (await VerifiedPayloadInstaller.VerifyAsync(source, "win-x64")).Files.Count);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("Servers/one/save.ark")]
    [InlineData("Clusters/one/player")]
    [InlineData("Dependencies/tool")]
    [InlineData("installation.json")]
    [InlineData("app-settings/presets.json")]
    [InlineData("catalog.json")]
    [InlineData("logs/activity.jsonl")]
    [InlineData("Bootstrap/ArkServerManager.Launcher.exe")]
    [InlineData("bootstrap/ArkServerManager.Launcher")]
    public async Task UnsafeUpdateEntryLeavesNoStageOrOutsideFile(string name)
    {
        var source = await FixtureAsync();
        var zip = Path.Combine(root, "release.zip");
        ZipFile.CreateFromDirectory(source, zip);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
        using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) writer.Write("forbidden");
        await Assert.ThrowsAsync<InvalidDataException>(() => AppUpdateStaging.ExtractAsync(zip, root, "win-x64"));
        Assert.Empty(Directory.GetDirectories(root, ".ark-update-*"));
        Assert.False(File.Exists(Path.Combine(root, "outside")));
        Assert.False(Directory.Exists(Path.Combine(root, "Servers")));
    }

    [Fact]
    public async Task CorruptUpdatePayloadIsNotKeptAsAStage()
    {
        var source = await FixtureAsync();
        await File.WriteAllTextAsync(Path.Combine(source, "ArkServerManager.Desktop.exe"), "corrupted");
        var zip = Path.Combine(root, "release.zip");
        ZipFile.CreateFromDirectory(source, zip);
        await Assert.ThrowsAsync<InvalidDataException>(() => AppUpdateStaging.ExtractAsync(zip, root, "win-x64"));
        Assert.Empty(Directory.GetDirectories(root, ".ark-update-*"));
    }

    [Fact]
    public async Task UpdatePreparationBacksUpOnlyAppFilesAndKeepsServerDataAndSettings()
    {
        var source = await FixtureAsync();
        var installed = Path.Combine(root, "installed");
        await VerifiedPayloadInstaller.InstallAsync(source, installed, "win-x64");
        var world = Path.Combine(installed, "Servers", "one", "save.ark");
        Directory.CreateDirectory(Path.GetDirectoryName(world)!);
        await File.WriteAllTextAsync(world, "world state");
        var settings = await File.ReadAllBytesAsync(Path.Combine(installed, "installation.json"));
        var record = await AppUpdatePreparation.PrepareAsync(installed, source, root, "win-x64", (work, ct) => work(ct));
        Assert.Equal(AppUpdatePhase.Prepared, record.Phase);
        Assert.Equal(7, record.Previous.Files.Count);
        Assert.True(File.Exists(Path.Combine(record.RecoveryDirectory, "update.json")));
        Assert.False(Directory.Exists(Path.Combine(record.RecoveryDirectory, "Servers")));
        Assert.False(File.Exists(Path.Combine(record.RecoveryDirectory, "installation.json")));
        Assert.Equal("world state", await File.ReadAllTextAsync(world));
        Assert.Equal(settings, await File.ReadAllBytesAsync(Path.Combine(installed, "installation.json")));
        Assert.Equal(7, (await VerifiedPayloadInstaller.VerifyInstalledAsync(installed, "win-x64")).Files.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdatePreparationRefusesChangedOrUnstoppedApp(bool changed)
    {
        var source = await FixtureAsync();
        var installed = Path.Combine(root, "installed");
        await VerifiedPayloadInstaller.InstallAsync(source, installed, "win-x64");
        var target = Path.Combine(installed, "ArkServerManager.Desktop.exe");
        var failure = await Record.ExceptionAsync(() => AppUpdatePreparation.PrepareAsync(installed, source, root, "win-x64",
            async (work, ct) =>
            {
                if (changed) { await File.WriteAllTextAsync(target, "externally changed", ct); await work(ct); }
            }));
        if (changed) Assert.IsType<InvalidDataException>(failure);
        else Assert.IsType<InvalidOperationException>(failure);
        Assert.Empty(Directory.GetDirectories(root, ".ark-rollback-*"));
        Assert.True(File.Exists(Path.Combine(installed, "installation.json")));
    }

    private static async Task ChangePayloadAsync(string source)
    {
        var manifestPath = Path.Combine(source, "payload-manifest.json");
        var manifest = JsonSerializer.Deserialize<InstallerPayload>(await File.ReadAllTextAsync(manifestPath))!;
        var name = "ArkServerManager.Desktop.exe";
        var bytes = System.Text.Encoding.UTF8.GetBytes("updated desktop");
        await File.WriteAllBytesAsync(Path.Combine(source, name), bytes);
        manifest.Files[manifest.Files.FindIndex(f => f.Path == name)] = new(name, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
        var added = System.Text.Encoding.UTF8.GetBytes("new library");
        await File.WriteAllBytesAsync(Path.Combine(source, "new.dll"), added);
        manifest.Files.Add(new("new.dll", added.Length, Convert.ToHexString(SHA256.HashData(added))));
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest));
    }

    [Fact]
    public async Task AppReplacementAndRollbackPreserveWorldAndSettings()
    {
        var source = await FixtureAsync(); var installed = Path.Combine(root, "installed");
        await VerifiedPayloadInstaller.InstallAsync(source, installed, "win-x64");
        var executable = Path.Combine(installed, "ArkServerManager.Desktop.exe");
        var old = await File.ReadAllBytesAsync(executable);
        var world = Path.Combine(installed, "Servers", "one", "save.ark");
        Directory.CreateDirectory(Path.GetDirectoryName(world)!); await File.WriteAllTextAsync(world, "world state");
        var settings = await File.ReadAllBytesAsync(Path.Combine(installed, "installation.json"));
        await ChangePayloadAsync(source);
        var record = await AppUpdatePreparation.PrepareAsync(installed, source, root, "win-x64", (work, ct) => work(ct));
        var journal = Path.Combine(record.RecoveryDirectory, "update.json");
        Assert.Equal(AppUpdatePhase.Applied, (await AppUpdateTransaction.ApplyAsync(journal, (work, ct) => work(ct))).Phase);
        Assert.Equal("updated desktop", await File.ReadAllTextAsync(executable));
        Assert.True(File.Exists(Path.Combine(installed, "new.dll")));
        // Recovery must still work after the downloaded replacement has become unusable.
        await File.WriteAllTextAsync(Path.Combine(source, "ArkServerManager.Desktop.exe"), "corrupt replacement");
        Assert.Equal(AppUpdatePhase.RolledBack, (await AppUpdateTransaction.RestoreAsync(journal, (work, ct) => work(ct))).Phase);
        Assert.Equal(old, await File.ReadAllBytesAsync(executable));
        Assert.False(File.Exists(Path.Combine(installed, "new.dll")));
        Assert.Equal("world state", await File.ReadAllTextAsync(world));
        Assert.Equal(settings, await File.ReadAllBytesAsync(Path.Combine(installed, "installation.json")));
    }

    [Fact]
    public async Task InterruptedAppReplacementCanRestoreAndDoesNotOverwriteExternalChanges()
    {
        var source = await FixtureAsync(); var installed = Path.Combine(root, "installed");
        await VerifiedPayloadInstaller.InstallAsync(source, installed, "win-x64");
        await ChangePayloadAsync(source);
        var record = await AppUpdatePreparation.PrepareAsync(installed, source, root, "win-x64", (work, ct) => work(ct));
        var journal = Path.Combine(record.RecoveryDirectory, "update.json");
        await new AtomicJsonStore<AppUpdateRecord>(journal).SaveAsync(record with { Phase = AppUpdatePhase.Applying });
        var executable = Path.Combine(installed, "ArkServerManager.Desktop.exe");
        File.Copy(Path.Combine(source, "ArkServerManager.Desktop.exe"), executable, true);
        var library = Path.Combine(installed, "System.Private.CoreLib.dll");
        var original = await File.ReadAllBytesAsync(library);
        await File.WriteAllTextAsync(library, "external change");
        await Assert.ThrowsAsync<IOException>(() => AppUpdateTransaction.RestoreAsync(journal, (work, ct) => work(ct)));
        Assert.Equal("external change", await File.ReadAllTextAsync(library));
        Assert.Equal("updated desktop", await File.ReadAllTextAsync(executable));
        await File.WriteAllBytesAsync(library, original);
        Assert.Equal(AppUpdatePhase.RolledBack, (await AppUpdateTransaction.RestoreAsync(journal, (work, ct) => work(ct))).Phase);
        Assert.Equal("fixture ArkServerManager.Desktop.exe", await File.ReadAllTextAsync(executable));
    }

    [Fact]
    public async Task LaunchRecoveryRestoresMixedFilesBeforeOpeningInstalledVersion()
    {
        var source = await FixtureAsync(); var installed = Path.Combine(root, "installed");
        await VerifiedPayloadInstaller.InstallAsync(source, installed, "win-x64");
        await ChangePayloadAsync(source);
        var record = await AppUpdatePreparation.PrepareAsync(installed, source, root, "win-x64", (work, ct) => work(ct));
        var result = Path.Combine(root, "result.json");
        await AppUpdateLaunchRecovery.PublishAsync(record, result);
        await new AtomicJsonStore<AppUpdateRecord>(Path.Combine(record.RecoveryDirectory, "update.json")).SaveAsync(record with { Phase = AppUpdatePhase.Applying });
        File.Copy(Path.Combine(source, "ArkServerManager.Desktop.exe"), Path.Combine(installed, "ArkServerManager.Desktop.exe"), true);
        using (AppFileLease.OpenUsageIfInstalled(installed))
            await Assert.ThrowsAsync<IOException>(() => AppUpdateLaunchRecovery.RecoverBeforeLaunchAsync(installed));
        var recovered = await AppUpdateLaunchRecovery.RecoverBeforeLaunchAsync(installed);
        Assert.Equal(AppUpdatePhase.RolledBack, recovered!.Record.Phase);
        Assert.Equal("fixture ArkServerManager.Desktop.exe", await File.ReadAllTextAsync(Path.Combine(installed, "ArkServerManager.Desktop.exe")));
        var outcome = await new AtomicJsonStore<AppUpdateLaunchOutcome>(result).LoadAsync();
        Assert.False(outcome!.Updated); Assert.Contains("interrupted", outcome.Notice);
        Assert.Equal(AppUpdatePhase.RolledBack, (await AppUpdateLaunchRecovery.RecoverBeforeLaunchAsync(installed))!.Record.Phase);
    }

    [Fact]
    public async Task InstallerPlacesBootstrapOutsideItsUpdatableManifest()
    {
        var source = await FixtureAsync(); var manifestPath = Path.Combine(source, "payload-manifest.json");
        var manifest = (await new AtomicJsonStore<InstallerPayload>(manifestPath).LoadAsync())!;
        var relative = "bootstrap-payload/ArkServerManager.Launcher.exe"; var bytes = System.Text.Encoding.UTF8.GetBytes("standalone launcher fixture");
        Directory.CreateDirectory(Path.Combine(source, "bootstrap-payload")); await File.WriteAllBytesAsync(Path.Combine(source, relative), bytes);
        manifest.Files.Add(new(relative, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))));
        await new AtomicJsonStore<InstallerPayload>(manifestPath).SaveAsync(manifest);
        var installed = Path.Combine(root, "installed"); await VerifiedPayloadInstaller.InstallAsync(source, installed, "win-x64");
        Assert.Equal(bytes, await File.ReadAllBytesAsync(AppBootstrapLayout.ExecutableFor(installed, "win-x64")));
        var owned = await VerifiedPayloadInstaller.VerifyInstalledAsync(installed, "win-x64");
        Assert.DoesNotContain(owned.Files, file => file.Path.StartsWith("Bootstrap/", StringComparison.OrdinalIgnoreCase) || AppBootstrapLayout.IsBootstrapPayloadFile(file.Path));
        Assert.False(Directory.Exists(Path.Combine(installed, "bootstrap-payload")));
    }

    [Fact]
    public async Task WindowsLockedAppFileRollsBackAlreadyReplacedFiles()
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = await FixtureAsync(); var installed = Path.Combine(root, "installed");
        await VerifiedPayloadInstaller.InstallAsync(source, installed, "win-x64");
        await ChangePayloadAsync(source);
        var record = await AppUpdatePreparation.PrepareAsync(installed, source, root, "win-x64", (work, ct) => work(ct));
        var journal = Path.Combine(record.RecoveryDirectory, "update.json");
        using var locked = new FileStream(Path.Combine(installed, "ArkServerManager.Desktop.runtimeconfig.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        await Assert.ThrowsAsync<IOException>(() => AppUpdateTransaction.ApplyAsync(journal, (work, ct) => work(ct)));
        Assert.Equal("fixture ArkServerManager.Desktop.exe", await File.ReadAllTextAsync(Path.Combine(installed, "ArkServerManager.Desktop.exe")));
        Assert.Equal(AppUpdatePhase.RolledBack, (await new AtomicJsonStore<AppUpdateRecord>(journal).LoadAsync())!.Phase);
        Assert.Empty(Directory.GetFiles(installed, "*.ark-update-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task RollbackPreservesExternallyChangedInstalledManifest()
    {
        var source = await FixtureAsync(); var installed = Path.Combine(root, "installed");
        await VerifiedPayloadInstaller.InstallAsync(source, installed, "win-x64");
        await ChangePayloadAsync(source);
        var record = await AppUpdatePreparation.PrepareAsync(installed, source, root, "win-x64", (work, ct) => work(ct));
        var journal = Path.Combine(record.RecoveryDirectory, "update.json");
        await AppUpdateTransaction.ApplyAsync(journal, (work, ct) => work(ct));
        var manifest = Path.Combine(installed, "payload-manifest.json");
        await File.AppendAllTextAsync(manifest, "\n ");
        var changed = await File.ReadAllBytesAsync(manifest);
        await Assert.ThrowsAsync<IOException>(() => AppUpdateTransaction.RestoreAsync(journal, (work, ct) => work(ct)));
        Assert.Equal(changed, await File.ReadAllBytesAsync(manifest));
        Assert.Equal("updated desktop", await File.ReadAllTextAsync(Path.Combine(installed, "ArkServerManager.Desktop.exe")));
    }

    [Fact]
    public async Task UpdateConfirmationRequiresStartupAndMatchingInstalledApp()
    {
        var source = await FixtureAsync(); var installed = Path.Combine(root, "installed");
        await VerifiedPayloadInstaller.InstallAsync(source, installed, "win-x64");
        await ChangePayloadAsync(source);
        var record = await AppUpdatePreparation.PrepareAsync(installed, source, root, "win-x64", (work, ct) => work(ct));
        var journal = Path.Combine(record.RecoveryDirectory, "update.json");
        await AppUpdateTransaction.ApplyAsync(journal, (work, ct) => work(ct));
        Assert.Equal(AppUpdatePhase.Applied, (await AppUpdateTransaction.ConfirmAsync(journal, installed, _ => Task.FromResult(false))).Phase);
        await Assert.ThrowsAsync<InvalidDataException>(() => AppUpdateTransaction.ConfirmAsync(journal, source, _ => Task.FromResult(true)));
        Assert.Equal(AppUpdatePhase.Confirmed, (await AppUpdateTransaction.ConfirmAsync(journal, installed, _ => Task.FromResult(true))).Phase);
        Assert.Equal(AppUpdatePhase.Confirmed, (await AppUpdateTransaction.ConfirmAsync(journal, installed, _ => throw new InvalidOperationException())).Phase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => AppUpdateTransaction.RestoreAsync(journal, (work, ct) => work(ct)));
        Assert.True(File.Exists(Path.Combine(record.RecoveryDirectory, "ArkServerManager.Desktop.exe")));
    }

    [Fact]
    public async Task NewFileCreatedAfterPreparationIsPreservedEvenWhenItsBytesMatchTheUpdate()
    {
        var source = await FixtureAsync(); var installed = Path.Combine(root, "installed");
        await VerifiedPayloadInstaller.InstallAsync(source, installed, "win-x64");
        await ChangePayloadAsync(source);
        var record = await AppUpdatePreparation.PrepareAsync(installed, source, root, "win-x64", (work, ct) => work(ct));
        var journal = Path.Combine(record.RecoveryDirectory, "update.json");
        var target = Path.Combine(installed, "new.dll");
        File.Copy(Path.Combine(source, "new.dll"), target);
        await Assert.ThrowsAsync<IOException>(() => AppUpdateTransaction.ApplyAsync(journal, (work, ct) => work(ct)));
        Assert.Equal("new library", await File.ReadAllTextAsync(target));
        Assert.Equal("fixture ArkServerManager.Desktop.exe", await File.ReadAllTextAsync(Path.Combine(installed, "ArkServerManager.Desktop.exe")));
        Assert.Equal(AppUpdatePhase.Prepared, (await new AtomicJsonStore<AppUpdateRecord>(journal).LoadAsync())!.Phase);
        await AppUpdateTransaction.RestoreAsync(journal, (work, ct) => work(ct));
        await AppUpdateTransaction.RestoreAsync(journal, (work, ct) => work(ct));
        Assert.Equal("new library", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task InstalledAppAndHostUsageBlockUpdaterUntilBothClose()
    {
        var source = await FixtureAsync(); var installed = Path.Combine(root, "installed");
        await VerifiedPayloadInstaller.InstallAsync(source, installed, "win-x64");
        using (var app = AppFileLease.OpenUsageIfInstalled(installed))
        {
            using (var host = AppFileLease.OpenUsageIfInstalled(installed))
                Assert.Throws<IOException>(() => AppFileLease.OpenForUpdate(installed));
            Assert.Throws<IOException>(() => AppFileLease.OpenForUpdate(installed));
        }
        using (var update = AppFileLease.OpenForUpdate(installed))
            Assert.Throws<IOException>(() => AppFileLease.OpenUsageIfInstalled(installed));
        using var reopened = AppFileLease.OpenUsageIfInstalled(installed);
        Assert.NotNull(reopened);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentUpdaterAppliesAndRestoresOrRefusesLiveApp(bool appRunning)
    {
        var source = await FixtureAsync(); var installed = Path.Combine(root, "installed");
        await VerifiedPayloadInstaller.InstallAsync(source, installed, "win-x64");
        await ChangePayloadAsync(source);
        using var usage = appRunning ? AppFileLease.OpenUsageIfInstalled(installed) : null;
        var applied = await RunUpdaterAsync("--apply", installed, source, root, "win-x64");
        if (appRunning)
        {
            Assert.Equal(1, applied.ExitCode);
            Assert.Equal("fixture ArkServerManager.Desktop.exe", await File.ReadAllTextAsync(Path.Combine(installed, "ArkServerManager.Desktop.exe")));
            Assert.Empty(Directory.GetDirectories(root, ".ark-rollback-*"));
            return;
        }
        Assert.True(applied.ExitCode == 0, applied.Error);
        using var result = JsonDocument.Parse(applied.Output);
        var recovery = result.RootElement.GetProperty("RecoveryDirectory").GetString()!;
        Assert.Equal("updated desktop", await File.ReadAllTextAsync(Path.Combine(installed, "ArkServerManager.Desktop.exe")));
        var restored = await RunUpdaterAsync("--restore", installed, Path.Combine(recovery, "update.json"));
        Assert.True(restored.ExitCode == 0, restored.Error);
        Assert.Equal("fixture ArkServerManager.Desktop.exe", await File.ReadAllTextAsync(Path.Combine(installed, "ArkServerManager.Desktop.exe")));
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunUpdaterAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "updater-fixture", "ArkServerManager.Updater.dll"));
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(deadline.Token); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        return (process.ExitCode, await output, await error);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task UpdaterHandoffWaitsForParentAndReopensUpdatedOrBusyInstalledApp(bool managerRunning, bool startupFails)
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = await FixtureAsync();
        var fixture = Path.Combine(AppContext.BaseDirectory, "fixture");
        foreach (var file in Directory.GetFiles(fixture, "ArkServerManager.ProcessFixture*"))
            File.Copy(file, Path.Combine(source, Path.GetFileName(file)), overwrite: false);
        File.Copy(Path.Combine(fixture, "ArkServerManager.ProcessFixture.exe"), Path.Combine(source, "ArkServerManager.Desktop.exe"), overwrite: true);
        await File.WriteAllTextAsync(Path.Combine(source, "update-startup-fixture.txt"), "ready");
        var files = new List<PayloadFile>();
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories).Where(f => Path.GetFileName(f) != "payload-manifest.json"))
        {
            var bytes = await File.ReadAllBytesAsync(file);
            files.Add(new(Path.GetRelativePath(source, file).Replace('\\', '/'), bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))));
        }
        await File.WriteAllTextAsync(Path.Combine(source, "payload-manifest.json"), JsonSerializer.Serialize(new InstallerPayload(1, "win-x64", true, files)));
        var installed = Path.Combine(root, "installed");
        await VerifiedPayloadInstaller.InstallAsync(source, installed, "win-x64");
        if (startupFails)
        {
            var failed = System.Text.Encoding.UTF8.GetBytes("fail");
            await File.WriteAllBytesAsync(Path.Combine(source, "update-startup-fixture.txt"), failed);
            files[files.FindIndex(f => f.Path == "update-startup-fixture.txt")] = new("update-startup-fixture.txt", failed.Length, Convert.ToHexString(SHA256.HashData(failed)));
            await File.WriteAllTextAsync(Path.Combine(source, "payload-manifest.json"), JsonSerializer.Serialize(new InstallerPayload(1, "win-x64", true, files)));
        }
        using var manager = managerRunning ? AppFileLease.OpenUsageIfInstalled(installed) : null;
        var parentStart = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        parentStart.ArgumentList.Add(Path.Combine(fixture, "ArkServerManager.ProcessFixture.dll")); parentStart.ArgumentList.Add("--wait");
        using var parent = Process.Start(parentStart)!;
        var parentOutput = parent.StandardOutput.ReadToEndAsync(); var parentError = parent.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try
        {
            var resultPath = Path.Combine(root, "result.json");
            var updater = RunUpdaterAsync("--handoff", installed, source, root, "win-x64", parent.Id.ToString(),
                parent.StartTime.ToUniversalTime().Ticks.ToString(), resultPath);
            while (!File.Exists(resultPath + ".ready"))
            {
                if (updater.IsCompleted) Assert.Fail((await updater).Error);
                await Task.Delay(50, deadline.Token);
            }
            Assert.False(File.Exists(resultPath));
            Assert.False(parent.HasExited);
            await parent.StandardInput.WriteLineAsync("stop"); await parent.StandardInput.FlushAsync();
            await parent.WaitForExitAsync(deadline.Token);
            var completion = await updater;
            Assert.True(completion.ExitCode == 0, completion.Error);
            var outcome = await new AtomicJsonStore<AppUpdateLaunchOutcome>(resultPath).LoadAsync();
            Assert.Equal(!managerRunning && !startupFails, outcome!.Updated);
            if (startupFails)
            {
                Assert.Contains("previous version was restored", outcome.Notice);
                Assert.Equal("ready", await File.ReadAllTextAsync(Path.Combine(installed, "update-startup-fixture.txt")));
            }
            while (!File.Exists(resultPath + ".opened")) await Task.Delay(50, deadline.Token);
            Assert.Contains("--update-result", await File.ReadAllTextAsync(resultPath + ".opened"));
            Assert.Equal(managerRunning, outcome.RecoveryJournal is null);
        }
        finally
        {
            if (!parent.HasExited) { parent.Kill(entireProcessTree: true); await parent.WaitForExitAsync(); }
            await parentOutput; await parentError;
        }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
