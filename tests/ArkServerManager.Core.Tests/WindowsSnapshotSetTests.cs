using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Core.Tests;

public sealed class WindowsSnapshotSetTests
{
    private static WindowsSnapshotResponse Response() => new(1, new(Guid.NewGuid(), DateTimeOffset.UtcNow),
        [new(@"C:\", Guid.NewGuid(), @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy12"),
         new(@"D:\", Guid.NewGuid(), @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy13")]);

    [Fact]
    public void MapsMembersAndSharedDataAcrossVolumesFromOneSetResponse()
    {
        if (!OperatingSystem.IsWindows()) return;
        var sources = new[] { @"C:\Servers\Island\Saved", @"D:\Servers\Scorched\Saved", @"C:\Clusters\Transfers" };
        var mapped = WindowsSnapshotSetProvider.ResolveDirectories(sources, Response());
        Assert.Equal(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy12\Servers\Island\Saved", mapped[sources[0]]);
        Assert.Equal(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy13\Servers\Scorched\Saved", mapped[sources[1]]);
        Assert.EndsWith(@"Clusters\Transfers", mapped[sources[2]]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)mapped).Add(@"C:\Other", "unreviewed"));
    }

    [Theory]
    [InlineData("missing-volume")]
    [InlineData("extra-volume")]
    [InlineData("repeated-volume")]
    [InlineData("invalid-device")]
    [InlineData("empty-capture")]
    [InlineData("mismatched-device-identity")]
    public void RefusesIncompleteOrForgedSetResponses(string kind)
    {
        if (!OperatingSystem.IsWindows()) return;
        var response = Response();
        response = kind switch
        {
            "missing-volume" => response with { Volumes = [response.Volumes[0]] },
            "extra-volume" => response with { Volumes = [.. response.Volumes, new(@"E:\", Guid.NewGuid(), @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy14")] },
            "repeated-volume" => response with { Volumes = [response.Volumes[0], response.Volumes[0]] },
            "invalid-device" => response with { Volumes = [response.Volumes[0], response.Volumes[1] with { DeviceObject = @"C:\Windows" }] },
            "empty-capture" => response with { Capture = new(Guid.Empty, DateTimeOffset.UtcNow) },
            _ => response with { Volumes = [response.Volumes[0], response.Volumes[1] with { SnapshotId = response.Volumes[0].SnapshotId }] }
        };
        Assert.Throws<InvalidDataException>(() => WindowsSnapshotSetProvider.ResolveDirectories(
            [@"C:\Servers\Saved", @"D:\Clusters\Transfers"], response));
    }

    [Fact]
    public void AllowsDriveAliasesOnlyWhenSnapshotIdentityAndDeviceAgree()
    {
        if (!OperatingSystem.IsWindows()) return;
        var response = Response();
        response = response with { Volumes = [response.Volumes[0], response.Volumes[0] with { Volume = @"D:\" }] };
        var mapped = WindowsSnapshotSetProvider.ResolveDirectories([@"C:\Servers\Saved", @"D:\Clusters\Transfers"], response);
        Assert.All(mapped.Values, path => Assert.StartsWith(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy12\", path));
    }

    [Fact]
    public async Task CopiedHelperReportsCapabilitiesWithoutCreatingSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-vss-capability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "host-fixture", "WindowsSnapshot");
            var executable = Path.Combine(directory, OperatingSystem.IsWindows() ? "ArkServerManager.WindowsSnapshot.exe" : "ArkServerManager.WindowsSnapshot");
            await using var helper = ManagedProcess.Start(new(executable, directory, ["--capabilities"], Path.Combine(root, "helper.log")));
            Assert.Equal(0, (await helper.Completion.WaitAsync(TimeSpan.FromSeconds(10))).ExitCode);
            var capability = JsonSerializer.Deserialize<WindowsSnapshotCapability>(Assert.Single(helper.ConsoleTail, line => !line.StandardError).Text)!;
            Assert.Equal(OperatingSystem.IsWindows(), capability.Windows);
            Assert.False(string.IsNullOrWhiteSpace(capability.Notice));
            if (OperatingSystem.IsWindows())
            {
                Assert.True(capability.ProviderLoaded);
                Assert.True(File.Exists(Path.Combine(directory, "third-party", "AlphaVSS-LICENSE.txt")));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MalformedHelperRequestExitsWithoutReachingCapture()
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-vss-request-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "host-fixture", "WindowsSnapshot");
            var executable = Path.Combine(directory, OperatingSystem.IsWindows() ? "ArkServerManager.WindowsSnapshot.exe" : "ArkServerManager.WindowsSnapshot");
            await using var helper = ManagedProcess.Start(new(executable, directory, [], Path.Combine(root, "helper.log")));
            if (OperatingSystem.IsWindows()) await helper.SendStandardInputAsync("{\"SchemaVersion\":1,\"Volumes\":[\"C:\\\\unexpected\"]}");
            Assert.Equal(1, (await helper.Completion.WaitAsync(TimeSpan.FromSeconds(10))).ExitCode);
            using var result = JsonDocument.Parse(Assert.Single(helper.ConsoleTail, line => !line.StandardError).Text);
            Assert.True(result.RootElement.TryGetProperty("Error", out _));
        }
        finally { Directory.Delete(root, true); }
    }
}
