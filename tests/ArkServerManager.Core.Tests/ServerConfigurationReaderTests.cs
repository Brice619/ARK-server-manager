using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ServerConfigurationReaderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-config-read-" + Guid.NewGuid().ToString("N"));
    private CatalogRepository Repository => new(Path.Combine(root, "catalog.json"));

    [Fact]
    public async Task SteamPackageWithBothPlatformBinariesUsesNativeConfiguration()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;
        var id = await Register("Windows");
        var linux = Path.Combine(root, "server", "ShooterGame", "Binaries", "Linux", "ShooterGameServer");
        Directory.CreateDirectory(Path.GetDirectoryName(linux)!); await File.WriteAllTextAsync(linux, "linux fixture");
        foreach (var platform in new[] { "Windows", "Linux" })
        {
            var config = Path.Combine(root, "server", "ShooterGame", "Saved", "Config", platform + "Server");
            Directory.CreateDirectory(config); await File.WriteAllTextAsync(Path.Combine(config, "GameUserSettings.ini"), platform);
        }
        var native = OperatingSystem.IsWindows() ? "Windows" : "Linux";
        var result = await ServerConfigurationReader.ReadAsync(Repository, new(id, "GameUserSettings.ini"));
        Assert.Equal(native, result.Platform); Assert.Equal(native, result.Text);
    }

    [Theory]
    [InlineData("Windows")]
    [InlineData("Linux")]
    public async Task CreatesMissingGameConfigurationWithoutReplacingExistingOrRacingFile(string platform)
    {
        var id = await Register(platform);
        var config = Path.Combine(root, "server", "ShooterGame", "Saved", "Config", platform + "Server");
        Directory.CreateDirectory(config);
        var path = Path.Combine(config, "Game.ini");
        var userPath = Path.Combine(config, "GameUserSettings.ini");
        await File.WriteAllTextAsync(userPath, "existing settings");
        await Assert.ThrowsAsync<IOException>(() => ServerConfigurationWriter.CreateGameAsync(Repository, id, "new",
            (_, _, _) => throw new IOException("Running server refused")));
        Assert.False(File.Exists(path));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ServerConfigurationWriter.CreateGameAsync(Repository, id, "new",
            async (_, change, ct) => { await File.WriteAllTextAsync(path, "external editor", ct); await change(ct); }));
        Assert.Equal("external editor", await File.ReadAllTextAsync(path));
        File.Delete(path);
        await ServerConfigurationWriter.CreateGameAsync(Repository, id, "[ModInstaller]\nModIDS=123\n",
            (_, change, ct) => change(ct));
        Assert.Contains("ModIDS=123", await File.ReadAllTextAsync(path));
        Assert.Equal("existing settings", await File.ReadAllTextAsync(userPath));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ServerConfigurationWriter.CreateGameAsync(Repository, id, "overwrite",
            (_, change, ct) => change(ct)));
        Assert.Contains("ModIDS=123", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(config, "*.tmp"));
    }

    private async Task<Guid> Register(string platform)
    {
        var directory = Path.Combine(root, "server");
        var binary = Path.Combine(directory, "ShooterGame", "Binaries", platform == "Windows" ? "Win64" : "Linux",
            platform == "Windows" ? "ShooterGameServer.exe" : "ShooterGameServer");
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        await File.WriteAllTextAsync(binary, "non-executable fixture");
        var id = Guid.NewGuid();
        await Repository.ChangeAsync(c => c.AddServer(new(id, ArkEdition.ASE, "TheIsland", directory,
            new() { Name = "Fixture", GamePort = 7777, QueryPort = 27015, RconPort = 32330 })));
        return id;
    }

    [Theory]
    [InlineData("Windows")]
    [InlineData("Linux")]
    public async Task ReadsRegisteredPlatformExactlyAndDoesNotCreateMissingFile(string platform)
    {
        var id = await Register(platform);
        var config = Path.Combine(root, "server", "ShooterGame", "Saved", "Config", platform + "Server");
        Directory.CreateDirectory(config);
        var original = "; keep comment\r\n[Unknown]\r\nThing=one\r\nThing=two\r\n";
        var path = Path.Combine(config, "GameUserSettings.ini");
        await File.WriteAllTextAsync(path, original);
        var result = await ServerConfigurationReader.ReadAsync(Repository, new(id, "GameUserSettings.ini"));
        Assert.Equal(platform, result.Platform);
        Assert.Equal(original, result.Text);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path)))
            .ToLowerInvariant(), result.SourceSHA256);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        var missing = await ServerConfigurationReader.ReadAsync(Repository, new(id, "Game.ini"));
        Assert.Null(missing.Text);
        Assert.Null(missing.SourceSHA256);
        Assert.False(File.Exists(Path.Combine(config, "Game.ini")));
    }

    [Fact]
    public async Task WelcomeManagerRequiresConfirmationAndWritesOnceForReconnectedRequest()
    {
        var id = await Register("Windows");
        var config = Path.Combine(root, "server", "ShooterGame", "Saved", "Config", "WindowsServer");
        Directory.CreateDirectory(config);
        var path = Path.Combine(config, "GameUserSettings.ini");
        await File.WriteAllTextAsync(path, "; keep\r\n[ServerSettings]\r\nServerAdminPassword=private\r\n");
        var source = await ServerConfigurationReader.ReadAsync(Repository, new(id, "GameUserSettings.ini"));
        await using var jobs = new BackgroundOperations(8, 1);
        var writes = 0;
        var manager = new ServerWelcomeManagerOperations(Repository, jobs, async (_, write, ct) =>
        { Interlocked.Increment(ref writes); await write(ct); });
        var edit = new ServerWelcomeEdit(id, source.SourceSHA256!, "Welcome aboard", 35, false);
        var request = new ManagerRequest(LocalManagerProtocol.Version, Guid.NewGuid(), "server-welcome-save-start",
            System.Text.Json.JsonSerializer.SerializeToElement(edit));
        Assert.Throws<ArgumentException>(() => manager.Start(request));
        Assert.Equal(0, writes);
        request = request with { Payload = System.Text.Json.JsonSerializer.SerializeToElement(edit with { Confirmed = true }) };
        var first = manager.Start(request);
        Assert.Equal(first.Id, manager.Start(request).Id);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (jobs.Find(request.Id)!.FinishedAt is null) await Task.Delay(5, deadline.Token);
        Assert.Equal(BackgroundOperationState.Succeeded, jobs.Find(request.Id)!.State);
        Assert.Equal(1, writes);
        var saved = new IniDocument(await File.ReadAllTextAsync(path));
        Assert.Equal(["Welcome aboard"], saved.GetValues("MessageOfTheDay", "Message"));
        Assert.Equal(["35"], saved.GetValues("MessageOfTheDay", "Duration"));
        Assert.Equal(["private"], saved.GetValues("ServerSettings", "ServerAdminPassword"));
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8-bom")]
    [InlineData("utf16-le")]
    [InlineData("utf16-be")]
    [InlineData("utf32-le")]
    [InlineData("utf32-be")]
    public async Task WriterPreservesEncodingAndRequiresStoppedScope(string encodingName)
    {
        var id = await Register("Windows");
        var config = Path.Combine(root, "server", "ShooterGame", "Saved", "Config", "WindowsServer");
        Directory.CreateDirectory(config);
        var path = Path.Combine(config, "Game.ini");
        System.Text.Encoding encoding = encodingName switch
        {
            "utf8" => new System.Text.UTF8Encoding(false, true),
            "utf8-bom" => new System.Text.UTF8Encoding(true, true),
            "utf16-le" => new System.Text.UnicodeEncoding(false, true, true),
            "utf16-be" => new System.Text.UnicodeEncoding(true, true, true),
            "utf32-le" => new System.Text.UTF32Encoding(false, true, true),
            _ => new System.Text.UTF32Encoding(true, true, true)
        };
        await File.WriteAllTextAsync(path, "; Original\r\n[Unknown]\r\nValue=1\r\n", encoding);
        string? accessBefore = null;
        if (OperatingSystem.IsWindows())
        {
            var access = new FileInfo(path).GetAccessControl(System.Security.AccessControl.AccessControlSections.Access);
            access.SetAccessRuleProtection(true, false);
            access.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                System.Security.Principal.WindowsIdentity.GetCurrent().User!, System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(access);
            accessBefore = new FileInfo(path).GetAccessControl(System.Security.AccessControl.AccessControlSections.Access)
                .GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access);
        }
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var snapshot = await ServerConfigurationReader.ReadAsync(Repository, new(id, "Game.ini"));
        const string replacement = "; Retained comment\r\n[Unknown]\r\nValue=2\r\n";
        var edit = new ServerConfigurationEdit(id, "Game.ini", snapshot.SourceSHA256!, replacement);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ServerConfigurationWriter.WriteAsync(Repository, edit,
            (_, _, _) => Task.CompletedTask));
        Assert.Equal(snapshot.Text, await File.ReadAllTextAsync(path));
        await ServerConfigurationWriter.WriteAsync(Repository, edit, async (server, write, token) =>
        { Assert.Equal(id, server.Id); await write(token); });
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal(encoding.GetPreamble().Concat(encoding.GetBytes(replacement)).ToArray(), bytes);
        Assert.Equal(replacement, await File.ReadAllTextAsync(path));
        if (OperatingSystem.IsWindows())
            Assert.Equal(accessBefore, new FileInfo(path).GetAccessControl(System.Security.AccessControl.AccessControlSections.Access)
                .GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access));
        if (OperatingSystem.IsLinux()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Empty(Directory.GetFiles(config, "*.tmp"));
    }

    [Fact]
    public async Task WriterRejectsOutsideEditBeforeStoppingAndInsideStoppedScope()
    {
        var id = await Register("Windows");
        var config = Path.Combine(root, "server", "ShooterGame", "Saved", "Config", "WindowsServer");
        Directory.CreateDirectory(config);
        var path = Path.Combine(config, "Game.ini");
        await File.WriteAllTextAsync(path, "; Original");
        var snapshot = await ServerConfigurationReader.ReadAsync(Repository, new(id, "Game.ini"));
        var edit = new ServerConfigurationEdit(id, "Game.ini", snapshot.SourceSHA256!, "; App edit");
        await File.WriteAllTextAsync(path, "; Outside edit");
        var scopeCalls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ServerConfigurationWriter.WriteAsync(Repository, edit,
            (_, _, _) => { scopeCalls++; return Task.CompletedTask; }));
        Assert.Equal(0, scopeCalls);
        Assert.Equal("; Outside edit", await File.ReadAllTextAsync(path));
        await File.WriteAllTextAsync(path, "; Original");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ServerConfigurationWriter.WriteAsync(Repository, edit,
            async (_, write, token) => { await File.WriteAllTextAsync(path, "; New outside edit", token); await write(token); }));
        Assert.Equal("; New outside edit", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(config, "*.tmp"));
    }

    [Fact]
    public async Task SourceFingerprintTracksEncodingBytesEvenWhenEditorTextIsIdentical()
    {
        var id = await Register("Windows");
        var config = Path.Combine(root, "server", "ShooterGame", "Saved", "Config", "WindowsServer");
        Directory.CreateDirectory(config);
        var path = Path.Combine(config, "Game.ini");
        const string text = "; Same editor text\r\n[Example]\r\nValue=1\r\n";
        await File.WriteAllTextAsync(path, text, new System.Text.UTF8Encoding(true));
        var first = await ServerConfigurationReader.ReadAsync(Repository, new(id, "Game.ini"));
        await File.WriteAllTextAsync(path, text, new System.Text.UTF8Encoding(false));
        var second = await ServerConfigurationReader.ReadAsync(Repository, new(id, "Game.ini"));
        Assert.Equal(first.Text, second.Text);
        Assert.NotEqual(first.SourceSHA256, second.SourceSHA256);
    }

    [Fact]
    public async Task RejectsUnregisteredServerAndArbitraryFileNames()
    {
        var id = await Register("Windows");
        await Assert.ThrowsAsync<ArgumentException>(() => ServerConfigurationReader.ReadAsync(Repository, new(Guid.NewGuid(), "Game.ini")));
        await Assert.ThrowsAsync<ArgumentException>(() => ServerConfigurationReader.ReadAsync(Repository, new(id, "../../secret.txt")));
    }

    [Fact]
    public async Task MixedPlatformPackageDoesNotCreateMissingNativeConfiguration()
    {
        var id = await Register("Windows");
        var linux = Path.Combine(root, "server", "ShooterGame", "Binaries", "Linux", "ShooterGameServer");
        Directory.CreateDirectory(Path.GetDirectoryName(linux)!);
        await File.WriteAllTextAsync(linux, "non-executable fixture");
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        { await Assert.ThrowsAsync<InvalidDataException>(() => ServerConfigurationReader.ReadAsync(Repository, new(id, "Game.ini"))); return; }
        var result = await ServerConfigurationReader.ReadAsync(Repository, new(id, "Game.ini"));
        Assert.Equal(OperatingSystem.IsWindows() ? "Windows" : "Linux", result.Platform);
        Assert.Null(result.Text);
        Assert.False(Directory.Exists(Path.Combine(root, "server", "ShooterGame", "Saved")));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
