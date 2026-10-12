using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Text.Json;
namespace ArkServerManager.Desktop;
public sealed partial class MainWindow
{
    private bool clusterOverrideBusy;
    private ClusterOverrideSave? pendingOverrideSave;
    private Guid overrideSaveId;
    private async Task EditClusterOverridesAsync(ServerDefinition server)
    {
        if (client is null || catalog is null || clusterOverrideBusy || server.ClusterId is null) return;
        clusterOverrideBusy = true;
        try
        {
            var reconnect = pendingOverrideSave is not null;
            if (!reconnect)
            {
                var review = await client.CallAsync<ClusterOverrideReview>("cluster-overrides-read", server.Id, TimeSpan.FromSeconds(8), lifetime.Token);
                if (review.ServerId != server.Id || review.ClusterId != server.ClusterId || review.Directory != server.Directory) throw new InvalidDataException();
                ClusterSettingOverrides.Validate(review.Overrides);
                var keys = new List<ClusterSettingOverride>(review.Overrides);
                foreach (var file in new[] { "GameUserSettings.ini", "Game.ini" })
                {
                    var current = await client.CallAsync<ServerConfigurationFile>("server-configuration-read", new ServerConfigurationReadRequest(server.Id, file), TimeSpan.FromSeconds(8), lifetime.Token);
                    if (current.ServerId != server.Id || current.FileName != file) throw new InvalidDataException();
                    keys.AddRange(new IniDocument(current.Text ?? "").Entries().Select(entry => new ClusterSettingOverride(file, entry.Section, entry.Key)));
                    var defaults = catalog.Clusters.Single(cluster => cluster.Id == review.ClusterId).Defaults;
                    keys.AddRange(new IniDocument((file == "Game.ini" ? defaults?.GameSettings : defaults?.UserSettings) ?? "").Entries()
                        .Select(entry => new ClusterSettingOverride(file, entry.Section, entry.Key)));
                }
                var excluded = new HashSet<string>(["ServerPassword", "ServerAdminPassword", "AdminPassword", "RCONPort", "RCONEnabled", "SessionName", "ServerName", "Port", "GamePort", "QueryPort", "ClusterId", "ActiveMods", "ModIDS", "MaxPlayers", "Version"], StringComparer.OrdinalIgnoreCase);
                keys = keys.Where(key => !excluded.Contains(key.Key)).GroupBy(key => key.FileName + "\0" + key.Section + "\0" + key.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First()).OrderBy(key => key.Key).ToList();
                if (keys.Count > 4096) throw new InvalidDataException("Too many configuration keys for map override review.");
                var chosen = keys.ToDictionary(key => key, key => ClusterSettingOverrides.Contains(review.Overrides, key.FileName, key.Section, key.Key));
                var dialog = new Window { Title = "Map-specific settings", Width = 660, Height = 620, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Background = Brush.Parse("#0F1921"), FontFamily = FontFamily };
                var body = new StackPanel { Margin = new Thickness(28), Spacing = 16 };
                body.Children.Add(Label(server.Settings.Name, 25, true));
                body.Children.Add(Label("Keep selected settings specific to this map when applying cluster settings. Edit their values in this map’s settings or advanced editor.", 16));
                var capacity = new CheckBox { Content = "Keep this map’s player capacity", IsChecked = review.MaxPlayers };
                var mods = new CheckBox { Content = "Keep this map’s mod list", IsChecked = review.Mods }; body.Children.Add(capacity); body.Children.Add(mods);
                var search = new TextBox { PlaceholderText = "Find a setting, section or file…" }; body.Children.Add(search);
                var count = Label("", 14); body.Children.Add(count);
                var rows = new StackPanel { Spacing = 12 }; body.Children.Add(new ScrollViewer { Content = rows, Height = 245 });
                void Populate()
                {
                    rows.Children.Clear();
                    var filtered = keys.Where(key => (key.Key + " " + key.Section + " " + key.FileName).Contains(search.Text ?? "", StringComparison.OrdinalIgnoreCase)).ToArray();
                    count.Text = filtered.Length > 150 ? "Showing 150 of " + filtered.Length + " settings. Refine your search to find more." : filtered.Length + " settings";
                    foreach (var key in filtered.Take(150))
                    {
                        var box = new CheckBox { Content = key.Key + "\n" + key.Section + " · " + key.FileName, IsChecked = chosen[key] };
                        box.IsCheckedChanged += (_, _) => chosen[key] = box.IsChecked == true; rows.Children.Add(box);
                    }
                }
                search.TextChanged += (_, _) => Populate(); Populate();
                body.Children.Add(ActionButton("Review and save…", () => dialog.Close(true), true)); body.Children.Add(ActionButton("Cancel", () => dialog.Close(false)));
                dialog.Content = new ScrollViewer { Content = body };
                if (!await dialog.ShowDialog<bool>(this)) return;
                var confirm = new Window { Title = "Save map overrides?", Width = 530, Height = 290, Background = Brush.Parse("#0F1921"), FontFamily = FontFamily, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                var selection = chosen.Where(item => item.Value).Select(item => item.Key).ToList();
                var released = review.MaxPlayers && capacity.IsChecked != true || review.Mods && mods.IsChecked != true
                    || review.Overrides.Any(key => !ClusterSettingOverrides.Contains(selection, key.FileName, key.Section, key.Key));
                var confirmation = new StackPanel { Margin = new Thickness(28), Spacing = 18 };
                confirmation.Children.Add(Label("Save map-specific settings?", 23, true));
                confirmation.Children.Add(Label(released ? "Settings returned to shared control will use the cluster values. Stopped maps apply immediately; choose when this map applies changes if it is running."
                    : "These settings stay specific to this map during future cluster saves. Current values remain as they are.", 16));
                confirmation.Children.Add(ActionButton(released ? "Save for next start" : "Save", () => confirm.Close(SaveChoice.SaveForNextStart), true));
                if (released) confirmation.Children.Add(ActionButton("Save and restart this map", () => confirm.Close(SaveChoice.SaveAndRestart)));
                confirmation.Children.Add(ActionButton("Cancel", () => confirm.Close(SaveChoice.Cancel))); confirm.Content = new ScrollViewer { Content = confirmation };
                var choice = await confirm.ShowDialog<SaveChoice>(this); if (choice == SaveChoice.Cancel) return;
                pendingOverrideSave = new(review, selection, capacity.IsChecked == true, mods.IsChecked == true, true) { Choice = choice };
                overrideSaveId = Guid.NewGuid();
            }
            var job = reconnect ? (await client.CallAsync<OperationLookup>("operation-status-lookup", new OperationStatusRequest(overrideSaveId), TimeSpan.FromSeconds(8), lifetime.Token)).Operation
                : await client.CallAsync<BackgroundOperation>("cluster-overrides-save-start", pendingOverrideSave, TimeSpan.FromSeconds(8), lifetime.Token, overrideSaveId);
            while (job is not null)
            {
                if (job.Id != overrideSaveId || job.Kind != "cluster-overrides-save" || !Enum.IsDefined(job.State)) throw new InvalidDataException();
                if (job.FinishedAt is not null) break;
                await Task.Delay(500, lifetime.Token);
                job = (await client.CallAsync<OperationLookup>("operation-status-lookup", new OperationStatusRequest(overrideSaveId), TimeSpan.FromSeconds(8), lifetime.Token)).Operation;
            }
            var latest = await client.CallAsync<ClusterOverrideReview>("cluster-overrides-read", pendingOverrideSave!.Expected.ServerId, TimeSpan.FromSeconds(8), lifetime.Token);
            if (latest.ServerId != pendingOverrideSave.Expected.ServerId || latest.ClusterId != pendingOverrideSave.Expected.ClusterId || latest.Directory != pendingOverrideSave.Expected.Directory) throw new InvalidDataException();
            lifecycleNotice = latest.MaxPlayers == pendingOverrideSave.MaxPlayers && latest.Mods == pendingOverrideSave.Mods && latest.Overrides.SequenceEqual(pendingOverrideSave.Overrides)
                ? "The requested map overrides are saved." : "Map overrides differ from this request. Review the current selection before saving again.";
            if (job?.State == BackgroundOperationState.Succeeded)
            {
                var result = job.Result?.Deserialize<ClusterOverrideSaveResult>();
                if (result is null || result.Overrides.ServerId != latest.ServerId || result.Applied is { } applied && applied.ServerId != latest.ServerId) throw new InvalidDataException();
                if (result.ApplyError is not null) lifecycleNotice = result.ApplyError;
                else if (result.Applied is { } appliedResult) lifecycleNotice = appliedResult.Save.Pending
                    ? "Override choices and inherited settings are saved for the next start. The current choices remain active until then."
                    : lifecycleNotice + " " + appliedResult.Save.Message;
            }
            else if (job?.State is BackgroundOperationState.Failed or BackgroundOperationState.Canceled) lifecycleNotice = job.Error ?? "The save was interrupted. Review current overrides and pending settings.";
            else lifecycleNotice += " The original apply result is unavailable; review current and pending settings before another change.";
            pendingOverrideSave = null; await RefreshAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsBasicSaveError(error) || error is ArgumentException)
        { lifecycleNotice = pendingOverrideSave is null ? "Map overrides could not be reviewed: " + error.Message : "Override save outcome unknown. Reopen map-specific settings to check the original request."; }
        finally { clusterOverrideBusy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }
}
