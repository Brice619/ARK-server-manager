using ArkServerManager.Core;
namespace ArkServerManager.Core.Tests;
public sealed class InstallerExtractionCleanupTests
{
    [Fact]
    public void CleanupRemovesOnlyItsGeneratedTemporaryFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "ArkServerManager-Setup-" + Guid.NewGuid().ToString("N"));
        var unrelated = Path.Combine(Path.GetTempPath(), "ark-unrelated-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); Directory.CreateDirectory(unrelated);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "payload")); File.WriteAllText(Path.Combine(root, "payload", "file"), "test");
            File.WriteAllText(Path.Combine(unrelated, "world"), "preserve");
            Assert.Throws<IOException>(() => InstallerExtractionCleanup.Remove(unrelated));
            Assert.Throws<IOException>(() => InstallerExtractionCleanup.Remove(Path.GetTempPath()));
            InstallerExtractionCleanup.Remove(root); InstallerExtractionCleanup.Remove(root);
            Assert.False(Directory.Exists(root)); Assert.Equal("preserve", File.ReadAllText(Path.Combine(unrelated, "world")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); Directory.Delete(unrelated, true); }
    }
}
