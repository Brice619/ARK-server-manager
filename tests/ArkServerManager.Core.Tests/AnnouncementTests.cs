using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class AnnouncementTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("first\nsecond")]
    [InlineData("first\rsecond")]
    [InlineData("hidden\0command")]
    [InlineData("tab\tmessage")]
    public void InvalidMessagesCannotReachTheCommandTransport(string message)
        => Assert.Throws<ArgumentException>(() => AnnouncementManagerOperations.ValidateMessage(message));

    [Fact]
    public void BoundsMessagesAndKeepsTheirContentsOutOfDiagnosticStrings()
    {
        AnnouncementManagerOperations.ValidateMessage("Welcome, survivors! 玩家");
        Assert.Throws<ArgumentException>(() => AnnouncementManagerOperations.ValidateMessage(new string('x', 1025)));
        AnnouncementManagerOperations.ValidateMessage(new string('界', 1024));
        Assert.DoesNotContain("private announcement", new ServerAnnouncementRequest(Guid.NewGuid(), "private announcement", true).ToString());
    }
}
