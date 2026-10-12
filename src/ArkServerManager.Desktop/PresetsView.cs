using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using System.Text.Json;
using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private List<PresetEntry>? displayedPresets;
    private Guid? selectedPreset;
    private bool presetsLoading, presetsRequested;
    private string presetFilter = "", presetsMessage = "";
    private int presetEditionFilter;
    private bool presetExportBusy;
    private ServerPreset? importPreview;
    private Guid presetImportId;
    private bool presetImportBusy, importConfirm;
    private bool presetCreating, presetFromServer;

    private static Control EditionBadge(ArkEdition edition)
    {
        var color = edition == ArkEdition.ASA ? Brush.Parse("#75BCF6") : Amber;
        return new Border { BorderBrush = color, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 5), HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock { Text = edition.ToString(), Foreground = color, FontSize = 15 } };
    }

    private void RenderPresets()
    {
        if (presetEditorOpen) { RenderPresetEditor(); return; }
        if (presetCreating) { RenderPresetCreation(); return; }
        if (presetChangeEntry is not null) { RenderPresetChange(); return; }
        if (importPreview is not null) { RenderPresetImport(); return; }
        if (!presetsRequested)
        {
            presetsRequested = true;
            if (designPreview)
                displayedPresets = [new(Guid.NewGuid(), new("Solo example", ArkEdition.ASA, new ServerSettings(), [])),
                    new(Guid.NewGuid(), new("Cluster example", ArkEdition.ASE, new ServerSettings(), []))];
            else if (client is not null) _ = LoadPresetsAsync();
        }
        if (selectedPreset is Guid id && displayedPresets?.FirstOrDefault(p => p.Id == id) is { } entry)
        { RenderPresetDetails(entry); return; }
        content.Children.Add(ActionButton("← App Settings", () => { applicationCategory = 0; Render(); }));
        content.Children.Add(Label("Presets", 36, true));
        var subtitle = Label("Save and reuse server settings and mod configurations.", 18);
        subtitle.Foreground = Muted; content.Children.Add(subtitle);
        var toolbar = new WrapPanel { Margin = new Thickness(0, 22, 0, 20) };
        var editions = new ComboBox { ItemsSource = new[] { "All editions", "ASA", "ASE" }, SelectedIndex = presetEditionFilter,
            Width = 210, FontSize = 16, Margin = new Thickness(0, 0, 16, 12) };
        Avalonia.Automation.AutomationProperties.SetName(editions, "Filter preset edition");
        var search = new TextBox { Text = presetFilter, PlaceholderText = "Search presets…", Width = 390,
            FontSize = 16, Padding = new Thickness(12, 10), Margin = new Thickness(0, 0, 24, 12) };
        Avalonia.Automation.AutomationProperties.SetName(search, "Search presets");
        toolbar.Children.Add(editions); toolbar.Children.Add(search);
        toolbar.Children.Add(ActionButton(presetsLoading ? "Loading…" : "Refresh", client is null || presetsLoading ? null
            : () => { _ = LoadPresetsAsync(refreshView: true); }));
        var import = ActionButton("Import preset", client is null || designPreview || presetImportBusy || presetsLoading || presetExportBusy || !StorageProvider.CanOpen
            ? null : () => { _ = ChoosePresetImportAsync(); }); import.Margin = new Thickness(14, 0, 14, 12); toolbar.Children.Add(import);
        toolbar.Children.Add(ActionButton("+ Create preset", client is null || presetsLoading || presetImportBusy || presetExportBusy ? null
            : () => { presetCreating = true; presetsMessage = ""; Render(); }, primary: true)); content.Children.Add(toolbar);
        if (presetsMessage.Length > 0) { var message = Label(presetsMessage, 16); message.Foreground = Amber; content.Children.Add(message); }
        var rows = new StackPanel { Spacing = 18 };
        void Populate()
        {
            rows.Children.Clear();
            if (displayedPresets is null)
            {
                rows.Children.Add(Surface(Label(presetsLoading ? "Loading your preset library…" : "Preset library unavailable. Connect to the manager and refresh.", 17)));
                return;
            }
            var entries = displayedPresets.Where(p => p.Preset.Name.Contains(presetFilter, StringComparison.OrdinalIgnoreCase)
                && (presetEditionFilter == 0 || p.Preset.Edition == (presetEditionFilter == 1 ? ArkEdition.ASA : ArkEdition.ASE))).ToArray();
            foreach (var preset in entries)
            {
                var row = new WrapPanel();
                var identity = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24, Width = 470, Margin = new Thickness(0, 0, 18, 12) };
                identity.Children.Add(new Border { Width = 100, Height = 100, CornerRadius = new CornerRadius(5), Background = Brush.Parse("#101C26"),
                    Child = VectorIcon(GearIcon, Muted, 42) });
                var labels = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
                labels.Children.Add(Label(preset.Preset.Name, 24, true)); labels.Children.Add(EditionBadge(preset.Preset.Edition));
                identity.Children.Add(labels); row.Children.Add(identity);
                var contents = new StackPanel { Spacing = 10, Width = 370, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 18, 12) };
                contents.Children.Add(Label("Settings and mods", 20, true));
                var summary = Label(preset.Preset.Settings.Values.Count + " configuration entries · " + preset.Preset.Mods.Count + " mods", 16);
                summary.Foreground = Muted; contents.Children.Add(summary); row.Children.Add(contents);
                var more = ActionButton("••• More actions", () => { });
                var menu = new ContextMenu();
                var rename = new MenuItem { Header = "Rename", IsEnabled = !presetsLoading && !presetExportBusy };
                rename.Click += (_, _) => BeginPresetChange(preset, delete: false); menu.Items.Add(rename);
                var edit = new MenuItem { Header = "Edit", IsEnabled = !presetsLoading && !presetExportBusy && client is not null };
                edit.Click += (_, _) => BeginPresetEdit(preset); menu.Items.Add(edit);
                var delete = new MenuItem { Header = "Delete", IsEnabled = !presetsLoading && !presetExportBusy };
                delete.Click += (_, _) => BeginPresetChange(preset, delete: true); menu.Items.Add(delete);
                var export = new MenuItem { Header = "Export", IsEnabled = CanExportPreset };
                export.Click += async (_, _) => await ExportPresetAsync(preset);
                menu.Items.Add(export);
                more.Click += (_, _) => menu.Open(more); more.VerticalAlignment = VerticalAlignment.Center;
                more.Margin = new Thickness(0, 0, 16, 12); row.Children.Add(more);
                var view = ActionButton("View  ›", () => { selectedPreset = preset.Id; Render(); });
                view.VerticalAlignment = VerticalAlignment.Center; view.Margin = new Thickness(0, 0, 0, 12); row.Children.Add(view);
                rows.Children.Add(Surface(row, new Thickness(24, 20)));
            }
            if (entries.Length == 0)
            {
                var empty = new StackPanel { Spacing = 12 };
                empty.Children.Add(Label(displayedPresets.Count == 0 ? "No presets saved" : "No matching presets", 24, true));
                empty.Children.Add(Label(displayedPresets.Count == 0 ? "Your saved presets will appear here." : "Try another name or edition.", 16));
                rows.Children.Add(Surface(empty));
            }
        }
        search.TextChanged += (_, _) => { presetFilter = search.Text ?? ""; Populate(); };
        editions.SelectionChanged += (_, _) => { presetEditionFilter = Math.Max(0, editions.SelectedIndex); Populate(); };
        Populate(); content.Children.Add(rows);
        var note = Label(designPreview ? "Illustrative presets · These examples are not saved or approved defaults."
            : "Export removes join and admin passwords. Use a server’s More Actions menu to review and apply a preset.", 13);
        note.Foreground = Muted; content.Children.Add(note);
    }

    private async Task LoadPresetsAsync(bool refreshView = false)
    {
        if (client is null || presetsLoading || lifetime.IsCancellationRequested) return;
        presetsLoading = true; presetsMessage = "";
        if (refreshView) Render();
        try
        {
            var entries = await client.CallAsync<List<PresetEntry>>("presets-list", null, TimeSpan.FromSeconds(8), lifetime.Token);
            if (entries is null || entries.Any(p => p is null || p.Id == Guid.Empty || p.Preset is null
                || p.Preset.Settings is null || p.Preset.Settings.Values is null || p.Preset.Mods is null
                || string.IsNullOrWhiteSpace(p.Preset.Name) || !Enum.IsDefined(p.Preset.Edition))
                || entries.Select(p => p.Id).Distinct().Count() != entries.Count)
                throw new InvalidDataException("Unexpected preset library.");
            foreach (var entry in entries) entry.Preset.Settings.Validate();
            displayedPresets = entries;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
        catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException
            or InvalidOperationException or System.Text.Json.JsonException or ArgumentException)
        { presetsMessage = displayedPresets is null ? "Could not load presets. Refresh to try again." : "Could not refresh presets. Showing the last loaded library."; }
        finally
        {
            presetsLoading = false;
            if (!lifetime.IsCancellationRequested && (settingsPage && applicationCategory == 5 || creationPage && creationStep == 3)) Render();
        }
    }

    private void RenderPresetDetails(PresetEntry entry)
    {
        content.Children.Add(ActionButton("← Back to Presets", () => { selectedPreset = null; Render(); }));
        content.Children.Add(Label(entry.Preset.Name, 32, true)); content.Children.Add(EditionBadge(entry.Preset.Edition));
        if (presetsMessage.Length > 0) content.Children.Add(Label(presetsMessage, 16));
        var body = new StackPanel { Spacing = 16 }; body.Children.Add(Label("Settings", 24, true));
        Fact(body, "Maximum players", entry.Preset.Settings.MaxPlayers.ToString());
        Fact(body, "Configuration entries", entry.Preset.Settings.Values.Count.ToString());
        body.Children.Add(Label("Passwords and raw setting values are not displayed in this summary.", 15));
        content.Children.Add(Surface(body));
        var mods = new StackPanel { Spacing = 12 }; mods.Children.Add(Label("Mod load order", 24, true));
        if (entry.Preset.Mods.Count == 0) mods.Children.Add(Label("No mods recorded", 16));
        for (var i = 0; i < entry.Preset.Mods.Count; i++) mods.Children.Add(Label((i + 1) + ".  " + entry.Preset.Mods[i], 16));
        content.Children.Add(new Expander { Header = "View mods (" + entry.Preset.Mods.Count + ")", Content = Surface(mods) });
        content.Children.Add(ActionButton("Edit preset", client is null || presetExportBusy ? null : () => BeginPresetEdit(entry)));
        content.Children.Add(ActionButton(presetExportBusy ? "Exporting…" : "Export preset", CanExportPreset
            ? () => { _ = ExportPresetAsync(entry); } : null));
        var note = Label("Exported presets automatically exclude join and admin passwords.", 14);
        note.Foreground = Muted; content.Children.Add(note);
    }

    private bool CanExportPreset => client is not null && !designPreview && !presetExportBusy && StorageProvider.CanSave;

    private async Task ExportPresetAsync(PresetEntry entry)
    {
        if (!CanExportPreset || lifetime.IsCancellationRequested) return;
        presetExportBusy = true; presetsMessage = ""; Render();
        try
        {
            var exported = await client!.CallAsync<PresetExportDocument>("presets-export", new PresetIdRequest(entry.Id),
                TimeSpan.FromSeconds(8), lifetime.Token);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(exported);
            var validated = PresetLibrary.ReadImport(bytes);
            if (validated.Settings.Values.Keys.Any(PresetLibrary.IsPasswordSetting))
                throw new InvalidDataException("Export contains passwords.");
            if (lifetime.IsCancellationRequested) return;
            using var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export preset — passwords removed", SuggestedFileName = "ARK-preset.json", DefaultExtension = "json",
                ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType("ARK preset") { Patterns = ["*.json"] }]
            });
            if (file is null || lifetime.IsCancellationRequested) return;
            var path = file.TryGetLocalPath();
            if (path is null) { presetsMessage = "Choose a file on this computer to export the preset."; return; }
            await new AtomicJsonStore<PresetExportDocument>(path).SaveAsync(exported, lifetime.Token);
            presetsMessage = "Preset exported. Join and admin passwords were removed.";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or TimeoutException
            or InvalidOperationException or NotSupportedException or ArgumentException or JsonException or OperationCanceledException)
        { presetsMessage = "Could not export the preset. Check the manager connection and destination, then try again."; }
        finally
        {
            presetExportBusy = false;
            if (!lifetime.IsCancellationRequested && settingsPage && applicationCategory == 5) Render();
        }
    }
}
