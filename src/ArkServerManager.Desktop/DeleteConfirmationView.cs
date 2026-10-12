using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private bool serverDeletionReviewOpen;
    private ServerDeletionSelection? pendingDeletion;
    private ServerDefinition? pendingDeletionServer;
    private Guid deletionRequestId;

    private async Task ReviewServerDeletionAsync(ServerDefinition server)
    {
        if (serverDeletionReviewOpen || lifetime.IsCancellationRequested) return;
        if (pendingDeletion is not null && pendingDeletion.ServerId != server.Id) return;
        serverDeletionReviewOpen = true;
        try
        {
            var dialog = new Window { Title = "Delete " + server.Settings.Name + "?", Width = 650, Height = 540,
                CanResize = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                FontFamily = FontFamily, Background = Brush.Parse("#0F1921") };
            var body = new StackPanel { Margin = new Thickness(32), Spacing = 22 };
            var heading = Label("Delete " + server.Settings.Name + "?", 27, true);
            heading.TextWrapping = TextWrapping.Wrap;
            body.Children.Add(heading);
            var explanation = Label("Deleting this server removes its associated files, including its saves, downloaded mods and backups.", 17);
            body.Children.Add(Label("Recorded router and Windows Firewall rules are removed first. Windows may request administrator access. If network cleanup fails, server files are retained for review.", 15));
            explanation.TextWrapping = TextWrapping.Wrap; body.Children.Add(explanation);
            if (server.ClusterId is not null)
            {
                var shared = Label("Shared cluster player/transfer data and shared backups remain.", 16);
                shared.Foreground = Muted; body.Children.Add(shared);
            }
            var prompt = Label("Type " + server.Settings.Name + " to confirm", 17);
            prompt.TextWrapping = TextWrapping.Wrap; body.Children.Add(prompt);
            var confirmation = new TextBox { FontSize = 17, Padding = new Thickness(12, 10),
                Text = pendingDeletion?.ConfirmedName, IsReadOnly = pendingDeletion is not null };
            Avalonia.Automation.AutomationProperties.SetName(confirmation, "Server name deletion confirmation");
            body.Children.Add(confirmation);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 14 };
            var cancel = ActionButton("Cancel", () => dialog.Close(false)); actions.Children.Add(cancel);
            var remove = ActionButton(designPreview ? "Delete preview server" : pendingDeletion is null ? "Delete server" : "Check deletion result",
                designPreview ? () => dialog.Close(true) : null);
            remove.IsEnabled = pendingDeletion is not null && client is not null;
            remove.Background = Brush.Parse("#8E3035");
            confirmation.TextChanged += (_, _) => remove.IsEnabled = (designPreview || client is not null)
                && string.Equals(confirmation.Text, server.Settings.Name, StringComparison.Ordinal);
            actions.Children.Add(remove); body.Children.Add(actions);
            var note = Label(designPreview ? "Design preview · Removes only the illustrative server in this session. No files are deleted."
                : "Only this map will be stopped. Its exact file scope is checked before deletion.", 14);
            note.TextWrapping = TextWrapping.Wrap;
            note.Foreground = Muted; body.Children.Add(note);
            var deleting = false;
            dialog.Closing += (_, closing) => { if (deleting && closing.CloseReason == WindowCloseReason.WindowClosing) closing.Cancel = true; };
            if (!designPreview)
                remove.Click += async (_, _) =>
                {
                    deleting = true; remove.IsEnabled = cancel.IsEnabled = confirmation.IsEnabled = false;
                    try
                    {
                        if (await ExecuteReviewedDeletionAsync(server, confirmation.Text ?? "", text => note.Text = text))
                        { deleting = false; dialog.Close(true); }
                        else
                        {
                            remove.Content = pendingDeletion is null ? "Delete server" : "Check deletion result";
                            confirmation.IsReadOnly = pendingDeletion is not null;
                        }
                    }
                    finally { deleting = false; cancel.IsEnabled = confirmation.IsEnabled = true;
                        remove.IsEnabled = client is not null && confirmation.Text == server.Settings.Name; }
                };
            dialog.Content = new ScrollViewer { Content = body };
            if (await dialog.ShowDialog<bool>(this) && designPreview && !lifetime.IsCancellationRequested)
            {
                catalog?.Servers.RemoveAll(s => s.Id == server.Id);
                modOrderDrafts.Remove(server.Id);
                foreach (var key in advancedPreviewDrafts.Keys.Where(k => k.Server == server.Id).ToArray()) advancedPreviewDrafts.Remove(key);
                foreach (var key in advancedPreviewSaved.Keys.Where(k => k.Server == server.Id).ToArray()) advancedPreviewSaved.Remove(key);
                foreach (var key in modConfigurationPreviewDrafts.Keys.Where(k => k.Server == server.Id).ToArray()) modConfigurationPreviewDrafts.Remove(key);
                foreach (var key in modConfigurationPreviewSaved.Keys.Where(k => k.Server == server.Id).ToArray()) modConfigurationPreviewSaved.Remove(key);
                selectedServer = null;
                Render();
            }
        }
        catch (InvalidOperationException)
        { connection.Text = "The deletion review could not be shown. Please try again."; }
        finally { serverDeletionReviewOpen = false; }
    }

    private async Task<bool> ExecuteReviewedDeletionAsync(ServerDefinition server, string name, Action<string> report)
    {
        if (client is null || lifetime.IsCancellationRequested) return false;
        try
        {
            BackgroundOperation job;
            if (pendingDeletion is null)
            {
                if (name != server.Settings.Name) return false;
                pendingDeletion = new(server.Id, name); pendingDeletionServer = server; deletionRequestId = Guid.NewGuid();
                job = await client.CallAsync<BackgroundOperation>("server-deletion-start", pendingDeletion, TimeSpan.FromSeconds(8), lifetime.Token, deletionRequestId);
            }
            else job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(deletionRequestId), TimeSpan.FromSeconds(8), lifetime.Token);
            while (true)
            {
                if (job.Id != deletionRequestId || job.Kind != "server-deletion" || !Enum.IsDefined(job.State)) throw new InvalidDataException();
                if (job.State == BackgroundOperationState.Succeeded)
                {
                    var result = job.Result?.Deserialize<ServerDeletionRecord>();
                    if (result is null || result.OperationId != deletionRequestId || result.ServerId != server.Id
                        || result.Phase != ServerDeletionPhase.FilesRemoved) throw new InvalidDataException();
                    pendingDeletion = null; pendingDeletionServer = null; selectedServer = null;
                    ForgetDeletedServerViews(server.Id);
                    await RefreshAsync(); return true;
                }
                if (job.State is BackgroundOperationState.Failed or BackgroundOperationState.Canceled)
                {
                    pendingDeletion = null; pendingDeletionServer = null;
                    await RefreshAsync();
                    report("Deletion did not finish. Files are not assumed unchanged. Check activity and any recovery notice before trying again."); return false;
                }
                report(job.Progress?.Message ?? "Deleting the selected map…");
                await Task.Delay(1000, lifetime.Token);
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(deletionRequestId), TimeSpan.FromSeconds(8), lifetime.Token);
            }
        }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or ArgumentException or JsonException or OperationCanceledException)
        {
            if (!lifetime.IsCancellationRequested && pendingDeletion is not null)
            {
                try
                {
                    var recorded = await client.CallAsync<RecordedDeletionStatus?>("server-deletion-record", deletionRequestId,
                        TimeSpan.FromSeconds(8), lifetime.Token);
                    if (recorded?.Record is { } record && record.OperationId == deletionRequestId && record.ServerId == server.Id
                        && record.Phase == ServerDeletionPhase.FilesRemoved && recorded.Assessment is { Action: DeletionRecoveryAction.None })
                    {
                        pendingDeletion = null; pendingDeletionServer = null; selectedServer = null;
                        ForgetDeletedServerViews(server.Id);
                        await RefreshAsync(); return true;
                    }
                }
                catch (Exception recoveryError) when (recoveryError is IOException or TimeoutException or InvalidOperationException or ArgumentException or JsonException or OperationCanceledException) { }
            }
            report("Deletion result unknown. Check this request to reconnect; it will not be sent again automatically. Recovery notices are available in activity."); return false;
        }
    }

    private void AddPendingDeletionNotice()
    {
        if (pendingDeletion is null || pendingDeletionServer is null) return;
        var server = pendingDeletionServer;
        content.Children.Add(ActionButton("Check pending deletion: " + server.Settings.Name,
            serverDeletionReviewOpen ? null : () => { _ = ReviewServerDeletionAsync(server); }));
    }

    private void ForgetDeletedServerViews(Guid id)
    {
        welcomeEditors.Remove(id); commandConsoles.Remove(id); playerObservations.Remove(id); announcements.Remove(id);
        basicEditors.Remove(id);
        modOrderDrafts.Remove(id);
        foreach (var key in advancedLoadedFiles.Keys.Where(k => k.Server == id).ToArray()) advancedLoadedFiles.Remove(key);
        foreach (var key in advancedPreviewDrafts.Keys.Where(k => k.Server == id).ToArray()) advancedPreviewDrafts.Remove(key);
        foreach (var key in advancedPreviewSaved.Keys.Where(k => k.Server == id).ToArray()) advancedPreviewSaved.Remove(key);
        foreach (var key in advancedValidation.Keys.Where(k => k.Server == id).ToArray()) advancedValidation.Remove(key);
        foreach (var key in modConfigurationPreviewDrafts.Keys.Where(k => k.Server == id).ToArray()) modConfigurationPreviewDrafts.Remove(key);
        foreach (var key in modConfigurationPreviewSaved.Keys.Where(k => k.Server == id).ToArray()) modConfigurationPreviewSaved.Remove(key);
    }
}
