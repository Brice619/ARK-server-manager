using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Globalization;
namespace ArkServerManager.Desktop;
public sealed partial class MainWindow
{
    private bool sharedSettingsBusy;
    public static Window CreateSharedSettingsDesignReview()
    {
        var owner = new MainWindow(["--design-preview"]);
        var cluster = owner.catalog!.Clusters.First();
        var preset = new ServerPreset(cluster.Name, cluster.Edition, cluster.Settings, [])
        { UserSettings = "[ServerSettings]\nHarvestAmountMultiplier=2\nTamingSpeedMultiplier=3\nXPMultiplier=2\n", GameSettings = "[/Script/ShooterGame.ShooterGameMode]\n" };
        return owner.BuildSharedSettingsDialog(cluster, preset, null, preview: true).Dialog;
    }
    private async Task EditSharedClusterSettingsAsync(ClusterDefinition selected)
    {
        if (client is null || sharedSettingsBusy || pendingClusterPreset is not null) return;
        sharedSettingsBusy = true;
        try
        {
            await RefreshAsync();
            var cluster = catalog!.Clusters.Single(item => item.Id == selected.Id);
            var baseline = cluster.Defaults;
            string? initialMap = null;
            if (baseline is null)
            {
                var first = catalog.Servers.FirstOrDefault(server => server.ClusterId == cluster.Id) ?? throw new InvalidOperationException("Add a map before defining shared settings.");
                baseline = await client.CallAsync<ServerPreset>("presets-capture", new PresetCaptureRequest(first, cluster.Name), TimeSpan.FromSeconds(15), lifetime.Token);
                initialMap = first.Settings.Name;
            }
            var editor = BuildSharedSettingsDialog(cluster, baseline, initialMap);
            if (!await editor.Dialog.ShowDialog<bool>(this)) return;
            await ApplyClusterPresetAsync(cluster, editor.Build());
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsBasicSaveError(error) || error is ArgumentException)
        { clusterPresetNotice = "Shared settings could not be reviewed: " + error.Message; }
        finally { sharedSettingsBusy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }

    private (Window Dialog, Func<ServerPreset> Build) BuildSharedSettingsDialog(ClusterDefinition cluster, ServerPreset baseline, string? initialMap, bool preview = false)
    {
            var templates = PresetConfigurationEntries.Apply(baseline.UserSettings ?? "", baseline.GameSettings ?? "", baseline.Settings.Values);
            var dialog = new Window { Title = "Shared cluster settings", Width = 680, Height = 650, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Brush.Parse("#0F1921"), FontFamily = FontFamily };
            var body = new StackPanel { Margin = new Thickness(28), Spacing = 14 };
            body.Children.Add(Label(cluster.Name, 27, true));
            body.Children.Add(Label("Edit shared settings, then choose maps and review the changes. Map-specific overrides are kept.", 16));
            if (initialMap is not null) body.Children.Add(Label("Initial shared settings are loaded from " + initialMap + ".", 15));
            var capacity = new TextBox { Text = baseline.Settings.MaxPlayers.ToString(CultureInfo.InvariantCulture), MaxLength = 9 };
            body.Children.Add(SharedSettingField("Maximum players", capacity, "Per map · local overrides stay"));
            var user = new TextBox { Text = templates.UserSettings, AcceptsReturn = true, Height = 240, FontFamily = new FontFamily("Consolas") };
            var game = new TextBox { Text = templates.GameSettings, AcceptsReturn = true, Height = 240, FontFamily = new FontFamily("Consolas") };
            var rates = new List<TextBox>();
            var rateEditors = new Dictionary<string, TextBox>();
            var syncingRates = false;
            var writingFromGeneral = false;
            foreach (var setting in new[] { ("Harvest amount", "HarvestAmountMultiplier"), ("Taming speed", "TamingSpeedMultiplier"), ("Experience gain", "XPMultiplier") })
            {
                var current = new IniDocument(user.Text ?? "").GetValues("ServerSettings", setting.Item2).LastOrDefault();
                var input = new TextBox { Text = current ?? "1", MaxLength = 32 };
                rates.Add(input);
                rateEditors.Add(setting.Item2, input);
                input.TextChanged += (_, _) =>
                {
                    if (syncingRates) return;
                    if (!decimal.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0) return;
                    var ini = new IniDocument(user.Text ?? ""); ini.SetValues("ServerSettings", setting.Item2, [value.ToString(CultureInfo.InvariantCulture)]);
                    writingFromGeneral = true;
                    try { user.Text = ini.ToString(); } finally { writingFromGeneral = false; }
                };
                body.Children.Add(SharedSettingField(setting.Item1, input, "Multiplier · 1 is the normal rate"));
            }
            user.TextChanged += (_, _) =>
            {
                if (writingFromGeneral) return;
                syncingRates = true;
                try
                {
                    var ini = new IniDocument(user.Text ?? "");
                    foreach (var editor in rateEditors) editor.Value.Text = ini.GetValues("ServerSettings", editor.Key).LastOrDefault() ?? "1";
                }
                finally { syncingRates = false; }
            };
            var mods = new TextBox { Text = string.Join(",", baseline.Mods), MaxLength = 4096 };
            var advanced = new StackPanel { Spacing = 14 };
            advanced.Children.Add(DraftField("Shared mod IDs", mods, "Comma-separated IDs in load order. Map mod-list overrides are kept."));
            advanced.Children.Add(Label("GameUserSettings.ini", 17, true)); advanced.Children.Add(user);
            advanced.Children.Add(Label("Game.ini", 17, true)); advanced.Children.Add(game);
            advanced.Children.Add(Label("Map names, ports and passwords remain specific to each map.", 15));
            body.Children.Add(new Expander { Header = "Advanced configuration and mods", Content = advanced });
            var validation = Label("", 15); validation.IsVisible = false; body.Children.Add(validation);
            var actions = new WrapPanel();
            actions.Children.Add(ActionButton("Review changes…", preview ? null : () =>
            {
                if (!int.TryParse(capacity.Text, out var parsed) || parsed < 1 || rates.Any(input => !decimal.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0))
                { validation.Text = "Enter a positive capacity and non-negative numeric multipliers."; validation.IsVisible = true; return; }
                if (ConfigurationSyntax.Check(user.Text ?? "").Count > 0 || ConfigurationSyntax.Check(game.Text ?? "").Count > 0)
                { validation.Text = "Correct the configuration syntax before reviewing."; validation.IsVisible = true; return; }
                dialog.Close(true);
            }, true));
            var cancel = ActionButton("Cancel", () => dialog.Close(false)); cancel.Margin = new Thickness(14, 0, 0, 0); actions.Children.Add(cancel); body.Children.Add(actions);
            dialog.Content = new ScrollViewer { Content = body };

        return (dialog, () =>
        {

            if (!int.TryParse(capacity.Text, out var maximum) || maximum < 1) throw new ArgumentException("Enter a positive player capacity.");
            var modIds = (mods.Text ?? "").Split([',', ' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToList();
            AsaModLaunchArguments.Build(modIds);
            var draft = new ServerPreset(cluster.Name + " shared settings", cluster.Edition, baseline.Settings.Copy() with { MaxPlayers = maximum, Values = [] }, modIds)
                { UserSettings = user.Text ?? "", GameSettings = game.Text ?? "" };
            return draft;
        });
    }
    private Control SharedSettingField(string name, TextBox input, string hint)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*") };
        var text = new StackPanel { Spacing = 5, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        text.Children.Add(Label(name, 16, true));
        var help = Label(hint, 13); help.Foreground = Muted; text.Children.Add(help);
        grid.Children.Add(text); Grid.SetColumn(input, 1); input.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center; input.MinHeight = 40; grid.Children.Add(input);
        return new Border { Child = grid, Padding = new Thickness(14), CornerRadius = new CornerRadius(8), Background = Brush.Parse("#16232D") };
    }
}

