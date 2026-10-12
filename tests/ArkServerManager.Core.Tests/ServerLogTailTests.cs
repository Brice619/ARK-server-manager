using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class ServerLogTailTests
{
    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    public async Task RegisteredLogTailIsBoundedAndRedactsCurrentAndRecordedPasswordAssignments(string format)
    {
        var root = Path.Combine(Path.GetTempPath(), "ark-log-tail-" + Guid.NewGuid().ToString("N"));
        try
        {
            var binary = Path.Combine(root, "ShooterGame", "Binaries", "Win64", "ShooterGameServer.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(binary)!); await File.WriteAllTextAsync(binary, "inert");
            var config = Path.Combine(root, "ShooterGame", "Saved", "Config", "WindowsServer"); Directory.CreateDirectory(config);
            await File.WriteAllTextAsync(Path.Combine(config, "GameUserSettings.ini"), "[ServerSettings]\nServerAdminPassword=current-secret\nServerPassword=join-secret\n");
            var repository = new CatalogRepository(Path.Combine(root, "catalog.json")); var id = Guid.NewGuid();
            await repository.ChangeAsync(c => c.AddServer(new(id, ArkEdition.ASE, "TheIsland", root, new() { Name = "Fixture", GamePort = 7777, QueryPort = 27015, RconPort = 32330 })));
            var logs = Path.Combine(root, "logs"); Directory.CreateDirectory(logs);
            var path = Path.Combine(logs, "console.log");
            var encoding = format == "utf16le" ? System.Text.Encoding.Unicode : format == "utf16be" ? System.Text.Encoding.BigEndianUnicode : System.Text.Encoding.UTF8;
            await File.WriteAllTextAsync(path, new string('x', 70000) + "\ncurrent-secret join-secret\nServerAdminPassword=previous-secret?Port=7777\nlatest line café\n", encoding);
            var before = await File.ReadAllTextAsync(path);
            var result = await ServerLogTail.ReadAsync(repository, id);
            Assert.Equal(id, result.ServerId); Assert.True(result.Lines.Count <= 500);
            var text = string.Join('\n', result.Lines);
            Assert.Contains("latest line café", text); Assert.DoesNotContain("current-secret", text);
            Assert.DoesNotContain("join-secret", text); Assert.DoesNotContain("previous-secret", text);
            Assert.DoesNotContain(new string('x', 100), text);
            Assert.Equal(before, await File.ReadAllTextAsync(path));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
