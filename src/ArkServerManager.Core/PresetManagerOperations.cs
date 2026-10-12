using System.Text.Json;

namespace ArkServerManager.Core;

public sealed record PresetSaveRequest(ServerPreset Preset, Guid? ExistingId = null);
public sealed record PresetRenameRequest(Guid Id, string Name);
public sealed record PresetIdRequest(Guid Id);
public sealed record PresetImportRequest(Guid ImportId, ServerPreset Preset);
public sealed record PresetEditRequest(PresetEntry Expected, ServerPreset Replacement);

public sealed class PresetManagerOperations(PresetLibrary library)
{
    public async Task<object> HandleAsync(ManagerRequest request, CancellationToken ct)
    {
        switch (request.Operation)
        {
            case "presets-list": return await library.ListAsync(ct);
            case "presets-import":
                var imported = Payload<PresetImportRequest>(request);
                return await library.ImportAsNewAsync(imported.ImportId, imported.Preset, ct);
            case "presets-save":
                var save = Payload<PresetSaveRequest>(request);
                if (save.Preset is null) throw new ArgumentException("Preset data is required.");
                return await library.SaveAsync(save.Preset, save.ExistingId, ct);
            case "presets-edit":
                var edit = Payload<PresetEditRequest>(request);
                return await library.EditReviewedAsync(edit.Expected, edit.Replacement, ct);
            case "presets-rename":
                var rename = Payload<PresetRenameRequest>(request);
                return await library.RenameAsync(rename.Id, rename.Name, ct);
            case "presets-delete": return new { Deleted = await library.DeleteAsync(Payload<PresetIdRequest>(request).Id, ct) };
            case "presets-export": return await library.ExportAsync(Payload<PresetIdRequest>(request).Id, ct);
            case "presets-import-preview":
                if (request.Payload is not JsonElement import) throw new ArgumentException("Preset file data is required.");
                return PresetLibrary.ReadImport(JsonSerializer.SerializeToUtf8Bytes(import));
            default: throw new ArgumentException("Unknown preset operation.");
        }
    }

    private static T Payload<T>(ManagerRequest request) where T : class
    {
        try
        {
            return request.Payload?.Deserialize<T>() ?? throw new ArgumentException("Operation details are required.");
        }
        catch (JsonException error) { throw new ArgumentException("Operation details are invalid.", error); }
    }
}
