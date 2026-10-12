using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public class ImportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-import-test-" + Guid.NewGuid().ToString("N"));

    private async Task<string> Source()
    {
        var source = Path.Combine(root, "source");
        var binary = Path.Combine(source, "ShooterGame", "Binaries", "Win64", "ShooterGameServer.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        await File.WriteAllTextAsync(binary, "test binary");
        var saved = Path.Combine(source, "ShooterGame", "Saved", "SavedArks");
        Directory.CreateDirectory(saved);
        await File.WriteAllTextAsync(Path.Combine(saved, "TheIsland.ark"), "world state");
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        return source;
    }

    [Fact]
    public async Task OversizedConfigurationIsRejectedWithoutChangingSource()
    {
        var source = await Source();
        var folder = Path.Combine(source, "ShooterGame", "Saved", "Config", "WindowsServer");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "GameUserSettings.ini");
        const long size = 4 * 1024 * 1024 + 1;
        await using (var file = File.Create(path)) file.SetLength(size);
        await Assert.ThrowsAsync<InvalidDataException>(() => InstallationConfigurationReader.ReadAsync(source, ArkEdition.ASE, "Windows"));
        Assert.Equal(size, new FileInfo(path).Length);
        Assert.False(File.Exists(Path.Combine(folder, "Game.ini")));
    }

    [Fact]
    public async Task CanceledReadAndMismatchedEditionDoNotInspectOrChangeConfiguration()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InstallationConfigurationReader.ReadAsync("relative", ArkEdition.ASE, "Windows", canceled.Token));
        Assert.False(Directory.Exists(root));
        var source = await Source();
        await Assert.ThrowsAsync<InvalidDataException>(() => InstallationConfigurationReader.ReadAsync(source, ArkEdition.ASA, "Windows"));
        Assert.False(Directory.Exists(Path.Combine(source, "ShooterGame", "Saved", "Config")));
    }

    [Fact]
    public void ConfigurationSummaryOmitsPasswordsAndPreservesAmbiguousValuesForReview()
    {
        const string text = "[SessionSettings]\nSessionName=First\nSessionName=Second\n[ServerSettings]\nMaxPlayers=20\nMaxPlayers=40\nServerPassword=join-secret\nServerAdminPassword=admin-secret\n";
        var summary = InstallationConfigurationReader.Summarize(new("fixture", "Windows", text, null, []));
        Assert.Equal(new[] { "First", "Second" }, summary.SessionNames);
        Assert.Equal(new[] { "20", "40" }, summary.MaximumPlayers);
        Assert.True(summary.JoinPasswordConfigured);
        Assert.True(summary.AdminPasswordConfigured);
        Assert.False(summary.HasGame);
        Assert.Equal(2, summary.Findings.Count);
        var serialized = System.Text.Json.JsonSerializer.Serialize(summary);
        Assert.DoesNotContain("join-secret", serialized);
        Assert.DoesNotContain("admin-secret", serialized);
        var missing = InstallationConfigurationReader.Summarize(new("fixture", "Windows", null, null, []));
        Assert.Empty(missing.SessionNames);
        Assert.Empty(missing.MaximumPlayers);
        Assert.False(missing.JoinPasswordConfigured);
    }

    [Fact]
    public async Task ConfigurationImportPreservesUnknownRepeatedSettingsAndDoesNotInventMissingFiles()
    {
        var source = await Source();
        var folder = Path.Combine(source, "ShooterGame", "Saved", "Config", "WindowsServer");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "GameUserSettings.ini");
        const string original = "; comment\r\n[ServerSettings]\r\nServerPassword=secret\r\nUnknown=one\r\nUnknown=two\r\n";
        await File.WriteAllTextAsync(path, original);
        var result = await InstallationConfigurationReader.ReadAsync(source, ArkEdition.ASE, "Windows");
        Assert.Equal(original, result.GameUserSettings);
        Assert.Null(result.Game);
        Assert.Single(result.Findings);
        Assert.False(File.Exists(Path.Combine(folder, "Game.ini")));
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public void ManagedDestinationRejectsEscapesAndSiblingPrefixWithoutCreatingAnything()
    {
        var managed = Path.Combine(root, "managed");
        var source = Path.Combine(root, "source");
        var target = Path.Combine(managed, "server");
        Assert.Equal(target, InstallationTransfer.CheckManagedDestination(managed, source, target));
        Assert.Throws<ArgumentException>(() => InstallationTransfer.CheckManagedDestination(managed, source, managed));
        Assert.Throws<ArgumentException>(() => InstallationTransfer.CheckManagedDestination(managed, source,
            Path.Combine(managed, "..", "escaped")));
        Assert.Throws<ArgumentException>(() => InstallationTransfer.CheckManagedDestination(managed, source,
            Path.Combine(root, "managed-other", "server")));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void DestinationPreflightDoesNotCreateFoldersAndRejectsExistingOrOverlappingTargets()
    {
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        var target = Path.Combine(root, "missing-parent", "server");
        Assert.Equal(target, InstallationTransfer.CheckDestination(source, target));
        Assert.False(Directory.Exists(Path.GetDirectoryName(target)));
        var existingDirectory = Path.Combine(root, "existing-directory");
        Directory.CreateDirectory(existingDirectory);
        Assert.Throws<IOException>(() => InstallationTransfer.CheckDestination(source, existingDirectory));
        Assert.Throws<ArgumentException>(() => InstallationTransfer.CheckDestination(source, Path.Combine(source, "nested")));
        Assert.Throws<ArgumentException>(() => InstallationTransfer.CheckDestination(source, "relative-folder"));
        var existing = Path.Combine(root, "existing-file");
        File.WriteAllText(existing, "preserved");
        Assert.Throws<IOException>(() => InstallationTransfer.CheckDestination(source, existing));
        Assert.Equal("preserved", File.ReadAllText(existing));
    }

    [Fact]
    public async Task CopyPreservesOriginalAndTransfersAllFilesAndEmptyDirectories()
    {
        var source = await Source();
        var target = Path.Combine(root, "target");
        var stops = 0;
        var service = new InstallationTransfer((_, _) => { stops++; return Task.FromResult(true); });
        var result = await service.ExecuteAsync(source, target, ArkEdition.ASE, InstallationImportMode.Copy, true);
        Assert.False(result.OriginalRemoved);
        Assert.Equal(2, stops);
        Assert.True(Directory.Exists(source));
        Assert.True(Directory.Exists(Path.Combine(target, "empty")));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            Assert.Equal(await File.ReadAllBytesAsync(file), await File.ReadAllBytesAsync(Path.Combine(target, Path.GetRelativePath(source, file))));
    }

    [Fact]
    public async Task MoveRelocatesServerWithoutMovingAppOrOtherDirectories()
    {
        var source = await Source();
        await File.WriteAllTextAsync(Path.Combine(root, "unrelated.txt"), "untouched");
        var target = Path.Combine(root, "target");
        var result = await new InstallationTransfer((_, _) => Task.FromResult(true))
            .ExecuteAsync(source, target, ArkEdition.ASE, InstallationImportMode.Move, true);
        Assert.True(result.OriginalRemoved);
        Assert.False(Directory.Exists(source));
        Assert.True(File.Exists(Path.Combine(target, "ShooterGame", "Binaries", "Win64", "ShooterGameServer.exe")));
        Assert.Equal("untouched", await File.ReadAllTextAsync(Path.Combine(root, "unrelated.txt")));
    }

    [Fact]
    public async Task ConsentAndShutdownFailuresLeaveOriginalUntouched()
    {
        var source = await Source();
        var target = Path.Combine(root, "target");
        var service = new InstallationTransfer((_, _) => Task.FromResult(false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(source, target, ArkEdition.ASE, InstallationImportMode.Copy, false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(source, target, ArkEdition.ASE, InstallationImportMode.Move, true));
        Assert.True(Directory.Exists(source));
        Assert.False(Directory.Exists(target));
        var selected = await service.ExecuteAsync(source, null, ArkEdition.ASE, InstallationImportMode.ManageInPlace, false);
        Assert.Equal(source, selected.Directory);
    }

    [Fact]
    public async Task RejectsOverlappingDestination()
    {
        var source = await Source();
        await Assert.ThrowsAsync<ArgumentException>(() => new InstallationTransfer((_, _) => Task.FromResult(true))
            .ExecuteAsync(source, Path.Combine(source, "copy"), ArkEdition.ASE, InstallationImportMode.Copy, true));
        Assert.True(Directory.Exists(source));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
