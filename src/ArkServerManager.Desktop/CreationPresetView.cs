using Avalonia.Controls;
using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private Control CreationPresetPicker()
    {
        var body = new StackPanel { Spacing = 16 };
        body.Children.Add(Label("Choose a preset", 23, true));
        body.Children.Add(Label("Replaces this draft's settings and mod list. Setup assigns its own ports.", 16));
        if (creationPreset is not null) body.Children.Add(Label("Applied: " + creationPreset.Name, 17));
        body.Children.Add(ActionButton(presetsLoading ? "Loading…" : "Refresh presets", client is null || presetsLoading ? null
            : () => { _ = LoadPresetsAsync(); }));
        var entries = displayedPresets?.Where(p => p.Preset.Edition == draftEdition).ToArray() ?? [];
        if (entries.Length == 0) { body.Children.Add(Label("No presets loaded for this edition. Create or import one in App Settings.", 16)); return body; }
        var chooser = new ComboBox { ItemsSource = entries.Select(p => p.Preset.Name).ToArray(), SelectedIndex = 0, MinWidth = 250 };
        Avalonia.Automation.AutomationProperties.SetName(chooser, "New server preset"); body.Children.Add(chooser);
        var review = new StackPanel { Spacing = 12, IsVisible = false };
        body.Children.Add(ActionButton("Review preset", () =>
        {
            review.Children.Clear(); review.IsVisible = true;
            var preset = entries[chooser.SelectedIndex].Preset;
            Fact(review, "Preset", preset.Name); Fact(review, "Maximum players", preset.Settings.MaxPlayers.ToString());
            Fact(review, "Mod count", preset.Mods.Count.ToString());
            review.Children.Add(Label("Apply these settings to the new server draft?", 17));
            review.Children.Add(ActionButton("Yes, apply preset", () =>
            {
                try
                {
                    // Validate the full configuration and mod IDs before changing any draft choices.
                    var validation = new GuidedServerConfigurationRequest
                    {
                        Server = new(Guid.NewGuid(), preset.Edition, ServerMapIdentifier.ForLaunch(preset.Edition, draftMap!),
                            Path.GetTempPath(), preset.Settings.Copy()) { Mods = [.. preset.Mods] },
                        PvE = true, AdministratorPassword = "validation-only",
                        UserSettingsTemplate = preset.UserSettings, GameSettingsTemplate = preset.GameSettings
                    };
                    GuidedServerConfiguration.Build(validation);
                    creationPreset = preset with { Settings = preset.Settings.Copy(), Mods = [.. preset.Mods] };
                    creationModDraft = null;
                    draftName = preset.Settings.Name; draftCapacity = preset.Settings.MaxPlayers;
                    var config = PresetConfigurationEntries.Apply(preset.UserSettings ?? "", preset.GameSettings ?? "", preset.Settings.Values);
                    var ini = new IniDocument(config.UserSettings);
                    string? Single(string section, string key) => ini.GetValues(section, key) is { Count: 1 } values ? values[0] : null;
                    draftMode = bool.TryParse(Single("ServerSettings", "ServerPVE"), out var pve) ? pve ? "PvE" : "PvP" : null;
                    draftAdminPassword = Single("ServerSettings", "ServerAdminPassword") ?? "";
                    draftJoinPassword = Single("ServerSettings", "ServerPassword") ?? "";
                    draftDescription = Single("MessageOfTheDay", "Message") ?? "";
                    draftWelcomeDuration = int.TryParse(Single("MessageOfTheDay", "Duration"), out var duration) && duration > 0 ? duration : null;
                    customizationCategory = 0; creationValidation = ""; Render();
                }
                catch (Exception error) when (error is ArgumentException or InvalidDataException or NotSupportedException)
                { creationValidation = "This preset contains unsupported configuration or mod IDs. Edit the preset before applying it."; Render(); }
            }, primary: true));
        }));
        chooser.SelectionChanged += (_, _) => review.IsVisible = false;
        body.Children.Add(review); return body;
    }
}
