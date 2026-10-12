using System.Text.Json;

namespace ArkServerManager.Core;

public sealed record PresetCaptureRequest(ServerDefinition Expected, string Name);

public static class PresetCapture
{
    /// <summary>The Host holds its configuration/file scope for this read-only capture.</summary>
    public static async Task<ServerPreset> ReadAsync(CatalogRepository repository, PresetCaptureRequest request, CancellationToken ct)
    {
        if (request.Expected is null || string.IsNullOrWhiteSpace(request.Name)) throw new ArgumentException("Choose a server and preset name.");
        await RequireUnchanged();
        var user = await ServerConfigurationReader.ReadAsync(repository, new(request.Expected.Id, "GameUserSettings.ini"), ct);
        var game = await ServerConfigurationReader.ReadAsync(repository, new(request.Expected.Id, "Game.ini"), ct);
        if (user.Text is null || user.SourceSHA256 is null) throw new InvalidDataException("The server settings could not be captured.");
        var userCheck = await ServerConfigurationReader.ReadAsync(repository, new(request.Expected.Id, "GameUserSettings.ini"), ct);
        var gameCheck = await ServerConfigurationReader.ReadAsync(repository, new(request.Expected.Id, "Game.ini"), ct);
        if (userCheck.SourceSHA256 != user.SourceSHA256 || gameCheck.SourceSHA256 != game.SourceSHA256)
            throw new InvalidOperationException("The configuration changed during capture. Try again.");
        await RequireUnchanged();
        var preset = new ServerPreset(request.Name.Trim(), request.Expected.Edition, request.Expected.Settings.Copy(), [.. request.Expected.Mods])
            { UserSettings = user.Text, GameSettings = game.Text };
        PresetLibrary.ReadImport(JsonSerializer.SerializeToUtf8Bytes(new PresetExportDocument(1, preset)));
        return preset;

        async Task RequireUnchanged()
        {
            var current = (await repository.ReadAsync(ct)).Servers.SingleOrDefault(s => s.Id == request.Expected.Id);
            if (current is null || JsonSerializer.Serialize(current) != JsonSerializer.Serialize(request.Expected))
                throw new InvalidOperationException("The server changed. Refresh before capturing its preset.");
        }
    }
}
