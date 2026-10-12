using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ResourceTests
{
    [Fact]
    public void LinuxAvailableMemoryIncludesReclaimableCapacityAndRejectsMissingData()
    {
        var memory = ResourceChecks.ReadLinuxMemory("MemTotal: 8192 kB\nMemFree: 1024 kB\nMemAvailable: 4096 kB\nBuffers: 512 kB\n");
        Assert.Equal(8192 * 1024, memory.TotalBytes);
        Assert.Equal(4096 * 1024, memory.AvailableBytes);
        Assert.Throws<InvalidDataException>(() => ResourceChecks.ReadLinuxMemory("MemTotal: 8192 kB\nMemFree: 1024 kB"));
        Assert.Throws<InvalidDataException>(() => ResourceChecks.ReadLinuxMemory("MemTotal: 8192 kB\nMemAvailable: 9000 kB"));
        Assert.Throws<InvalidDataException>(() => ResourceChecks.ReadLinuxMemory("MemTotal: 8192 kB\nMemAvailable: -1 kB"));
        Assert.Throws<InvalidDataException>(() => ResourceChecks.ReadLinuxMemory("MemTotal: 8192 kB\nMemAvailable: 100 kB\nMemAvailable: 200 kB"));
    }

    [Fact]
    public void AssessmentUsesAvailableCapacityAndTreatsUnknownValuesAsUnknown()
    {
        var snapshot = new ResourceSnapshot(DateTimeOffset.UtcNow, new(1000, 100), new("folder", 10000, 1000), null, null);
        var assessed = ResourceChecks.Assess(snapshot, new(200, 1000));
        Assert.True(assessed.HasInsufficientResources);
        Assert.Equal(ResourceAvailability.Insufficient, assessed.Memory.Availability);
        Assert.Equal(ResourceAvailability.Sufficient, assessed.Disk.Availability);
        Assert.True(ResourceChecks.Assess(snapshot, new(null, 500)).HasUnknownResources);
        Assert.True(ResourceChecks.Assess(snapshot with { Disk = null, DiskError = "Unavailable" }, new(50, 500)).HasUnknownResources);
        Assert.Throws<ArgumentException>(() => ResourceChecks.Assess(snapshot, new(-1, 0)));
    }

    [Fact]
    public void MeasuresLocalCapacityWithoutCreatingDestinationAndReportsFileParentFailure()
    {
        var destination = Path.Combine(Path.GetTempPath(), "ark-resources-" + Guid.NewGuid().ToString("N"), "new-server");
        var snapshot = ResourceChecks.Measure(destination);
        Assert.False(Directory.Exists(Path.GetDirectoryName(destination)));
        Assert.NotNull(snapshot.Disk);
        Assert.True(snapshot.Disk.AvailableBytes >= 0);
        Assert.True(snapshot.Disk.TotalBytes >= snapshot.Disk.AvailableBytes);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            Assert.NotNull(snapshot.Memory);
            Assert.True(snapshot.Memory.TotalBytes > 0);
            Assert.InRange(snapshot.Memory.AvailableBytes, 0, snapshot.Memory.TotalBytes);
        }
        var file = Path.GetTempFileName();
        try
        {
            var bad = ResourceChecks.Measure(Path.Combine(file, "server"));
            Assert.Null(bad.Disk);
            Assert.NotNull(bad.DiskError);
        }
        finally { File.Delete(file); }
    }
}
