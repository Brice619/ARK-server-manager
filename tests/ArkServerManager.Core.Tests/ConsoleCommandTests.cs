using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Core.Tests;

public sealed class ConsoleCommandTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("ListPlayers\nDoExit")]
    [InlineData("ListPlayers\rDoExit")]
    [InlineData("ListPlayers\0DoExit")]
    public void RejectsEmptyAndControlCharacterCommands(string command)
        => Assert.Throws<ArgumentException>(() => ConsoleCommandManagerOperations.ValidateCommand(command));

    [Fact]
    public void CommandLimitsRespectActualUtf8PacketSize()
    {
        ConsoleCommandManagerOperations.ValidateCommand("ListPlayers");
        ConsoleCommandManagerOperations.ValidateCommand("Broadcast 玩家");
        Assert.Throws<ArgumentException>(() => ConsoleCommandManagerOperations.ValidateCommand(new string('x', 2049)));
        Assert.Throws<ArgumentException>(() => ConsoleCommandManagerOperations.ValidateCommand(new string('界', 2048)));
    }

    [Fact]
    public async Task ActivityJournalDoesNotPersistCommandResponseContents()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ark-command-activity-" + Guid.NewGuid().ToString("N"));
        try
        {
            var response = new ServerConsoleResponse(Guid.NewGuid(), DateTimeOffset.UtcNow, "private response content");
            Assert.DoesNotContain(response.Response, response.ToString());
            Assert.DoesNotContain("private command", new ServerConsoleCommand(response.ServerId, "private command").ToString());
            var journal = new ActivityJournal(Path.Combine(folder, "activity.jsonl"));
            await BackgroundActivityRecorder.RecordAsync(journal, new(Guid.NewGuid(), "server-console-command", BackgroundOperationState.Succeeded,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, JsonSerializer.SerializeToElement(response), null));
            var entries = await journal.ReadRecentAsync(10);
            Assert.DoesNotContain(response.Response, JsonSerializer.Serialize(entries));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
