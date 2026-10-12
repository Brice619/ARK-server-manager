using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private PresetEntry? presetChangeEntry;
    private bool presetDelete, presetChangeConfirm, presetChangeBusy;
    private string presetChangeText = "";

    private void BeginPresetChange(PresetEntry entry, bool delete)
    {
        presetChangeEntry = entry; presetDelete = delete; presetChangeConfirm = false;
        presetChangeText = delete ? "" : entry.Preset.Name; presetsMessage = ""; Render();
    }

    private void RenderPresetChange()
    {
        var entry = presetChangeEntry!;
        content.Children.Add(ActionButton("← Back to Presets", presetChangeBusy ? null : () =>
        { presetChangeEntry = null; presetsMessage = ""; Render(); }));
        content.Children.Add(Label(presetDelete ? "Delete preset" : presetChangeConfirm ? "Save the new name?" : "Rename preset", 32, true));
        var body = new StackPanel { Spacing = 18 };
        Fact(body, "Preset", entry.Preset.Name); Fact(body, "Edition", entry.Preset.Edition.ToString());
        var input = new TextBox { Text = presetChangeText, PlaceholderText = presetDelete ? "Type the preset name" : "New preset name",
            Width = 500, HorizontalAlignment = HorizontalAlignment.Left, FontSize = 17, Padding = new Thickness(12, 10),
            IsReadOnly = presetChangeBusy || presetChangeConfirm };
        Avalonia.Automation.AutomationProperties.SetName(input, presetDelete ? "Confirm preset name" : "New preset name");
        body.Children.Add(Label(presetDelete ? "Type “" + entry.Preset.Name + "” to confirm deletion." : "Choose a name for this preset.", 16));
        body.Children.Add(input);
        if (presetDelete)
        { var warning = Label("This removes the preset from your library.", 17); warning.Foreground = Amber; body.Children.Add(warning); }
        else if (presetChangeConfirm) Fact(body, "New name", presetChangeText.Trim());
        content.Children.Add(Surface(body));
        if (presetsMessage.Length > 0) { var message = Label(presetsMessage, 16); message.Foreground = Amber; content.Children.Add(message); }
        var actions = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
        var cancel = ActionButton(presetChangeConfirm ? "Keep editing" : "Cancel", presetChangeBusy ? null : () =>
        { if (presetChangeConfirm) presetChangeConfirm = false; else presetChangeEntry = null; Render(); });
        cancel.Margin = new Thickness(0, 0, 16, 10); actions.Children.Add(cancel);
        bool Valid() => presetDelete ? presetChangeText == entry.Preset.Name : !string.IsNullOrWhiteSpace(presetChangeText);
        var commit = ActionButton(presetChangeBusy ? "Saving…" : presetDelete ? "Delete preset" : presetChangeConfirm ? "Yes, save name" : "Save name", () =>
        {
            if (!Valid() || presetChangeBusy) return;
            if (!presetDelete && !presetChangeConfirm) { presetChangeConfirm = true; Render(); return; }
            _ = CommitPresetChangeAsync(entry);
        }, primary: true);
        commit.IsEnabled = Valid() && !presetChangeBusy && (designPreview ? !presetDelete && !presetChangeConfirm : client is not null);
        input.TextChanged += (_, _) =>
        { presetChangeText = input.Text ?? ""; commit.IsEnabled = Valid() && !presetChangeBusy && (designPreview ? !presetDelete && !presetChangeConfirm : client is not null); };
        actions.Children.Add(commit); content.Children.Add(actions);
        if (designPreview) content.Children.Add(Label("Design preview · Library changes are disabled.", 14));
    }

    private async Task CommitPresetChangeAsync(PresetEntry entry)
    {
        if (client is null || designPreview || presetChangeBusy || lifetime.IsCancellationRequested) return;
        presetChangeBusy = true; presetsMessage = ""; Render();
        try
        {
            if (presetDelete)
            {
                var result = await client.CallAsync<System.Text.Json.JsonElement>("presets-delete", new PresetIdRequest(entry.Id), TimeSpan.FromSeconds(8), lifetime.Token);
                if (result.ValueKind != System.Text.Json.JsonValueKind.Object || !result.TryGetProperty("Deleted", out var deleted)
                    || deleted.ValueKind is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False))
                    throw new InvalidDataException("Unexpected deletion response.");
                displayedPresets?.RemoveAll(p => p.Id == entry.Id);
                if (selectedPreset == entry.Id) selectedPreset = null;
                presetsMessage = "Preset removed from your library.";
            }
            else
            {
                var updated = await client.CallAsync<PresetEntry>("presets-rename", new PresetRenameRequest(entry.Id, presetChangeText.Trim()),
                    TimeSpan.FromSeconds(8), lifetime.Token);
                if (updated.Id != entry.Id || updated.Preset is null || updated.Preset.Name != presetChangeText.Trim())
                    throw new InvalidDataException("Unexpected rename response.");
                displayedPresets?.RemoveAll(p => p.Id == entry.Id); displayedPresets?.Add(updated);
                presetsMessage = "Preset renamed.";
            }
            presetChangeEntry = null;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException
            or System.Text.Json.JsonException or OperationCanceledException)
        { presetsMessage = "The change could not be confirmed. Retry or refresh the library to check its current state."; }
        finally
        {
            presetChangeBusy = false;
            if (!lifetime.IsCancellationRequested && settingsPage && applicationCategory == 5) Render();
        }
    }
}
