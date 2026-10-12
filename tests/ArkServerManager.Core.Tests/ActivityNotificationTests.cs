using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ActivityNotificationTests
{
    [Fact]
    public void IgnoresStartupHistoryGroupsNewEventsAndDoesNotReplayAfterPreferenceChanges()
    {
        var tracker = new ActivityNotificationTracker();
        var now = DateTimeOffset.UtcNow;
        ActivityEntry Entry(ActivitySeverity severity, int seconds, Guid? operation = null) =>
            new(Guid.NewGuid(), now.AddSeconds(seconds), "operation", severity, "Summary", OperationId: operation);
        var historical = Entry(ActivitySeverity.Error, -1);
        var preferences = new NotificationPreferences();
        Assert.Empty(tracker.Observe(new([historical], false, 0), preferences));
        var clockAdjustedFailure = Entry(ActivitySeverity.Error, -60);
        Assert.Equal(clockAdjustedFailure, Assert.Single(tracker.Observe(new([clockAdjustedFailure, historical], false, 0), preferences)));
        var warning = Entry(ActivitySeverity.Warning, 0);
        var error = Entry(ActivitySeverity.Error, 0);
        var completed = Entry(ActivitySeverity.Information, 0, Guid.NewGuid());
        var status = Entry(ActivitySeverity.Information, 0);
        var history = new ActivityHistory([historical, warning, error, completed, status], false, 0);
        Assert.Equal(new[] { warning.Id, error.Id }.OrderBy(id => id), tracker.Observe(history, preferences).Select(e => e.Id).OrderBy(id => id));
        Assert.Empty(tracker.Observe(history, preferences with { CompletedOperations = true }));
        var later = Entry(ActivitySeverity.Information, 1, Guid.NewGuid());
        Assert.Equal(later, Assert.Single(tracker.Observe(new([later, warning, error], false, 0), preferences with { CompletedOperations = true })));
        Assert.Empty(tracker.Observe(new([historical], false, 0), preferences));
    }

    [Fact]
    public async Task PersistsPreferencesAndRejectsUnsupportedSavedVersion()
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-notifications-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "notifications.json");
            var desired = new NotificationPreferences { InApp = false, OperatingSystem = false, CompletedOperations = true };
            await new AtomicJsonStore<NotificationPreferences>(path, true).SaveAsync(desired);
            Assert.Equal(desired, await new AtomicJsonStore<NotificationPreferences>(path, true).LoadAsync());
            Assert.Throws<InvalidDataException>(() => new NotificationPreferences { SchemaVersion = 2 }.Validate());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("ubuntu", "/usr/bin/apt-get", "libnotify4")]
    [InlineData("debian", "/usr/bin/apt-get", "libnotify4")]
    [InlineData("fedora", "/usr/bin/dnf", "libnotify")]
    [InlineData("arch", "/usr/bin/pacman", "libnotify")]
    public void MissingLinuxNotificationSupportUsesFixedDistributionPackage(string distribution, string installer, string package)
    {
        var command = LinuxNotificationToolPlan.Select("ID=" + distribution);
        Assert.Equal(installer, command.Executable);
        Assert.Equal(package, command.Arguments.Last());
        Assert.DoesNotContain(command.Arguments, argument => argument.Contains(';') || argument.Contains('$'));
    }

    [Fact]
    public void UnsupportedDistributionDoesNotChooseAnInstaller()
        => Assert.Throws<NotSupportedException>(() => LinuxNotificationToolPlan.Select("ID=unknown"));
}
