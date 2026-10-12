using ArkServerManager.Core;
namespace ArkServerManager.Core.Tests;
public sealed class LinuxAppStartupTests
{
    [Fact]
    public void RegistrationRoundTripPreservesUnrelatedAndEditedEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-startup-files-" + Guid.NewGuid().ToString("N"));
        try
        {
            var startup = new LinuxAppStartup(root); const string app = "/apps/ARK Server Manager";
            Assert.False(startup.Read(app)); startup.Save(app, true); Assert.True(startup.Read(app));
            var own = Directory.GetFiles(Path.Combine(root, "autostart")).Single();
            var other = Path.Combine(root, "autostart", "other.desktop"); File.WriteAllText(other, "keep");
            File.WriteAllText(own, "user edited entry");
            Assert.Throws<IOException>(() => startup.Save(app, false)); Assert.Equal("user edited entry", File.ReadAllText(own));
            File.WriteAllText(own, LinuxAppStartup.Entry(app)); startup.Save(app, false); Assert.False(startup.Read(app));
            Assert.Equal("keep", File.ReadAllText(other));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
