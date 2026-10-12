using ArkServerManager.Core;
namespace ArkServerManager.Core.Tests;
public sealed class AppUpdateLaunchRecoveryTests
{
    [Theory]
    [InlineData(AppUpdatePhase.Prepared, true)]
    [InlineData(AppUpdatePhase.Applying, true)]
    [InlineData(AppUpdatePhase.RollingBack, true)]
    [InlineData(AppUpdatePhase.Applied, false)]
    [InlineData(AppUpdatePhase.RolledBack, false)]
    [InlineData(AppUpdatePhase.Confirmed, false)]
    public void OnlyUnfinishedReplacementRequiresRollback(AppUpdatePhase phase, bool rollback)
        => Assert.Equal(rollback, AppUpdateLaunchRecovery.RequiresRollback(phase));
    [Fact]
    public async Task FreshLauncherCanReadInterruptedJournalAndRefusesReplacementIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-launch-recovery-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var app = Path.Combine(root, "app"); Directory.CreateDirectory(app);
            var recovery = Path.Combine(root, "recovery"); var journal = Path.Combine(recovery, "update.json");
            var payload = new InstallerPayload(1, "win-x64", true, []);
            var record = new AppUpdateRecord(1, Guid.NewGuid(), app, Path.Combine(root, "payload"), recovery, payload, payload, AppUpdatePhase.Prepared);
            var store = new AtomicJsonStore<AppUpdateRecord>(journal); await store.SaveAsync(record);
            await AppUpdateLaunchRecovery.PublishAsync(record, Path.Combine(root, "result.json"));
            await store.SaveAsync(record with { Phase = AppUpdatePhase.Applying });
            var status = await AppUpdateLaunchRecovery.ReadAsync(app);
            Assert.Equal(record.Id, status!.Pointer.OperationId); Assert.Equal(AppUpdatePhase.Applying, status.Record.Phase);
            await store.SaveAsync(record with { Id = Guid.NewGuid() });
            await Assert.ThrowsAsync<InvalidDataException>(() => AppUpdateLaunchRecovery.ReadAsync(app));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void RecoveryInsideReplacedApplicationIsRefused()
    {
        var app = Path.Combine(Path.GetTempPath(), "ark-launch-recovery-app"); var recovery = Path.Combine(app, "recovery"); var payload = new InstallerPayload(1, "win-x64", true, []);
        var record = new AppUpdateRecord(1, Guid.NewGuid(), app, app + "-payload", recovery, payload, payload, AppUpdatePhase.Prepared);
        var pointer = new AppUpdateRecoveryPointer(1, record.Id, app, Path.Combine(recovery, "update.json"), "win-x64", app + "-result.json");
        Assert.Throws<InvalidDataException>(() => AppUpdateLaunchRecovery.Validate(app, pointer, record));
    }
}
