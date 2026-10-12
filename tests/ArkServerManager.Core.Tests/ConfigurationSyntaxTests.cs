using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ConfigurationSyntaxTests
{
    [Fact]
    public void AcceptsModKeysArraysCommentsAndValuesContainingEquals()
        => Assert.Empty(ConfigurationSyntax.Check("; comment\r\n[UnknownMod]\r\n+Items=one\r\n+Items=two\r\nOption=a=b\r\nEmpty=\r\n# comment\r\n"));

    [Fact]
    public void ReportsMalformedLinesWithoutIncludingConfigurationValues()
    {
        var findings = ConfigurationSyntax.Check("[ServerSettings\nServerAdminPassword=secret\nmissing equals\n=private\nName=bad\0value\n");
        Assert.Equal(new[] { 1, 3, 4, 5 }, findings.Select(f => f.Line));
        Assert.DoesNotContain("secret", string.Join(" ", findings));
        Assert.DoesNotContain("private", string.Join(" ", findings));
    }
}
