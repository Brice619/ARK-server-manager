using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private void RenderPresetImport()
    {
        var preset = importPreview!;
        content.Children.Add(ActionButton("← Back to Presets", presetImportBusy ? null : () =>
        { importPreview = null; presetEditEntry = null; importConfirm = false; presetsMessage = ""; Render(); }));
        content.Children.Add(Label(importConfirm ? "Save preset?" : presetEditEntry is not null ? "Review preset changes" : presetFromServer ? "Review new preset" : "Review imported preset", 32, true));
        var subtitle = Label(importConfirm ? presetEditEntry is null ? "This adds a new preset to your library." : "This updates the selected preset in your library." : "Check the contents before saving.", 17);
        subtitle.Foreground = Muted; content.Children.Add(subtitle);
        var body = new StackPanel { Spacing = 16 };
        Fact(body, "Name", preset.Name); Fact(body, "Edition", preset.Edition.ToString());
        Fact(body, "Server name", preset.Settings.Name);
        Fact(body, "Maximum players", preset.Settings.MaxPlayers.ToString());
        Fact(body, "Contents", preset.Settings.Values.Count + " configuration entries · " + preset.Mods.Count + " mods");
        body.Children.Add(Label("This saves a preset. It does not apply settings to any server.", 16));
        content.Children.Add(Surface(body));
        if (presetsMessage.Length > 0) { var message = Label(presetsMessage, 16); message.Foreground = Amber; content.Children.Add(message); }
        var mods = new StackPanel { Spacing = 8 };
        for (var i = 0; i < preset.Mods.Count; i++) mods.Children.Add(Label((i + 1) + ". " + preset.Mods[i], 16));
        if (preset.Mods.Count == 0) mods.Children.Add(Label("No mods recorded", 16));
        content.Children.Add(new Expander { Header = "View mod load order", Content = Surface(mods) });
        var entries = new StackPanel { Spacing = 8 };
        foreach (var setting in preset.Settings.Values)
            Fact(entries, setting.Key, setting.Key.Contains("Password", StringComparison.OrdinalIgnoreCase) ? "Password stored" : setting.Value);
        if (preset.Settings.Values.Count == 0) entries.Children.Add(Label("No configuration entries recorded", 16));
        content.Children.Add(new Expander { Header = "View configuration entries", Content = Surface(entries) });
        var actions = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
        if (importConfirm)
        {
            var back = ActionButton("Keep reviewing", presetImportBusy ? null : () => { importConfirm = false; Render(); });
            back.Margin = new Thickness(0, 0, 16, 10); actions.Children.Add(back);
            actions.Children.Add(ActionButton(presetImportBusy ? "Saving…" : presetEditEntry is null ? "Yes, save as a new preset" : "Yes, save changes", presetImportBusy ? null
                : () => { _ = SaveImportedPresetAsync(); }, true));
        }
        else
        {
            if (presetEditEntry is not null) actions.Children.Add(ActionButton("Keep editing", () =>
            { importPreview = null; presetEditorOpen = true; Render(); }));
            actions.Children.Add(ActionButton(presetEditEntry is null ? "Save as a new preset" : "Save changes", () => { importConfirm = true; Render(); }, true));
        }
        content.Children.Add(actions);
    }

    private async Task ChoosePresetImportAsync()
    {
        if (client is null || designPreview || presetImportBusy || presetsLoading || presetExportBusy || lifetime.IsCancellationRequested) return;
        presetImportBusy = true; presetsMessage = "";
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose an ARK preset", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("ARK preset") { Patterns = ["*.json"] }]
            });
            if (files.Count == 0 || lifetime.IsCancellationRequested) return;
            using var file = files[0];
            await using var stream = await file.OpenReadAsync();
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            while (true)
            {
                var count = await stream.ReadAsync(chunk, lifetime.Token);
                if (count == 0) break;
                if (buffer.Length + count > 4 * 1024 * 1024) throw new InvalidDataException("Preset exceeds the size limit.");
                buffer.Write(chunk, 0, count);
            }
            importPreview = PresetLibrary.ReadImport(buffer.ToArray());
            presetEditEntry = null;
            presetFromServer = false;
            presetImportId = Guid.NewGuid(); importConfirm = false;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
            or NotSupportedException or ArgumentException)
        { presetsMessage = "Could not read this preset. Choose a valid ARK preset file up to 4 MB."; }
        finally
        {
            presetImportBusy = false;
            if (!lifetime.IsCancellationRequested && settingsPage && applicationCategory == 5) Render();
        }
    }

    private async Task SaveImportedPresetAsync()
    {
        if (client is null || importPreview is null || !importConfirm || presetImportBusy || lifetime.IsCancellationRequested) return;
        presetImportBusy = true; presetsMessage = ""; Render();
        try
        {
            var saved = presetEditEntry is not null
                ? await client.CallAsync<PresetEntry>("presets-edit", new PresetEditRequest(presetEditEntry, importPreview), TimeSpan.FromSeconds(8), lifetime.Token)
                : await client.CallAsync<PresetEntry>("presets-import", new PresetImportRequest(presetImportId, importPreview), TimeSpan.FromSeconds(8), lifetime.Token);
            if (saved.Id != (presetEditEntry?.Id ?? presetImportId) || saved.Preset is null) throw new InvalidDataException("Unexpected preset save result.");
            displayedPresets ??= []; displayedPresets.RemoveAll(p => p.Id == saved.Id); displayedPresets.Add(saved);
            selectedPreset = saved.Id; importPreview = null; presetEditEntry = null; importConfirm = false;
            presetsMessage = "Preset saved to your library.";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException
            or System.Text.Json.JsonException or OperationCanceledException)
        { presetsMessage = "Saving could not be confirmed. Retry the same change, or return to the library and refresh before editing again."; }
        finally
        {
            presetImportBusy = false;
            if (!lifetime.IsCancellationRequested && settingsPage && applicationCategory == 5) Render();
        }
    }
}
