using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Text.Json;
namespace ArkServerManager.Desktop;
public sealed partial class MainWindow
{
    private ClusterPresetSave? pendingClusterPreset;
    private Guid clusterPresetRequestId;
    private bool clusterPresetBusy;
    private string clusterPresetNotice = "";
    private async Task ApplyClusterPresetAsync(ClusterDefinition cluster, ServerPreset? draft = null)
    {
        if (client is null || catalog is null || clusterPresetBusy) return;
        clusterPresetBusy = true;
        try
        {
            var reconnect = pendingClusterPreset is not null;
            if (!reconnect)
            {
                var presets = draft is null ? (await client.CallAsync<List<PresetEntry>>("presets-list", null, TimeSpan.FromSeconds(8), lifetime.Token))
                    .Where(entry => entry.Preset.Edition == cluster.Edition).ToArray() : new[] { new PresetEntry(Guid.NewGuid(), draft) };
                var members = catalog.Servers.Where(server => server.ClusterId == cluster.Id).ToArray();
                if (presets.Length == 0 || members.Length == 0) { clusterPresetNotice = "Create a matching preset and add cluster maps before applying it."; return; }
                var dialog = ClusterPresetDialog(draft is null ? "Choose cluster preset" : "Choose maps for shared settings");
                var body = new StackPanel { Margin = new Thickness(28), Spacing = 16 };
                body.Children.Add(Label(cluster.Name, 25, true));
                var picker = new ComboBox { ItemsSource = presets.Select(entry => entry.Preset.Name).ToArray(), SelectedIndex = 0,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch }; body.Children.Add(picker);
                body.Children.Add(Label("Choose maps to change. Unchecked maps retain their own settings.", 16));
                var maps = new List<(Guid Id, CheckBox Choice)>();
                foreach (var member in members)
                {
                    var choice = new CheckBox { Content = member.Settings.Name + " · " + MapName(member.Map), IsChecked = true };
                    maps.Add((member.Id, choice)); body.Children.Add(choice);
                }
                body.Children.Add(ActionButton("Review changes…", () => dialog.Close(true), true));
                body.Children.Add(ActionButton("Cancel", () => dialog.Close(false))); dialog.Content = new ScrollViewer { Content = body };
                if (!await dialog.ShowDialog<bool>(this)) return;
                var selected = maps.Where(map => map.Choice.IsChecked == true).Select(map => map.Id).ToList();
                if (selected.Count == 0 || picker.SelectedIndex < 0) { clusterPresetNotice = "Choose at least one map and a preset."; return; }
                var review = await client.CallAsync<ClusterPresetReview>("cluster-preset-review",
                    new ClusterPresetSelection(cluster.Id, presets[picker.SelectedIndex].Id, selected) { DraftPreset = draft }, TimeSpan.FromSeconds(30), lifetime.Token);
                if (review.ClusterId != cluster.Id || review.Maps is null || review.Maps.Count != selected.Count
                    || !review.Maps.Select(map => map.ServerId).ToHashSet().SetEquals(selected)) throw new InvalidDataException();
                var confirmation = ClusterPresetDialog("Apply cluster preset?");
                var changes = new StackPanel { Margin = new Thickness(28), Spacing = 16 };
                changes.Children.Add(Label("Review " + review.Maps.Count + " maps", 25, true));
                changes.Children.Add(Label("Apply shared settings to selected maps while keeping their map-specific overrides, names, ports and passwords. Stopped maps apply immediately; running maps use your choice below.", 16));
                var future = new CheckBox { Content = "Use these settings as defaults for new cluster maps", IsChecked = true };
                changes.Children.Add(future);
                foreach (var map in review.Maps)
                    changes.Children.Add(new Expander { Header = map.ServerName, Content = Label(map.CurrentMaxPlayers + " → " + map.DesiredMaxPlayers
                        + " players\n" + map.ConfigurationChanges + " configuration changes\nAdd " + map.AddedMods.Count + " mods · Remove " + map.RemovedMods.Count + " mods", 15) });
                changes.Children.Add(ActionButton("Save for next start", () => confirmation.Close(SaveChoice.SaveForNextStart), true));
                changes.Children.Add(ActionButton("Save and restart selected running maps", () => confirmation.Close(SaveChoice.SaveAndRestart)));
                changes.Children.Add(ActionButton("Cancel", () => confirmation.Close(SaveChoice.Cancel))); confirmation.Content = new ScrollViewer { Content = changes };
                var saveChoice = await confirmation.ShowDialog<SaveChoice>(this); if (saveChoice == SaveChoice.Cancel) return;
                pendingClusterPreset = new(review, saveChoice, true) { UseForNewMaps = future.IsChecked == true }; clusterPresetRequestId = Guid.NewGuid();
            }
            Render();
            var job = reconnect
                ? (await client.CallAsync<OperationLookup>("operation-status-lookup", new OperationStatusRequest(clusterPresetRequestId), TimeSpan.FromSeconds(8), lifetime.Token)).Operation
                : await client.CallAsync<BackgroundOperation>("cluster-preset-save-start", pendingClusterPreset, TimeSpan.FromSeconds(8), lifetime.Token, clusterPresetRequestId);
            while (true)
            {
                if (job is null)
                {
                    var lines = new List<string> { "The original cluster save result is unavailable. Current saved preset status:" };
                    foreach (var expected in pendingClusterPreset!.Expected.Maps)
                    {
                        var status = await client.CallAsync<PresetApplicationPendingResult>("presets-application-pending", expected.ServerId, TimeSpan.FromSeconds(8), lifetime.Token);
                        if (status.ServerId != expected.ServerId || status.Pending is { } saved && (saved.Review is null || saved.Review.ServerId != expected.ServerId
                            || saved.Review.Edition != expected.Edition)) throw new InvalidDataException();
                        lines.Add(expected.ServerName + ": " + (status.Pending is null ? "No pending preset is recorded; review current settings."
                            : status.Pending.Review.PresetId != expected.PresetId ? "A different preset is saved; review it."
                            : status.Pending.Started ? "Preset application began; review its recovery status." : "Preset is saved for the next start."));
                    }
                    lines.Add("The cluster save was not repeated. Review each map and new-map defaults before saving again.");
                    await RefreshAsync();
                    pendingClusterPreset = null;
                    clusterPresetNotice = string.Join("\n", lines);
                    return;
                }
                if (job.Id != clusterPresetRequestId || job.Kind != "cluster-preset-save" || !Enum.IsDefined(job.State)) throw new InvalidDataException();
                if (job.State == BackgroundOperationState.Succeeded)
                {
                    var result = job.Result?.Deserialize<ClusterPresetSaveResult>();
                    if (result is null || result.ClusterId != pendingClusterPreset!.Expected.ClusterId || result.Maps is null
                        || result.Maps.Count != pendingClusterPreset.Expected.Maps.Count
                        || result.DefaultsSaved != pendingClusterPreset.UseForNewMaps
                        || result.Maps.Any(map => map is null || (map.Result is null) == (map.Error is null)
                            || map.Result is { } saved && (saved.ServerId != map.ServerId || saved.PresetId != pendingClusterPreset.Expected.PresetId
                                || saved.Save is null || !saved.Save.Saved || saved.Save.Pending && saved.Save.Restarted))
                        || !result.Maps.Select(map => map.ServerId).ToHashSet().SetEquals(pendingClusterPreset.Expected.Maps.Select(map => map.ServerId))) throw new InvalidDataException();
                    var failures = result.Maps.Where(map => map.Error is not null).ToArray();
                    clusterPresetNotice = (result.Maps.Count - failures.Length) + " maps saved. " + failures.Length + " need review.";
                    if (result.DefaultsSaved) clusterPresetNotice += " New-map defaults updated.";
                    foreach (var failed in failures) clusterPresetNotice += "\n" + failed.Error;
                    pendingClusterPreset = null; await RefreshAsync(); return;
                }
                if (job.State is BackgroundOperationState.Failed or BackgroundOperationState.Canceled)
                { pendingClusterPreset = null; clusterPresetNotice = job.Error ?? "Cluster preset did not complete. Review each map’s saved preset status."; return; }
                clusterPresetNotice = job.Progress?.Message ?? "Applying cluster preset…"; Render();
                await Task.Delay(500, lifetime.Token);
                job = (await client.CallAsync<OperationLookup>("operation-status-lookup", new OperationStatusRequest(clusterPresetRequestId), TimeSpan.FromSeconds(8), lifetime.Token)).Operation;
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsBasicSaveError(error))
        { clusterPresetNotice = pendingClusterPreset is null ? "Cluster preset could not be reviewed: " + error.Message : "Save outcome unknown. Check the original cluster preset request."; }
        finally { clusterPresetBusy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }
    private Window ClusterPresetDialog(string title) => new() { Title = title, Width = 650, Height = 530,
        WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush.Parse("#0F1921"), FontFamily = FontFamily };
}
