using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class UmuBootstrapTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VerifiedLauncherIsReadWithoutExtractingUpstreamAlias(bool alias)
    {
        var launcher = Encoding.UTF8.GetBytes("fixture launcher");
        var archive = Archive(launcher, alias);
        Assert.Equal(launcher, UmuBootstrap.VerifyAndReadLauncher(archive, Hash(archive), Hash(launcher)));
    }
    [Theory]
    [InlineData("../escape")]
    [InlineData("umu/unexpected")]
    [InlineData("/absolute")]
    public void UnexpectedEntriesAreRefusedEvenWithMatchingArchiveHash(string name)
    {
        var launcher = Encoding.UTF8.GetBytes("fixture launcher");
        var archive = Archive(launcher, false, name);
        Assert.Throws<InvalidDataException>(() => UmuBootstrap.VerifyAndReadLauncher(archive, Hash(archive), Hash(launcher)));
    }
    [Fact]
    public void BothPackageAndLauncherHashesAreRequired()
    {
        var launcher = Encoding.UTF8.GetBytes("fixture launcher");
        var archive = Archive(launcher, false);
        Assert.Throws<InvalidDataException>(() => UmuBootstrap.VerifyAndReadLauncher(archive, new string('0', 64), Hash(launcher)));
        Assert.Throws<InvalidDataException>(() => UmuBootstrap.VerifyAndReadLauncher(archive, Hash(archive), new string('0', 64)));
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static byte[] Archive(byte[] launcher, bool alias, string? extra = null)
    {
        using var bytes = new MemoryStream();
        using (var writer = new TarWriter(bytes, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "umu/"));
            using var content = new MemoryStream(launcher);
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "umu/umu-run") { DataStream = content });
            if (alias) writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "umu/umu_run.py") { LinkName = "umu-run" });
            if (extra is not null)
            {
                using var extraContent = new MemoryStream([1]);
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, extra) { DataStream = extraContent });
            }
        }
        return bytes.ToArray();
    }
}
