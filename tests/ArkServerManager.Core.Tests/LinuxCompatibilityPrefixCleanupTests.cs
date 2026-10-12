using ArkServerManager.Core;
namespace ArkServerManager.Core.Tests;
public sealed class LinuxCompatibilityPrefixCleanupTests
{
    [Fact]
    public void CleanupRefusesAnotherServersPrefixOrIncompleteProcessIdentity()
    {
        var server = Guid.NewGuid(); var record = new LinuxAsaPrefixRecord(1, server, "/managed/prefix", Guid.NewGuid(), null, Guid.Empty, null, true);
        LinuxCompatibilityPrefixCleanup.ValidateRecord(record, server, record.Prefix);
        Assert.Throws<InvalidDataException>(() => LinuxCompatibilityPrefixCleanup.ValidateRecord(record, Guid.NewGuid(), record.Prefix));
        Assert.Throws<InvalidDataException>(() => LinuxCompatibilityPrefixCleanup.ValidateRecord(record, server, "/shared/runtime"));
        Assert.Throws<InvalidDataException>(() => LinuxCompatibilityPrefixCleanup.ValidateRecord(record with { ProcessId = 123 }, server, record.Prefix));
        Assert.Throws<InvalidDataException>(() => LinuxCompatibilityPrefixCleanup.ValidateRecord(record with { ProcessId = 123, StartedTicks = 1 }, server, record.Prefix));
    }
}
