using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private bool advancedSaveBusy, advancedSaveAccepted;
    private Guid advancedSaveId;
    private ServerConfigurationEdit? advancedPendingEdit;
    private SaveChoice advancedSaveChoice;
    private bool lastAdvancedSaveSucceeded;

    private async Task SaveAdvancedConfigurationAsync(ServerDefinition server, int file, string name, string text)
    {
        if (client is null || advancedSaveBusy || lifetime.IsCancellationRequested) return;
        advancedSaveBusy = true;
        lastAdvancedSaveSucceeded = false;
        var key = (server.Id, file);
        advancedReadNoticeKey = key;
        try
        {
            if (advancedPendingEdit is null)
            {
                if (!advancedLoadedFiles.TryGetValue(key, out var loaded) || loaded.Text is not null && loaded.SourceSHA256 is null) return;
                var dialog = new Window { Title = "Save configuration?", Width = 580, Height = 340,
                    CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Background = Brush.Parse("#0F1921"), FontFamily = FontFamily };
                var body = new StackPanel { Margin = new Thickness(30), Spacing = 20 };
                body.Children.Add(Label("Save these changes?", 27, true));
                body.Children.Add(Label($"{server.Settings.Name} · {name}\nStopped maps apply immediately. Choose when this map applies changes if it is running.", 16));
                body.Children.Add(ActionButton("Save for next start", () => dialog.Close(SaveChoice.SaveForNextStart), true));
                body.Children.Add(ActionButton("Save and restart this map", () => dialog.Close(SaveChoice.SaveAndRestart)));
                body.Children.Add(ActionButton("Cancel", () => dialog.Close(SaveChoice.Cancel))); dialog.Content = new ScrollViewer { Content = body };
                advancedSaveChoice = await dialog.ShowDialog<SaveChoice>(this);
                if (advancedSaveChoice == SaveChoice.Cancel) return;
                advancedPendingEdit = new(server.Id, name, loaded.SourceSHA256 ?? SettingsConfigurationBinding.MissingFileHash, text);
                advancedSaveId = Guid.NewGuid(); advancedSaveAccepted = false;
            }
            var pending = advancedPendingEdit;
            advancedReadNoticeKey = (pending.ServerId, pending.FileName == "Game.ini" ? 1 : 0);
            advancedReadNotice = "Connecting to configuration save…"; Render();
            var reconnect = advancedSaveAccepted;
            advancedSaveAccepted = true;
            BackgroundOperation? job = reconnect
                ? (await client.CallAsync<OperationLookup>("operation-status-lookup", new OperationStatusRequest(advancedSaveId), TimeSpan.FromSeconds(8), lifetime.Token)).Operation
                : await client.CallAsync<BackgroundOperation>("server-configuration-save-start", new ConfirmedConfigurationEdit
                    { Edit = pending, Confirmed = true, Choice = advancedSaveChoice }, TimeSpan.FromSeconds(8), lifetime.Token, advancedSaveId);
            while (true)
            {
                if (job is null)
                {
                    var saved = await client.CallAsync<List<PendingConfigurationStatus>>("server-configuration-pending", pending.ServerId, TimeSpan.FromSeconds(8), lifetime.Token);
                    if (saved.Any(s => s.ServerId != pending.ServerId || s.Id == Guid.Empty || s.FileName is not ("Game.ini" or "GameUserSettings.ini"))) throw new InvalidDataException();
                    var current = await client.CallAsync<ServerConfigurationFile>("server-configuration-read", new ServerConfigurationReadRequest(pending.ServerId, pending.FileName), TimeSpan.FromSeconds(8), lifetime.Token);
                    if (current.ServerId != pending.ServerId || current.FileName != pending.FileName) throw new InvalidDataException();
                    advancedReadNotice = saved.Any(s => s.Id == advancedSaveId && s.FileName == pending.FileName)
                        ? "This configuration is durably saved for the next start. The original operation result is unavailable."
                        : current.Text == pending.Text ? "The file matches your saved text. The original restart outcome is unavailable; check the server state."
                        : "The original save result is unavailable. Your draft is retained; reload current configuration before saving again.";
                    advancedLoadedFiles.Remove((pending.ServerId, pending.FileName == "Game.ini" ? 1 : 0));
                    lastAdvancedSaveSucceeded=saved.Any(s=>s.Id==advancedSaveId&&s.FileName==pending.FileName)||current.Text==pending.Text;
                    advancedPendingEdit = null; advancedSaveAccepted = false; break;
                }
                if (job.Id != advancedSaveId || job.Kind != "server-configuration-save" || !Enum.IsDefined(job.State)) throw new InvalidDataException();
                advancedSaveAccepted = true;
                if (job.State == BackgroundOperationState.Succeeded)
                {
                    var result = job.Result?.Deserialize<ServerConfigurationEditResult>();
                    if (result is null || result.ServerId != pending.ServerId || result.FileName != pending.FileName || !result.Saved) throw new InvalidDataException();
                    if (result.Save is { } save && (!save.Saved || save.Pending && save.Restarted)) throw new InvalidDataException();
                    var savedKey = (pending.ServerId, pending.FileName == "Game.ini" ? 1 : 0);
                    advancedLoadedFiles.Remove(savedKey); advancedPreviewDrafts[savedKey] = pending.Text;
                    lastAdvancedSaveSucceeded=true;
                    advancedPendingEdit = null; advancedSaveAccepted = false;
                    advancedReadNotice = (result.Save?.Message ?? "Configuration saved.") + " Read it again before further edits."; break;
                }
                if (job.State is BackgroundOperationState.Failed or BackgroundOperationState.Canceled)
                {
                    advancedLoadedFiles.Remove((pending.ServerId, pending.FileName == "Game.ini" ? 1 : 0));
                    advancedPendingEdit = null; advancedSaveAccepted = false;
                    advancedReadNotice = "Save did not complete. Your draft is retained. Review pending configuration and reload before retrying."; break;
                }
                advancedReadNotice = "Saving configuration…"; Render();
                await Task.Delay(1000, lifetime.Token);
                job = (await client.CallAsync<OperationLookup>("operation-status-lookup", new OperationStatusRequest(advancedSaveId), TimeSpan.FromSeconds(8), lifetime.Token)).Operation;
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or InvalidOperationException or ArgumentException or JsonException or OperationCanceledException)
        { advancedReadNotice = "The save result is unknown. Your draft is retained; reconnect to check the pending save."; }
        finally { advancedSaveBusy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }
}
