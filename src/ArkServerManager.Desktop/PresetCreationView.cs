using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private void RenderPresetCreation()
    {
        content.Children.Add(ActionButton("← Back to Presets", () => { presetCreating = false; Render(); }));
        content.Children.Add(Label("Create preset", 32, true));
        content.Children.Add(Label("Start with defaults or capture a server's configuration and mod order.", 17));
        var body = new StackPanel { Spacing = 16 };
        var servers = catalog?.Servers.OrderBy(s => s.Settings.Name).ToArray() ?? [];
        body.Children.Add(Label("Start from", 16, true));
        var source = new ComboBox { ItemsSource = new[] { "New defaults" }.Concat(servers.Select(s => s.Settings.Name + " · " + s.Edition)).ToArray(),
            SelectedIndex = 0, MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Stretch };
        body.Children.Add(source);
        body.Children.Add(Label("Preset name", 16, true));
        var name = new TextBox { PlaceholderText = "Name your preset", FontSize = 17, Padding = new Thickness(12, 10) };
        body.Children.Add(name);
        var edition = new ComboBox { ItemsSource = new[] { "ASA", "ASE" }, SelectedIndex = 0, MinWidth = 140 };
        body.Children.Add(Label("Edition", 16, true)); body.Children.Add(edition);
        var capacity = new NumericUpDown { Minimum = 1, Maximum = int.MaxValue, Value = 70, FormatString = "0", Width = 160,
            HorizontalAlignment = HorizontalAlignment.Left };
        body.Children.Add(Label("Maximum players", 16, true)); body.Children.Add(capacity);
        Avalonia.Automation.AutomationProperties.SetName(source, "Preset source");
        Avalonia.Automation.AutomationProperties.SetName(name, "Preset name");
        Avalonia.Automation.AutomationProperties.SetName(edition, "Preset edition");
        Avalonia.Automation.AutomationProperties.SetName(capacity, "Preset maximum players");
        source.SelectionChanged += (_, _) =>
        {
            var selected = source.SelectedIndex > 0 ? servers[source.SelectedIndex - 1] : null;
            edition.IsEnabled = selected is null;
            if (selected is not null)
            {
                edition.SelectedIndex = selected.Edition == ArkEdition.ASA ? 0 : 1;
                capacity.Value = selected.Settings.MaxPlayers;
                if (string.IsNullOrWhiteSpace(name.Text)) name.Text = selected.Settings.Name + " preset";
            }
        };
        body.Children.Add(Label("Review the contents on the next screen. Existing servers keep their settings.", 15));
        content.Children.Add(Surface(body));
        var warning = Label("", 15); warning.Foreground = Amber; content.Children.Add(warning);
        Button review = null!;
        review = ActionButton("Review preset", () =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { warning.Text = "Enter a preset name."; return; }
            var selected = source.SelectedIndex > 0 ? servers[source.SelectedIndex - 1] : null;
            var settings = selected?.Settings.Copy() ?? new ServerSettings
                { Name = name.Text.Trim(), GamePort = 7777, QueryPort = 27015, RconPort = 32330 };
            settings = settings with { MaxPlayers = (int)(capacity.Value ?? 70) };
            var preset = new ServerPreset(name.Text.Trim(), selected?.Edition ?? (edition.SelectedIndex == 0 ? ArkEdition.ASA : ArkEdition.ASE),
                settings, selected is null ? [] : [.. selected.Mods]);
            settings.Validate();
            if (selected is not null && !designPreview)
            { body.IsEnabled = false; _ = CapturePresetCreationAsync(selected, preset, body, warning); }
            else Complete(preset);
        }, primary: true);
        review.Margin = new Thickness(0, 18, 0, 0); content.Children.Add(review);

        void Complete(ServerPreset preset)
        {
            importPreview = preset; presetEditEntry = null; presetImportId = Guid.NewGuid(); importConfirm = false;
            presetFromServer = true; presetCreating = false; Render();
        }

        async Task CapturePresetCreationAsync(ServerDefinition server, ServerPreset draft, Control controls, TextBlock notice)
        {
            if (client is null || presetImportBusy) return;
            presetImportBusy = true; review.IsEnabled = false; notice.Text = "Reading server configuration…";
            try
            {
                var captured = await client.CallAsync<ServerPreset>("presets-capture", new PresetCaptureRequest(server, draft.Name),
                    TimeSpan.FromSeconds(15), lifetime.Token);
                if (captured is null || captured.Edition != server.Edition || captured.Name != draft.Name || captured.UserSettings is null)
                    throw new InvalidDataException("Unexpected captured preset.");
                captured = captured with { Settings = captured.Settings with { MaxPlayers = draft.Settings.MaxPlayers } };
                PresetLibrary.ReadImport(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new PresetExportDocument(1, captured)));
                if (presetCreating) Complete(captured);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or ArgumentException
                or System.Text.Json.JsonException or OperationCanceledException)
            { notice.Text = "Could not capture this server. Refresh its configuration and try again."; }
            finally { presetImportBusy = false; controls.IsEnabled = true; review.IsEnabled = true; }
        }
    }
}
