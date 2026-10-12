using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class StableCaptureCoordinatorTests
{
    private static IReadOnlyList<StableCaptureEntry> Inventory(string revision = "original")
        => [new("", true, 0, "directory"), new("world.ark", false, 10, revision)];

    [Fact]
    public async Task EverySourceBracketsEveryCopy()
    {
        var events = new List<string>();
        var result = await StableCaptureCoordinator.CopyAsync([new("a", "capture-a", null), new("b", "capture-b", null)],
            (source, _) => { events.Add("read:" + source.SourceDirectory); return Task.FromResult(Inventory()); },
            (source, entry, _) => { if (!entry.IsDirectory) events.Add("copy:" + source.SourceDirectory); return Task.CompletedTask; });
        Assert.Equal(["read:a", "read:b", "copy:a", "copy:b", "read:a", "read:b"], events);
        Assert.NotEqual(Guid.Empty, result.Id);
    }

    [Fact]
    public async Task ChangeToFirstMapWhileSecondMapCopiesRefusesWholeCapture()
    {
        var changed = false;
        await Assert.ThrowsAsync<IOException>(() => StableCaptureCoordinator.CopyAsync(
            [new("a", "capture-a", null), new("b", "capture-b", null)],
            (source, _) => Task.FromResult(Inventory(source.SourceDirectory == "a" && changed ? "changed" : "original")),
            (source, entry, _) => { if (source.SourceDirectory == "b" && !entry.IsDirectory) changed = true; return Task.CompletedTask; }));
    }

    [Fact]
    public async Task InvalidInventoryNeverStartsCopying()
    {
        var copied = false;
        await Assert.ThrowsAsync<InvalidDataException>(() => StableCaptureCoordinator.CopyAsync([new("a", "capture-a", null)],
            (_, _) => Task.FromResult<IReadOnlyList<StableCaptureEntry>>([new("world.ark", false, 10, "x")]),
            (_, _, _) => { copied = true; return Task.CompletedTask; }));
        Assert.False(copied);
    }
}
