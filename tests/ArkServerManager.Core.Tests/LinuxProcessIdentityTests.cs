using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class LinuxProcessIdentityTests
{
    private static string Stat(string state, string ticks)
        => "123 (package worker ) with spaces) " + state + " " + string.Join(" ", Enumerable.Repeat("0", 18)) + " " + ticks;

    [Fact]
    public void KernelIdentityHandlesProcessNamesWithParenthesesAndPidReuse()
    {
        var boot = Guid.NewGuid();
        var process = LinuxProcessIdentity.Parse(123, boot, Stat("S", "987654"));
        Assert.True(process.Matches(123, boot, 987654));
        Assert.False(process.Matches(123, boot, 987655));
        Assert.False(process.Matches(123, Guid.NewGuid(), 987654));
        Assert.False(process.Matches(124, boot, 987654));
    }

    [Theory]
    [InlineData("Z")]
    [InlineData("X")]
    [InlineData("x")]
    public void ExitedKernelStatesDoNotBlockRecovery(string state)
    {
        var boot = Guid.NewGuid();
        Assert.False(LinuxProcessIdentity.Parse(123, boot, Stat(state, "987654")).Matches(123, boot, 987654));
    }

    [Fact]
    public void UnverifiedIdentityCannotBeTreatedAsAnExitedOwner()
    {
        Assert.Throws<InvalidDataException>(() => LinuxProcessIdentity.Parse(124, Guid.NewGuid(), Stat("S", "987654")));
        Assert.Throws<InvalidDataException>(() => LinuxProcessIdentity.Parse(123, Guid.Empty, Stat("S", "987654")));
        Assert.Throws<InvalidDataException>(() => LinuxProcessIdentity.Parse(123, Guid.NewGuid(), Stat("?", "987654")));
        Assert.Throws<InvalidDataException>(() => LinuxProcessIdentity.Parse(123, Guid.NewGuid(), Stat("S", "invalid")));
    }
}
