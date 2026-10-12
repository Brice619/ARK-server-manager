using System.Security.Principal;
using System.Runtime.Versioning;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class WindowsSnapshotBrokerTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task UnansweredPermissionPromptCanBeCanceledAndDoesNotHoldTheSessionGate()
    {
        if (!OperatingSystem.IsWindows()) return;
        var executable = Path.Combine(AppContext.BaseDirectory, "host-fixture", "WindowsSnapshot", "ArkServerManager.WindowsSnapshot.exe");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var launches = 0;
        await using var broker = new WindowsSnapshotBroker(executable, _ =>
        {
            if (Interlocked.Increment(ref launches) == 1)
            {
                entered.SetResult(); release.Wait(); finished.SetResult();
            }
            return null;
        });
        try
        {
            using var cancellation = new CancellationTokenSource();
            var attempt = broker.InitializeAsync(true, cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt.WaitAsync(TimeSpan.FromSeconds(5)));
            await Assert.ThrowsAsync<IOException>(() => broker.InitializeAsync(false).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(2, launches);
        }
        finally { release.Set(); await finished.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task AutomaticCaptureDoesNotPromptForElevationBeforeExplicitPreparation()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var broker = new WindowsSnapshotBroker(Path.Combine(Path.GetTempPath(), "missing-snapshot-helper.exe"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => broker.AcquireSetAsync([Path.GetTempPath()]));
        await Assert.ThrowsAsync<FileNotFoundException>(() => broker.InitializeAsync(false));
        await broker.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => broker.InitializeAsync(false));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task RealHelperRefusesUnelevatedPreparationWithoutRequestingUac()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return;
        var executable = Path.Combine(AppContext.BaseDirectory, "host-fixture", "WindowsSnapshot", "ArkServerManager.WindowsSnapshot.exe");
        Assert.True(File.Exists(executable));
        await using var broker = new WindowsSnapshotBroker(executable);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<IOException>(() => broker.InitializeAsync(false, deadline.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => broker.AcquireSetAsync([Path.GetTempPath()], deadline.Token));
    }
}
