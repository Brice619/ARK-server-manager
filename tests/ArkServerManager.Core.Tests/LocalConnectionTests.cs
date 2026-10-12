using System.Buffers.Binary;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public class LocalConnectionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ark-pipe-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ClientCanDisconnectAndReconnectWithoutStoppingHost()
    {
        using var lifetime = new CancellationTokenSource();
        var requests = 0;
        var server = new LocalManagerServer(root, (request, _) =>
        {
            if (request.Operation == "fail") throw new InvalidOperationException("Rejected operation");
            Interlocked.Increment(ref requests);
            return Task.FromResult<object>(new { Name = "ready" });
        });
        var running = server.RunAsync(lifetime.Token);
        try
        {
            var client = new LocalManagerClient(root);
            Assert.Equal("ready", (await client.CallAsync<Dictionary<string, string>>("ping", null, TimeSpan.FromSeconds(5)))["Name"]);
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.CallAsync<object>("fail", null, TimeSpan.FromSeconds(5)));
            Assert.Equal("ready", (await new LocalManagerClient(root).CallAsync<Dictionary<string, string>>("ping", null, TimeSpan.FromSeconds(5)))["Name"]);
            Assert.Equal(2, requests);
            Assert.False(running.IsCompleted);
            await Assert.ThrowsAsync<IOException>(() => new LocalManagerServer(root, (_, _) => Task.FromResult<object>(new { })).RunAsync(default));
        }
        finally { await lifetime.CancelAsync(); await running; }
    }

    [Fact]
    public async Task RejectsOversizedMessageBeforeReadingItsBody()
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, LocalManagerProtocol.MaximumMessageBytes + 1);
        await using var stream = new MemoryStream(prefix);
        await Assert.ThrowsAsync<InvalidDataException>(() => LocalManagerProtocol.ReadAsync<ManagerRequest>(stream, default));
    }

    [Fact]
    public async Task PresetOperationsPersistAcrossClientReconnectsAndRejectInvalidRequests()
    {
        using var lifetime = new CancellationTokenSource();
        var operations = new PresetManagerOperations(new PresetLibrary(Path.Combine(root, "presets.json")));
        var server = new LocalManagerServer(root, operations.HandleAsync);
        var running = server.RunAsync(lifetime.Token);
        var deadline = TimeSpan.FromSeconds(5);
        var client = new LocalManagerClient(root);
        var preset = new ServerPreset("Preset", ArkEdition.ASA, new()
        { Name = "Server", GamePort = 7777, QueryPort = 27015, RconPort = 32330, Values = new() { ["Option"] = "Value" } }, ["123"]);
        try
        {
            var entry = await client.CallAsync<PresetEntry>("presets-save", new PresetSaveRequest(preset), deadline);
            await new LocalManagerClient(root).CallAsync<PresetEntry>("presets-rename", new PresetRenameRequest(entry.Id, "Renamed"), deadline);
            var exported = await client.CallAsync<PresetExportDocument>("presets-export", new PresetIdRequest(entry.Id), deadline);
            Assert.Equal("Renamed", exported.Preset.Name);
            var preview = await client.CallAsync<ServerPreset>("presets-import-preview", exported, deadline);
            Assert.Equal("Value", preview.Settings.Values["Option"]);
            Assert.Single(await client.CallAsync<List<PresetEntry>>("presets-list", null, deadline));
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.CallAsync<object>("presets-save", new { Preset = (object?)null }, deadline));
            Assert.Single(await client.CallAsync<List<PresetEntry>>("presets-list", null, deadline));
            await client.CallAsync<object>("presets-delete", new PresetIdRequest(entry.Id), deadline);
            Assert.Empty(await client.CallAsync<List<PresetEntry>>("presets-list", null, deadline));
        }
        finally { await lifetime.CancelAsync(); await running; }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
