using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class DeletionRecoveryStopGuardTests
{
    [Theory]
    [InlineData("original")]
    [InlineData("staged")]
    [InlineData("unresolved")]
    [InlineData("other")]
    public async Task MissingFoldersDoNotHideRunningOrUnresolvedProcesses(string evidence)
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-recovery-check-" + Guid.NewGuid().ToString("N"));
        var operation = Guid.NewGuid();
        var original = Path.Combine(root, "map");
        var staged = Path.Combine(root, ".ark-delete-" + operation.ToString("N"));
        var record = new ServerDeletionRecord(1, operation, Guid.NewGuid(), "Map", original, staged, null, [], ServerDeletionPhase.Staged);
        var location = evidence == "original" ? original : evidence == "staged" ? staged : Path.Combine(root, "peer");
        var guard = new DeletionRecoveryStopGuard(_ => Task.FromResult(new ServerProcessScan(DateTimeOffset.UtcNow,
            evidence == "unresolved" ? [] : [new(100, DateTimeOffset.UtcNow, Path.Combine(location, "ShooterGameServer.exe"), location, ArkEdition.ASE, "Windows")],
            evidence == "unresolved" ? [new(101, "ShooterGameServer", "unavailable")] : [], [])));
        Assert.Equal(OperatingSystem.IsWindows() && evidence == "other", await guard.VerifyAsync(record));
        Assert.False(Directory.Exists(root));
    }
}
