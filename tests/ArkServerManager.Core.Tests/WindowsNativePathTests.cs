using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class WindowsNativePathTests
{
    [Fact]
    public async Task AlternateDirectoryNameCannotHideObservedRunningInstallation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ark-native-path-with-long-name-" + Guid.NewGuid().ToString("N"));
        try
        {
            var executable = Path.Combine(directory, "ShooterGame", "Binaries", "Win64", "ArkAscendedServer.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!); await File.WriteAllTextAsync(executable, "fixture");
            var alternate = WindowsNativePath.ShortenExistingDirectory(directory);
            Assert.Equal(WindowsNativePath.ExpandExisting(directory), WindowsNativePath.ExpandExisting(alternate));
            Assert.Equal(WindowsNativePath.ResolveFinalExisting(directory), WindowsNativePath.ResolveFinalExisting(alternate));
            Assert.Equal(WindowsNativePath.ResolveFinalExisting(executable),
                WindowsNativePath.ResolveFinalExisting(Path.Combine(alternate, "ShooterGame", "Binaries", "Win64", "ArkAscendedServer.exe")));
            Assert.True(alternate.Length <= directory.Length);
            var measured = DateTimeOffset.UtcNow;
            var candidate = new ServerProcessCandidate(100, measured, executable, directory, ArkEdition.ASA, "Windows");
            var guard = new InstallationStopGuard(ct => Task.FromResult(new ServerProcessScan(measured, [candidate], [], [])));
            if (OperatingSystem.IsWindows())
            {
                var result = await guard.CheckAsync(alternate);
                Assert.Equal(InstallationStopState.Running, result.State);
                Assert.Equal(directory, result.Directory);
                Assert.Equal(candidate, Assert.Single(result.Processes));
            }
            // No folders or aliases are created by the conversion itself.
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(executable)!));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
