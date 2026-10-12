using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class LinuxMappedServerFilesTests
{
    private const string Asa = "/srv/ark with spaces/ShooterGame/Binaries/Win64/ArkAscendedServer.exe";
    [Fact]
    public void RepeatedObservationAcceptsSameKernelIdentityAndPayloads()
    {
        var identity = new LinuxProcessIdentity(42, Guid.NewGuid(), 1234, true);
        var payloads = LinuxMappedServerFiles.FindWindowsPayloads($"1000-2000 r-xp 0 08:01 42 {Asa}\n");
        LinuxWineServerObserver.ValidateStable(identity, identity, payloads, payloads);
    }
    [Theory]
    [InlineData("pid")]
    [InlineData("boot")]
    [InlineData("ticks")]
    [InlineData("exited")]
    [InlineData("inode")]
    [InlineData("deleted")]
    [InlineData("missing")]
    public void ChangingKernelIdentityOrLoadedPayloadRefusesObservation(string change)
    {
        var before = new LinuxProcessIdentity(42, Guid.NewGuid(), 1234, true);
        var after = change switch
        {
            "pid" => before with { ProcessId = 43 },
            "boot" => before with { BootId = Guid.NewGuid() },
            "ticks" => before with { StartedTicks = 1235 },
            "exited" => before with { Alive = false },
            _ => before
        };
        var first = LinuxMappedServerFiles.FindWindowsPayloads($"1000-2000 r-xp 0 08:01 42 {Asa}\n");
        IReadOnlyList<LinuxMappedArkPayload> second = change switch
        {
            "inode" => [first[0] with { Inode = 43 }],
            "deleted" => [first[0] with { Deleted = true }],
            "missing" => [],
            _ => first
        };
        Assert.Throws<IOException>(() => LinuxWineServerObserver.ValidateStable(before, after, first, second));
    }
    [Fact]
    public void KernelMappingsPreserveSpacesAndDeduplicateSectionsByLoadedFileIdentity()
    {
        var maps = $"1000-2000 r--p 00000000 08:01 42    {Asa}\n2000-3000 r-xp 00001000 08:01 42    {Asa}\n3000-4000 rw-p 00000000 00:00 0 [heap]\n";
        var payload = Assert.Single(LinuxMappedServerFiles.FindWindowsPayloads(maps));
        Assert.Equal(Asa, payload.Executable); Assert.Equal("/srv/ark with spaces", payload.Directory);
        Assert.Equal(ArkEdition.ASA, payload.Edition); Assert.Equal(42UL, payload.Inode); Assert.False(payload.Deleted);
        Assert.Equal(2, LinuxMappedServerFiles.Parse(maps).Count);
    }
    [Fact]
    public void DeletedLoadedServerIsRetainedAsUncertainEvidenceRatherThanIgnored()
    {
        var payload = Assert.Single(LinuxMappedServerFiles.FindWindowsPayloads($"1000-2000 r-xp 0 08:01 42 {Asa} (deleted)\n"));
        Assert.True(payload.Deleted); Assert.Equal(Asa, payload.Executable);
    }
    [Fact]
    public void SamePathWithDifferentKernelFileIdentityRemainsAmbiguous()
    {
        Assert.Equal(2, LinuxMappedServerFiles.FindWindowsPayloads(
            $"1000-2000 r-xp 0 08:01 42 {Asa}\n2000-3000 r-xp 0 08:01 43 {Asa}\n").Count);
    }
    [Fact]
    public void ReadOnlyMappingAndUnrelatedExecutableDoNotEstablishServerExecution()
    {
        Assert.Empty(LinuxMappedServerFiles.FindWindowsPayloads($"1000-2000 r--p 0 08:01 42 {Asa}\n2000-3000 r-xp 0 08:01 43 /usr/bin/wine\n"));
    }
    [Theory]
    [InlineData("truncated")]
    [InlineData("2000-1000 r-xp 0 08:01 42 /file")]
    [InlineData("1000-2000 rwxp 0 08:01 0 /file")]
    [InlineData("1000-2000 r-xp 0 08:01 42 /escaped\\012file")]
    [InlineData("1000-2000 r-xp 0 08:01 42 /file\n1800-3000 r-xp 0 08:01 42 /file")]
    public void IncompleteAmbiguousOrOverlappingMapsAreRefused(string maps) =>
        Assert.Throws<InvalidDataException>(() => LinuxMappedServerFiles.Parse(maps));
}
