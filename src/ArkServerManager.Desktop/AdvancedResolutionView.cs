using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private async Task ResolveAdvancedPendingAsync(PendingConfigurationStatus selected)
    {
        if (client is null || designPreview || advancedSaveBusy || advancedReading) return;
        advancedSaveBusy = true; advancedReadNoticeKey = (selected.ServerId, selected.FileName == "Game.ini" ? 1 : 0);
        try
        {
            var review = await client.CallAsync<PendingConfigurationResolutionReview>("server-configuration-resolution-review",
                new PendingConfigurationReviewRequest(selected.ServerId, selected.Id), TimeSpan.FromSeconds(8), lifetime.Token);
            if (review.ServerId != selected.ServerId || review.Id != selected.Id || review.FileName != selected.FileName
                || review.Token is not { Length: 64 } || review.CurrentText is null || review.SavedText is null
                || System.Text.Encoding.UTF8.GetByteCount(review.CurrentText) > 4 * 1024 * 1024
                || System.Text.Encoding.UTF8.GetByteCount(review.SavedText) > 4 * 1024 * 1024) throw new InvalidDataException();
            var name = catalog?.Servers.SingleOrDefault(s => s.Id == selected.ServerId)?.Settings.Name ?? "Selected map";
            var choice = await ConfigurationResolutionDialog(review, name).ShowDialog<ConfigurationResolutionChoice?>(this);
            if (choice is null) return;
            var confirm = new Window { Title = "Confirm configuration repair", Width = 540, Height = 310,
                Background = Brush.Parse("#0F1921"), FontFamily = FontFamily, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var body = new StackPanel { Margin = new Thickness(28), Spacing = 18 };
            body.Children.Add(Label(choice == ConfigurationResolutionChoice.UseSavedText ? "Apply the saved text?" : "Keep the current file?", 23, true));
            body.Children.Add(Label(name + " · " + selected.FileName + "\nThis map must be stopped. " + (choice == ConfigurationResolutionChoice.UseSavedText
                ? review.CurrentMissing ? "The missing file will be recreated from the saved text." : "The reviewed current file will be replaced with the saved text."
                : "The reviewed current file stays as it is and this saved edit is removed."), 16));
            body.Children.Add(ActionButton("Confirm repair", () => confirm.Close(true), true));
            body.Children.Add(ActionButton("Cancel", () => confirm.Close(false))); confirm.Content = new ScrollViewer { Content = body };
            if (!await confirm.ShowDialog<bool>(this)) return;
            var operationId = Guid.NewGuid();
            BackgroundOperation? job = await client.CallAsync<BackgroundOperation>("server-configuration-resolve-start",
                new PendingConfigurationResolution(selected.ServerId, selected.Id, review.Token, choice.Value, true), TimeSpan.FromSeconds(8), lifetime.Token, operationId);
            while (job is not null)
            {
                if (job.Id != operationId || job.Kind != "server-configuration-resolve" || !Enum.IsDefined(job.State)) throw new InvalidDataException();
                if (job.State is not (BackgroundOperationState.Queued or BackgroundOperationState.Running)) break;
                await Task.Delay(500, lifetime.Token);
                job = (await client.CallAsync<OperationLookup>("operation-status-lookup", new OperationStatusRequest(operationId), TimeSpan.FromSeconds(8), lifetime.Token)).Operation;
            }
            await ReadAdvancedPendingAsync(selected.ServerId);
            if (job?.State == BackgroundOperationState.Succeeded)
            {
                var result = job.Result?.Deserialize<PendingConfigurationResolutionResult>();
                if (result is null || result.ServerId != selected.ServerId || result.PreviousId != selected.Id
                    || result.Applied != (choice == ConfigurationResolutionChoice.UseSavedText) || result.KeptCurrent != (choice == ConfigurationResolutionChoice.KeepCurrentFile)
                    || result.ReplacementId != (choice == ConfigurationResolutionChoice.UseSavedText ? operationId : (Guid?)null)) throw new InvalidDataException();
                advancedReadNotice = result.Applied ? "Saved text applied. Reload the file before editing again." : "Current file kept and saved edit removed. Reload before editing again.";
            }
            else advancedReadNotice = job?.Error ?? "The original repair result is unavailable. Review current file and pending status before another action.";
            advancedLoadedFiles.Remove((selected.ServerId, selected.FileName == "Game.ini" ? 1 : 0));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or ArgumentException or JsonException)
        { advancedReadNotice = "Repair could not be confirmed. Stop this map, refresh its pending status and review both versions before retrying."; }
        finally { advancedSaveBusy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }

    private static Window ConfigurationResolutionDialog(PendingConfigurationResolutionReview review, string name)
    {
        var dialog = new Window { Title = "Resolve saved configuration", Width = 740, Height = 680, MinWidth = 600, MinHeight = 540,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush.Parse("#0F1921"), FontFamily = new FontFamily("Segoe UI, Inter, sans-serif") };
        var body = new StackPanel { Margin = new Thickness(28), Spacing = 18 };
        body.Children.Add(Label("Resolve saved configuration", 27, true));
        body.Children.Add(Label(name + " · " + review.FileName, 17));
        body.Children.Add(Label(review.CurrentMissing ? "The current file is missing. Review the saved text to restore it. Stop this map before confirming repair."
            : "Review each version, then choose which to keep. Stop this map before confirming repair.", 16));
        var text = new TextBox { Text = review.CurrentText, IsReadOnly = true, AcceptsReturn = true, AcceptsTab = true,
            FontFamily = new FontFamily("Consolas"), FontSize = 15, Height = 320, TextWrapping = TextWrapping.NoWrap };
        Avalonia.Automation.AutomationProperties.SetName(text, "Configuration version for repair");
        var version = new ComboBox { ItemsSource = new[] { review.CurrentMissing ? "Current file (missing)" : "Current file", "Saved edit" }, SelectedIndex = review.CurrentMissing ? 1 : 0, Width = 240 };
        if (review.CurrentMissing) text.Text = review.SavedText;
        Avalonia.Automation.AutomationProperties.SetName(version, "Configuration version");
        version.SelectionChanged += (_, _) => text.Text = version.SelectedIndex == 1 ? review.SavedText : review.CurrentText;
        body.Children.Add(version); body.Children.Add(text);
        var actions = new Avalonia.Controls.WrapPanel();
        actions.Children.Add(ActionButton("Keep current file", review.CurrentMissing ? null : () => dialog.Close((ConfigurationResolutionChoice?)ConfigurationResolutionChoice.KeepCurrentFile)));
        actions.Children.Add(ActionButton("Use saved text", () => dialog.Close((ConfigurationResolutionChoice?)ConfigurationResolutionChoice.UseSavedText), true));
        actions.Children.Add(ActionButton("Cancel", () => dialog.Close((ConfigurationResolutionChoice?)null))); body.Children.Add(actions);
        foreach (var button in actions.Children) button.Margin = new Thickness(0, 0, 12, 8);
        dialog.Content = new ScrollViewer { Content = body }; return dialog;
    }
    public static Window CreateConfigurationResolutionDesignReview(bool missing = false) => ConfigurationResolutionDialog(new(Guid.NewGuid(), Guid.NewGuid(),
        "GameUserSettings.ini", missing ? "" : "[ServerSettings]\nTamingSpeedMultiplier=7\n", "[ServerSettings]\nTamingSpeedMultiplier=4\n", true,
        missing ? null : new string('a', 64), new string('b', 64)) { CurrentMissing = missing }, "Main PvE Island");
}
