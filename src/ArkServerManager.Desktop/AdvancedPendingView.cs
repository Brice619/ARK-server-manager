using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private async Task ViewAdvancedPendingAsync(PendingConfigurationStatus selected)
    {
        if (client is null || designPreview || advancedReading || advancedSaveBusy) return;
        advancedReading = true;
        try
        {
            var review = await client.CallAsync<PendingConfigurationReview>("server-configuration-pending-review",
                new PendingConfigurationReviewRequest(selected.ServerId, selected.Id), TimeSpan.FromSeconds(8), lifetime.Token);
            if (review.Id != selected.Id || review.ServerId != selected.ServerId || review.FileName != selected.FileName || review.Text is null
                || System.Text.Encoding.UTF8.GetByteCount(review.Text) > 4 * 1024 * 1024 || review.Text.Contains('\0')) throw new InvalidDataException();
            if (advancedQueuedEdits.TryGetValue(selected.ServerId, out var entries))
                advancedQueuedEdits[selected.ServerId] = entries.Select(p => p.Id == selected.Id ? p with { Started = review.Started } : p).ToList();
            var name = catalog?.Servers.SingleOrDefault(s => s.Id == selected.ServerId)?.Settings.Name ?? "Selected map";
            await SavedConfigurationDialog(review, name).ShowDialog(this);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or JsonException)
        { advancedReadNoticeKey = (selected.ServerId, selected.FileName == "Game.ini" ? 1 : 0); advancedReadNotice = "The saved edit could not be loaded. Refresh its status before another action."; }
        finally { advancedReading = false; if (!lifetime.IsCancellationRequested) Render(); }
    }

    private static Window SavedConfigurationDialog(PendingConfigurationReview review, string name)
    {
        var dialog = new Window { Title = "Saved configuration", Width = 740, Height = 680, MinWidth = 600, MinHeight = 540,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush.Parse("#0F1921"), FontFamily = new FontFamily("Segoe UI, Inter, sans-serif") };
        var body = new StackPanel { Margin = new Thickness(28), Spacing = 18 };
        body.Children.Add(Label("Saved configuration", 27, true));
        body.Children.Add(Label(name + " · " + review.FileName, 17));
        body.Children.Add(Label(review.Started ? "Application began. This text is retained for recovery review."
            : "This is the saved text for the next start. Viewing it does not change the current file.", 16));
        var editor = new TextBox { Text = review.Text, IsReadOnly = true, AcceptsReturn = true, AcceptsTab = true,
            FontFamily = new FontFamily("Consolas"), FontSize = 15, Height = 350, TextWrapping = TextWrapping.NoWrap };
        Avalonia.Automation.AutomationProperties.SetName(editor, "Saved configuration text"); body.Children.Add(editor);
        body.Children.Add(ActionButton("Close", () => dialog.Close()));
        dialog.Content = new ScrollViewer { Content = body }; return dialog;
    }

    public static Window CreateSavedConfigurationDesignReview() => SavedConfigurationDialog(new(Guid.NewGuid(), Guid.NewGuid(),
        "GameUserSettings.ini", "; Illustrative saved edit\n[ServerSettings]\nTamingSpeedMultiplier=4\nXPMultiplier=2\n", false), "Main PvE Island");

    private async Task ReadAdvancedPendingAsync(Guid serverId)
    {
        var entries = await client!.CallAsync<List<PendingConfigurationStatus>>("server-configuration-pending", serverId, TimeSpan.FromSeconds(8), lifetime.Token);
        if (entries is null || entries.Count > 2 || entries.Any(p => p is null || p.ServerId != serverId || p.Id == Guid.Empty
                || p.FileName is not ("Game.ini" or "GameUserSettings.ini")) || entries.Select(p => p.FileName).Distinct().Count() != entries.Count)
            throw new InvalidDataException("Unexpected pending configuration response.");
        advancedQueuedEdits[serverId] = entries;
    }
    private async Task CancelAdvancedEditAsync(PendingConfigurationStatus selected)
    {
        if (client is null || advancedSaveBusy || selected.Started || designPreview) return;
        var dialog = new Window { Title = "Cancel saved edit?", Width = 530, Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush.Parse("#0F1921"), FontFamily = FontFamily };
        var body = new StackPanel { Margin = new Thickness(28), Spacing = 18 };
        body.Children.Add(Label("Cancel this saved edit?", 23, true));
        body.Children.Add(Label(selected.FileName + "\nThe saved change is removed only if application has not begun. The current server file stays as it is.", 16));
        body.Children.Add(ActionButton("Cancel saved edit", () => dialog.Close(true), true));
        body.Children.Add(ActionButton("Keep edit", () => dialog.Close(false))); dialog.Content = new ScrollViewer { Content = body };
        if (!await dialog.ShowDialog<bool>(this)) return;
        advancedSaveBusy = true;
        advancedReadNoticeKey = (selected.ServerId, selected.FileName == "Game.ini" ? 1 : 0);
        try
        {
            var requestId = Guid.NewGuid();
            BackgroundOperation? job = await client.CallAsync<BackgroundOperation>("server-configuration-discard-start",
                new PendingConfigurationDiscard(selected.ServerId, selected.Id, true), TimeSpan.FromSeconds(8), lifetime.Token, requestId);
            while (job is not null)
            {
                if (job.Id != requestId || job.Kind != "server-configuration-discard" || !Enum.IsDefined(job.State)) throw new InvalidDataException();
                if (job.State is not (BackgroundOperationState.Queued or BackgroundOperationState.Running)) break;
                await Task.Delay(500, lifetime.Token);
                job = (await client.CallAsync<OperationLookup>("operation-status-lookup", new OperationStatusRequest(requestId), TimeSpan.FromSeconds(8), lifetime.Token)).Operation;
            }
            await ReadAdvancedPendingAsync(selected.ServerId);
            if (job?.State == BackgroundOperationState.Succeeded)
            {
                var result = job.Result?.Deserialize<PendingConfigurationDiscardResult>();
                if (result is null || result.ServerId != selected.ServerId || result.Id != selected.Id || !result.Discarded) throw new InvalidDataException();
            }
            advancedReadNotice = advancedQueuedEdits[selected.ServerId].Any(p => p.Id == selected.Id)
                ? "The edit remains saved. Refresh its status before another action."
                : "The reviewed edit is no longer queued. Reload the current file to inspect its state.";
            if (job?.State is BackgroundOperationState.Failed or BackgroundOperationState.Canceled) advancedReadNotice = job.Error ?? advancedReadNotice;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or ArgumentException or JsonException)
        { advancedReadNotice = "Cancellation could not be confirmed. Read the configuration file again to refresh pending status before another action."; }
        finally { advancedSaveBusy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }
}
