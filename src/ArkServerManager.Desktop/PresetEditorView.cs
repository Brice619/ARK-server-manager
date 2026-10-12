using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private PresetEntry? presetEditEntry;
    private ServerPreset? presetEditDraft;
    private bool presetEditorOpen;

    private void BeginPresetEdit(PresetEntry entry)
    {
        presetEditEntry = entry;
        presetEditDraft = entry.Preset with { Settings = entry.Preset.Settings.Copy(), Mods = [.. entry.Preset.Mods] };
        presetEditorOpen = true; presetsMessage = ""; Render();
    }

    private void RenderPresetEditor()
    {
        var draft = presetEditDraft!;
        content.Children.Add(ActionButton("← Back to Presets", () =>
        { presetEditorOpen = false; presetEditEntry = null; presetEditDraft = null; Render(); }));
        content.Children.Add(Label("Edit preset", 32, true));
        content.Children.Add(Label("Changes stay in your library until you apply the preset to a server.", 17));
        var body = new StackPanel { Spacing = 16 };
        TextBox Input(string caption, string value, Action<string> change, bool secret = false)
        {
            body.Children.Add(Label(caption, 16, true));
            var input = new TextBox { Text = value, FontSize = 17, Padding = new Thickness(12, 10), PasswordChar = secret ? '●' : '\0' };
            Avalonia.Automation.AutomationProperties.SetName(input, caption);
            input.TextChanged += (_, _) => change(input.Text ?? ""); body.Children.Add(input); return input;
        }
        Input("Preset name", draft.Name, value => presetEditDraft = presetEditDraft! with { Name = value });
        Input("Server name", draft.Settings.Name, value => presetEditDraft = presetEditDraft! with
            { Settings = presetEditDraft!.Settings with { Name = value } });
        Fact(body, "Edition", draft.Edition.ToString());
        body.Children.Add(Label("Maximum players", 16, true));
        var capacity = new NumericUpDown { Minimum = 1, Maximum = int.MaxValue, Value = draft.Settings.MaxPlayers,
            Width = 160, HorizontalAlignment = HorizontalAlignment.Left, FormatString = "0" };
        Avalonia.Automation.AutomationProperties.SetName(capacity, "Preset maximum players");
        capacity.ValueChanged += (_, _) => presetEditDraft = presetEditDraft! with
            { Settings = presetEditDraft!.Settings with { MaxPlayers = (int)(capacity.Value ?? 70) } };
        body.Children.Add(capacity); content.Children.Add(Surface(body));

        var mods = new TextBox { Text = string.Join(Environment.NewLine, draft.Mods), AcceptsReturn = true,
            MinHeight = 100, MaxHeight = 230, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        Avalonia.Automation.AutomationProperties.SetName(mods, "Mod IDs in load order");
        mods.TextChanged += (_, _) => presetEditDraft = presetEditDraft! with
            { Mods = (mods.Text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() };
        var modBody = new StackPanel { Spacing = 12 };
        modBody.Children.Add(Label("One mod ID per line, in load order.", 16)); modBody.Children.Add(mods);
        content.Children.Add(new Expander { Header = "Mod load order", Content = Surface(modBody) });

        var settingsBody = new StackPanel { Spacing = 12 };
        var rows = new StackPanel { Spacing = 12 };
        void Populate()
        {
            rows.Children.Clear();
            foreach (var pair in presetEditDraft!.Settings.Values.ToArray())
            {
                var row = new StackPanel { Spacing = 8 };
                row.Children.Add(Label(pair.Key, 16, true));
                var input = new TextBox { Text = pair.Value, PasswordChar = pair.Key.Contains("Password", StringComparison.OrdinalIgnoreCase) ? '●' : '\0' };
                Avalonia.Automation.AutomationProperties.SetName(input, pair.Key);
                input.TextChanged += (_, _) => presetEditDraft!.Settings.Values[pair.Key] = input.Text ?? "";
                row.Children.Add(input);
                row.Children.Add(ActionButton("Remove setting", () => { presetEditDraft!.Settings.Values.Remove(pair.Key); Populate(); }));
                rows.Children.Add(row);
            }
        }
        Populate(); settingsBody.Children.Add(rows);
        var key = new TextBox { PlaceholderText = "Setting name" };
        var value = new TextBox { PlaceholderText = "Setting value" };
        Avalonia.Automation.AutomationProperties.SetName(key, "New setting name");
        Avalonia.Automation.AutomationProperties.SetName(value, "New setting value");
        key.TextChanged += (_, _) => value.PasswordChar = (key.Text ?? "").Contains("Password", StringComparison.OrdinalIgnoreCase) ? '●' : '\0';
        var notice = Label("", 15); notice.Foreground = Amber;
        settingsBody.Children.Add(key); settingsBody.Children.Add(value);
        settingsBody.Children.Add(ActionButton("Add setting", () =>
        {
            var name = key.Text?.Trim() ?? "";
            if (name.Length == 0 || presetEditDraft!.Settings.Values.Keys.Any(k => k.Equals(name, StringComparison.OrdinalIgnoreCase)))
            { notice.Text = "Enter a new, unique setting name."; return; }
            presetEditDraft.Settings.Values.Add(name, value.Text ?? ""); key.Text = value.Text = ""; notice.Text = ""; Populate();
        }));
        settingsBody.Children.Add(notice);
        content.Children.Add(new Expander { Header = "Configuration entries", Content = Surface(settingsBody) });
        var warning = Label("", 15); warning.Foreground = Amber; content.Children.Add(warning);
        content.Children.Add(ActionButton("Review changes", () =>
        {
            try
            {
                var edited = presetEditDraft! with { Name = presetEditDraft!.Name.Trim() };
                PresetLibrary.ReadImport(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new PresetExportDocument(1, edited)));
                importPreview = edited; presetFromServer = false; importConfirm = false; presetEditorOpen = false; Render();
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException)
            { warning.Text = "Check the names, player limit and mod IDs. Each mod ID must be unique."; }
        }, primary: true));
    }
}
