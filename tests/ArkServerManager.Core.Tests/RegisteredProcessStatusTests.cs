using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class RegisteredProcessStatusTests
{
    [Theory]
    [InlineData(false, false, RegisteredProcessState.NotDetected)]
    [InlineData(false, true, RegisteredProcessState.Unknown)]
    [InlineData(true, false, RegisteredProcessState.Unknown)]
    public void MissingProcessDoesNotImplyAbsenceWhenScanOrCompatibilityIsIncomplete(
        bool linux, bool unresolved, RegisteredProcessState expected)
    {
        var id = Guid.NewGuid();
        var folder = Path.Combine(Path.GetTempPath(), "ark-status-fixture");
        var catalog = new CatalogDocument(1, [], [new(id, ArkEdition.ASA, "TheIsland_WP", folder, new())]);
        var scan = new ServerProcessScan(DateTimeOffset.UtcNow, [], unresolved ? [new(12, "server", "unverified")] : [], []);
        var status = RegisteredProcessStatus.FromScan(catalog, scan, linux);
        Assert.Equal(scan.MeasuredAt, status.MeasuredAt);
        Assert.Equal(expected, Assert.Single(status.Servers).State);
    }

    [Fact]
    public void DetectionMatchesExactInstallationAndEditionDespiteOtherUnresolvedProcesses()
    {
        var id = Guid.NewGuid();
        var folder = Path.Combine(Path.GetTempPath(), "ark-status-fixture");
        var catalog = new CatalogDocument(1, [], [new(id, ArkEdition.ASE, "TheIsland", folder, new())]);
        var now = DateTimeOffset.UtcNow;
        var scan = new ServerProcessScan(now,
            [new(10, now, "fixture", folder + Path.DirectorySeparatorChar, ArkEdition.ASE, "Windows"),
             new(11, now, "fixture", folder + "-other", ArkEdition.ASE, "Windows"),
             new(12, now, "fixture", folder, ArkEdition.ASA, "Windows")], [new(13, "server", "unverified")], []);
        var result = Assert.Single(RegisteredProcessStatus.FromScan(catalog, scan, false).Servers);
        Assert.Equal(RegisteredProcessState.Detected, result.State);
        Assert.Equal(1, result.ProcessCount);
    }
}
