using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class SteamNativeConsoleReaderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-steam-console-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task IgnoresPreviousSuccessAndReadsOnlyCompleteNewLines()
    {
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        var path = Path.Combine(root, "logs", "console_log.txt");
        await File.WriteAllTextAsync(path, "Success! App '376030' fully installed.\n");
        var reader = new SteamNativeConsoleReader(Path.Combine(root, "steamcmd.exe"));
        await reader.ReadAsync(default); Assert.False(reader.Confirmed(376030));
        await File.AppendAllTextAsync(path, "downloading 20%\nSuccess! App '376030' fully");
        await reader.ReadAsync(default); Assert.False(reader.Confirmed(376030));
        Assert.Contains("20%", Assert.Single(reader.Tail).Text);
        await File.AppendAllTextAsync(path, " installed.\n");
        await reader.ReadAsync(default); Assert.True(reader.Confirmed(376030)); Assert.False(reader.Confirmed(2430930));
        await File.WriteAllTextAsync(path, "short");
        await Assert.ThrowsAsync<IOException>(() => reader.ReadAsync(default));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
