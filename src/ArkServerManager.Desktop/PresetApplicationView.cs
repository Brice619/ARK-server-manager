using Avalonia.Controls;
using Avalonia;
using Avalonia.Media;
using System.Text.Json;
using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private Guid? presetApplicationServer;
    private List<PresetEntry> applicationPresets = [];
    private Guid? applicationPresetId;
    private PresetApplicationReview? applicationPresetReview;
    private bool applicationPresetBusy;
    private string applicationPresetNotice = "";
    private PresetApplicationSave? applicationPresetSave;
    private Guid applicationPresetRequestId;
    private bool applicationPresetAttempted;
    private PresetApplicationPendingStatus? applicationPresetPending;
    private PresetApplicationDiscard? applicationPresetDiscard;
    private Guid applicationPresetDiscardId;
    private bool applicationPresetDiscardAttempted;

    private void BeginPresetApplication(ServerDefinition server)
    {
        if (applicationPresetBusy) return;
        if (applicationPresetDiscard is not null)
        {
            presetApplicationServer = applicationPresetDiscard.ServerId;
            selectedServer = presetApplicationServer; Render(); return;
        }
        if (applicationPresetSave is not null)
        {
            presetApplicationServer = applicationPresetSave.Expected.ServerId;
            selectedServer = presetApplicationServer; Render(); return;
        }
        presetApplicationServer = server.Id; applicationPresetId = null; applicationPresetReview = null;
        applicationPresetPending = null;
        applicationPresets = []; applicationPresetNotice = "Loading saved presets…";
        Render(); _ = LoadApplicationPresetsAsync(server);
    }

    private void RenderPresetApplication(ServerDefinition server)
    {
        content.Children.Add(ActionButton("← Back to server", () => { presetApplicationServer = null; Render(); }));
        content.Children.Add(Label("Apply a preset", 32, true));
        content.Children.Add(Label(server.Settings.Name + " · " + server.Edition, 18));
        var body = new StackPanel { Spacing = 16 };
        if (applicationPresetReview is { } review)
        {
            body.Children.Add(Label(review.PresetName, 24, true));
            body.Children.Add(Label($"Player limit: {review.CurrentMaxPlayers} → {review.DesiredMaxPlayers}", 18));
            body.Children.Add(Label($"{review.ConfigurationChanges} configuration changes · {review.AddedMods.Count} mods added · {review.RemovedMods.Count} mods removed", 16));
            if (review.AddedMods.Count + review.RemovedMods.Count > 0)
            {
                var details = new StackPanel { Spacing = 8 };
                foreach (var id in review.AddedMods) details.Children.Add(Label("Add mod " + id, 15));
                foreach (var id in review.RemovedMods) details.Children.Add(Label("Remove mod " + id, 15));
                body.Children.Add(new Expander { Header = "Mod changes", Content = details });
            }
            body.Children.Add(Label("Your server identity, passwords, ports and save location are preserved.", 15));
            var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12 };
            if (applicationPresetPending is { } pending)
            {
                body.Children.Add(Label(pending.Started ? "Application was interrupted. Starting this server retries its saved work."
                    : "This preset is saved for the next start. You can cancel it before application begins.", 15));
                if (!pending.Started)
                    actions.Children.Add(ActionButton(applicationPresetBusy ? "Checking…" : applicationPresetDiscard is null ? "Cancel queued preset" : "Check cancellation status",
                        applicationPresetBusy || designPreview ? null : () => { _ = DiscardPresetApplicationAsync(server); }));
            }
            else
            {
            actions.Children.Add(ActionButton(applicationPresetBusy ? "Saving…" : applicationPresetSave is null ? "Save preset changes" : "Check save status",
                applicationPresetBusy || designPreview ? null : () => { _ = SavePresetApplicationAsync(server); }, true));
            actions.Children.Add(ActionButton("Choose another preset", applicationPresetBusy || applicationPresetSave is not null ? null
                : () => { applicationPresetReview = null; Render(); }));
            }
            body.Children.Add(actions);
        }
        else
        {
            var picker = new ComboBox { ItemsSource = applicationPresets.Select(p => p.Preset.Name).ToArray(),
                SelectedIndex = applicationPresets.FindIndex(p => p.Id == applicationPresetId), MinWidth = 280,
                IsEnabled = !applicationPresetBusy };
            Avalonia.Automation.AutomationProperties.SetName(picker, "Choose a saved preset");
            picker.SelectionChanged += (_, _) =>
            {
                applicationPresetId = picker.SelectedIndex >= 0 ? applicationPresets[picker.SelectedIndex].Id : null;
                Render();
            };
            body.Children.Add(picker);
            body.Children.Add(ActionButton(applicationPresetBusy ? "Checking…" : "Review changes",
                applicationPresetBusy || applicationPresetId is null ? null : () => { _ = ReadPresetApplicationAsync(server); }, true));
        }
        if (applicationPresetNotice.Length > 0) body.Children.Add(Label(applicationPresetNotice, 15));
        content.Children.Add(Surface(body));
    }

    private async Task LoadApplicationPresetsAsync(ServerDefinition server)
    {
        if (client is null) return;
        applicationPresetBusy = true; Render();
        try
        {
            var pendingResult = await client.CallAsync<PresetApplicationPendingResult>("presets-application-pending", server.Id,
                TimeSpan.FromSeconds(8), lifetime.Token);
            if (pendingResult is null || pendingResult.ServerId != server.Id) throw new InvalidDataException();
            if (presetApplicationServer != server.Id) return;
            if (pendingResult.Pending is { } pending)
            {
                if (pending.Review is null || pending.Review.ServerId != server.Id || pending.Review.Edition != server.Edition
                    || pending.Review.AddedMods is null || pending.Review.RemovedMods is null || pending.Review.ReviewToken is not { Length: 64 }
                    || pending.Review.ReviewToken.Any(c => !char.IsAsciiHexDigit(c))) throw new InvalidDataException();
                applicationPresetPending = pending; applicationPresetReview = pending.Review; applicationPresetNotice = ""; return;
            }
            applicationPresetPending = null;
            applicationPresetReview = null;
            var presets = await client.CallAsync<List<PresetEntry>>("presets-list", null, TimeSpan.FromSeconds(8), lifetime.Token);
            if (presets is null || presets.Any(p => p is null || p.Preset is null || p.Id == Guid.Empty
                || string.IsNullOrWhiteSpace(p.Preset.Name)) || presets.Select(p => p.Id).Distinct().Count() != presets.Count)
                throw new InvalidDataException();
            if (presetApplicationServer != server.Id) return;
            applicationPresets = presets.Where(p => p.Preset.Edition == server.Edition).ToList();
            applicationPresetNotice = applicationPresets.Count == 0 ? "No saved presets for this game. Create one in App Settings." : "";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or System.Text.Json.JsonException)
        { applicationPresetNotice = "Presets could not be loaded. Return to the server and try again."; }
        finally { applicationPresetBusy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }

    private async Task ReadPresetApplicationAsync(ServerDefinition server)
    {
        if (client is null || applicationPresetBusy || applicationPresetId is not Guid presetId) return;
        applicationPresetBusy = true; applicationPresetNotice = ""; Render();
        try
        {
            var review = await client.CallAsync<PresetApplicationReview>("presets-application-review", new PresetApplicationSelection(server.Id, presetId),
                TimeSpan.FromSeconds(10), lifetime.Token);
            if (review is null || review.ServerId != server.Id || review.PresetId != presetId || review.Edition != server.Edition
                || review.AddedMods is null || review.RemovedMods is null || review.DesiredMaxPlayers < 1 || review.CurrentMaxPlayers < 1
                || review.ConfigurationChanges < 0 || string.IsNullOrWhiteSpace(review.PresetName)
                || review.ReviewToken is null || review.ReviewToken.Length != 64 || review.ReviewToken.Any(c => !char.IsAsciiHexDigit(c)))
                throw new InvalidDataException("Unexpected preset review.");
            AsaModLaunchArguments.Build(review.AddedMods);
            AsaModLaunchArguments.Build(review.RemovedMods);
            if (presetApplicationServer == server.Id && applicationPresetId == presetId) applicationPresetReview = review;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        { applicationPresetNotice = "The preset changes could not be reviewed. Try again."; }
        finally { applicationPresetBusy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }

    private async Task SavePresetApplicationAsync(ServerDefinition server)
    {
        if (client is null || applicationPresetBusy || applicationPresetReview is null) return;
        applicationPresetBusy = true; Render();
        try
        {
            if (applicationPresetSave is null)
            {
                var dialog = new Window { Title = "Save preset changes?", Width = 570, Height = 420, CanResize = false,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush.Parse("#0F1921"), FontFamily = FontFamily };
                var panel = new StackPanel { Margin = new Thickness(28), Spacing = 16 };
                panel.Children.Add(Label("Save preset changes?", 26, true));
                var details = Label($"Apply {applicationPresetReview.PresetName} to {server.Settings.Name}.\n\nIf stopped, changes apply now. If running, choose when to apply them. Restart affects only this server.", 16);
                details.TextWrapping = TextWrapping.Wrap; panel.Children.Add(details);
                panel.Children.Add(ActionButton("Save · apply on next start if running", () => dialog.Close(SaveChoice.SaveForNextStart), true));
                panel.Children.Add(ActionButton("Save · restart this server if running", () => dialog.Close(SaveChoice.SaveAndRestart)));
                panel.Children.Add(ActionButton("Cancel", () => dialog.Close(SaveChoice.Cancel)));
                dialog.Content = new ScrollViewer { Content = panel };
                var choice = await dialog.ShowDialog<SaveChoice>(this);
                if (choice == SaveChoice.Cancel) return;
                applicationPresetSave = new(applicationPresetReview, choice, true);
                applicationPresetRequestId = Guid.NewGuid(); applicationPresetAttempted = false;
            }
            BackgroundOperation job;
            if (!applicationPresetAttempted)
            {
                applicationPresetAttempted = true;
                job = await client.CallAsync<BackgroundOperation>("presets-application-save-start", applicationPresetSave,
                    TimeSpan.FromSeconds(8), lifetime.Token, applicationPresetRequestId);
            }
            else job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(applicationPresetRequestId),
                TimeSpan.FromSeconds(8), lifetime.Token);
            while (true)
            {
                if (job.Id != applicationPresetRequestId || job.Kind != "presets-application-save" || !Enum.IsDefined(job.State))
                    throw new InvalidDataException("Unexpected preset save operation.");
                if (job.State == BackgroundOperationState.Succeeded)
                {
                    var result = job.Result?.Deserialize<PresetApplicationSaveResult>();
                    if (result is null || result.ServerId != server.Id || result.PresetId != applicationPresetSave.Expected.PresetId || result.Save is null)
                        throw new InvalidDataException("Unexpected preset save result.");
                    applicationPresetNotice = result.Save.Message; applicationPresetSave = null;
                    applicationPresetReview = null; await RefreshAsync(); await LoadApplicationPresetsAsync(server);
                    applicationPresetNotice = result.Save.Message; break;
                }
                if (job.State is BackgroundOperationState.Failed or BackgroundOperationState.Canceled)
                {
                    applicationPresetNotice = "Save did not complete. Any unfinished preset application is retained. Review server status before retrying.";
                    applicationPresetSave = null; applicationPresetReview = null; await RefreshAsync(); await LoadApplicationPresetsAsync(server);
                    applicationPresetNotice = "Save did not complete. Review the saved preset and server status before retrying."; break;
                }
                applicationPresetNotice = job.Progress?.Message ?? "Saving preset changes…"; Render();
                await Task.Delay(1000, lifetime.Token);
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(applicationPresetRequestId),
                    TimeSpan.FromSeconds(8), lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or ArgumentException or JsonException)
        { applicationPresetNotice = applicationPresetSave is null ? "Preset changes could not be saved. Review them again."
            : "Save outcome is unknown. Check this save's status before starting another."; }
        finally { applicationPresetBusy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }

    private async Task DiscardPresetApplicationAsync(ServerDefinition server)
    {
        if (client is null || applicationPresetBusy || applicationPresetPending is null) return;
        applicationPresetBusy = true; Render();
        try
        {
            if (applicationPresetDiscard is null)
            {
                var dialog = new Window { Title = "Cancel queued preset?", Width = 530, Height = 300, CanResize = false,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush.Parse("#0F1921"), FontFamily = FontFamily };
                var panel = new StackPanel { Margin = new Thickness(28), Spacing = 16 };
                panel.Children.Add(Label("Cancel queued preset?", 26, true));
                panel.Children.Add(Label("Remove this saved preset change. Your current server settings and world remain as they are.", 16));
                panel.Children.Add(ActionButton("Cancel queued preset", () => dialog.Close(true), true));
                panel.Children.Add(ActionButton("Keep it", () => dialog.Close(false)));
                dialog.Content = new ScrollViewer { Content = panel };
                if (!await dialog.ShowDialog<bool>(this)) return;
                applicationPresetDiscard = new(server.Id, applicationPresetPending.Review.ReviewToken, true);
                applicationPresetDiscardId = Guid.NewGuid(); applicationPresetDiscardAttempted = false;
            }
            BackgroundOperation job;
            if (!applicationPresetDiscardAttempted)
            {
                applicationPresetDiscardAttempted = true;
                job = await client.CallAsync<BackgroundOperation>("presets-application-discard-start", applicationPresetDiscard,
                    TimeSpan.FromSeconds(8), lifetime.Token, applicationPresetDiscardId);
            }
            else job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(applicationPresetDiscardId), TimeSpan.FromSeconds(8), lifetime.Token);
            while (true)
            {
                if (job.Id != applicationPresetDiscardId || job.Kind != "presets-application-discard" || !Enum.IsDefined(job.State)) throw new InvalidDataException();
                if (job.State == BackgroundOperationState.Succeeded)
                {
                    var result = job.Result?.Deserialize<PresetApplicationDiscardResult>();
                    if (result is null || result.ServerId != server.Id || !result.Discarded) throw new InvalidDataException();
                    applicationPresetDiscard = null; await LoadApplicationPresetsAsync(server);
                    applicationPresetNotice = "Queued preset canceled. Current settings and world were preserved."; break;
                }
                if (job.State is BackgroundOperationState.Failed or BackgroundOperationState.Canceled)
                {
                    applicationPresetDiscard = null; await LoadApplicationPresetsAsync(server);
                    applicationPresetNotice = "Cancellation did not complete. Refresh the saved preset status before retrying."; break;
                }
                applicationPresetNotice = "Canceling the queued preset…"; Render();
                await Task.Delay(1000, lifetime.Token);
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(applicationPresetDiscardId), TimeSpan.FromSeconds(8), lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or ArgumentException or JsonException)
        { applicationPresetNotice = "Cancellation outcome is unknown. Check the existing cancellation request before trying again."; }
        finally { applicationPresetBusy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }
}
