using Avalonia.Controls;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private IReadOnlyList<DeletionRecoveryNotice>? deletionRecovery;
    private bool recoveryStale;
    private bool recoveryReviewOpen;
    private readonly Dictionary<Guid, RecoveryJobView> recoveryJobs = [];
    private sealed class RecoveryJobView(DeletionRecoverySelection selection)
    {
        public readonly DeletionRecoverySelection Selection = selection;
        public readonly Guid RequestId = Guid.NewGuid();
        public bool Busy;
        public bool Accepted;
        public bool Finished;
        public string Message = "Recovery has not started.";
    }

    private async Task LoadRecoveryAsync()
    {
        if (client is null || lifetime.IsCancellationRequested) return;
        try
        {
            var notices = await client.CallAsync<List<DeletionRecoveryNotice>>("deletion-recovery-status", null,
                TimeSpan.FromSeconds(8), lifetime.Token);
            if (notices is null || notices.Count > 1024 || notices.Any(n => n is null
                || !Enum.IsDefined(n.Action) || n.Action == DeletionRecoveryAction.None
                || string.IsNullOrWhiteSpace(n.Message)))
                throw new InvalidDataException("Invalid recovery status.");
            deletionRecovery = notices;
            recoveryStale = false;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException
            or OperationCanceledException or System.Text.Json.JsonException)
        { recoveryStale = true; }
        if (!lifetime.IsCancellationRequested) Render();
    }

    private void AddRecoveryNotice(bool details)
    {
        if (designPreview || (!recoveryStale && deletionRecovery is not { Count: > 0 })) return;
        var body = new StackPanel { Spacing = 12 };
        var heading = Label(recoveryStale ? "Recovery status could not be checked"
            : $"{deletionRecovery!.Count} recovery notice{(deletionRecovery.Count == 1 ? " needs" : "s need")} attention", 18, true);
        heading.Foreground = Brush.Parse("#FFC04D");
        body.Children.Add(heading);
        if (recoveryStale) body.Children.Add(Label("Refresh to check again. Previously loaded notices may be out of date.", 14));
        if (details)
        {
            body.Children.Add(Label("Server files are left untouched until recovery can be checked and completed.", 14));
            foreach (var notice in deletionRecovery ?? [])
            {
                var item = new StackPanel { Spacing = 8 };
                item.Children.Add(Label(notice.ServerName ?? "Recovery record needs review", 17, true));
                item.Children.Add(Label(notice.Message, 15));
                if (notice.OperationId is Guid operation) item.Children.Add(Label("Reference: " + operation, 13));
                if (OperatingSystem.IsWindows() && notice.OperationId is Guid identity && notice.ServerId is Guid server
                    && !string.IsNullOrWhiteSpace(notice.ServerName)
                    && notice.Action is DeletionRecoveryAction.RestoreStagedFiles or DeletionRecoveryAction.FinishFileRemoval)
                {
                    var pending = recoveryJobs.GetValueOrDefault(identity);
                    item.Children.Add(ActionButton(pending is null ? "Review recovery" : "Recovery is tracked below",
                        recoveryStale || pending is not null || client is null ? null : () => _ = ReviewRecoveryAsync(notice)));
                }
                body.Children.Add(new Expander { Header = notice.Action switch
                {
                    DeletionRecoveryAction.RestoreStagedFiles => "Restore staged server files",
                    DeletionRecoveryAction.FinishFileRemoval => "Complete interrupted deletion",
                    _ => "Review recovery record"
                }, Content = item });
            }
        }
        else body.Children.Add(ActionButton("View recovery notices", () =>
        { ResetPage(); activityPage = true; Render(); }));
        content.Children.Add(Surface(body));
    }

    private void AddRecoveryJobs()
    {
        foreach (var view in recoveryJobs.Values)
        {
            var item = new StackPanel { Spacing = 10 };
            item.Children.Add(Label("Recovery · " + view.Selection.ConfirmedName, 18, true));
            item.Children.Add(Label(view.Message, 15));
            if (view.Busy) item.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 4 });
            else if (!view.Finished) item.Children.Add(ActionButton("Reconnect to recovery", () => _ = TrackRecoveryAsync(view)));
            else item.Children.Add(ActionButton("Dismiss result", () => { recoveryJobs.Remove(view.Selection.OperationId); Render(); }));
            content.Children.Add(Surface(item));
        }
    }

    private async Task ReviewRecoveryAsync(DeletionRecoveryNotice notice)
    {
        if (recoveryReviewOpen || client is null || recoveryStale || notice.OperationId is not Guid operation
            || notice.ServerId is not Guid server || notice.ServerName is null) return;
        recoveryReviewOpen = true;
        try
        {
            var removing = notice.Action == DeletionRecoveryAction.FinishFileRemoval;
            var title = removing ? "Complete interrupted deletion?" : "Restore staged server files?";
            var dialog = new Window { Title = title, Width = 650, Height = 500, CanResize = false,
                ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                FontFamily = FontFamily, Background = Brush.Parse("#0F1921") };
            var body = new StackPanel { Margin = new Avalonia.Thickness(32), Spacing = 20 };
            body.Children.Add(Label(title, 26, true));
            body.Children.Add(Label(notice.ServerName, 19, true));
            body.Children.Add(Label(removing
                ? "This permanently removes the staged files from the interrupted deletion. Shared cluster data remains."
                : "This moves the staged files back to their registered server folder. It does not start the server.", 17));
            body.Children.Add(Label("The selected map must be stopped. Other maps can keep running.", 15));
            var confirm = ActionButton(removing ? "Complete deletion" : "Restore files", () => dialog.Close(true));
            if (removing)
            {
                body.Children.Add(Label("Type " + notice.ServerName + " to confirm", 16));
                var name = new TextBox();
                Avalonia.Automation.AutomationProperties.SetName(name, "Recovery server name confirmation");
                confirm.IsEnabled = false;
                name.TextChanged += (_, _) => confirm.IsEnabled = name.Text == notice.ServerName;
                body.Children.Add(name);
                confirm.Background = Brush.Parse("#8E3035");
            }
            var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 14 };
            actions.Children.Add(ActionButton("Cancel", () => dialog.Close(false))); actions.Children.Add(confirm);
            body.Children.Add(actions); dialog.Content = new ScrollViewer { Content = body };
            if (await dialog.ShowDialog<bool>(this) && !lifetime.IsCancellationRequested)
            {
                var view = new RecoveryJobView(new(operation, server, notice.ServerName, notice.Action));
                recoveryJobs[operation] = view;
                await TrackRecoveryAsync(view);
            }
        }
        catch (InvalidOperationException) { connection.Text = "Recovery review could not be opened. Please try again."; Render(); }
        finally { recoveryReviewOpen = false; }
    }

    private async Task TrackRecoveryAsync(RecoveryJobView view)
    {
        if (client is null || view.Busy || view.Finished || lifetime.IsCancellationRequested) return;
        view.Busy = true; view.Message = "Connecting to recovery…"; Render();
        try
        {
            var job = await client.CallAsync<BackgroundOperation>(view.Accepted ? "operation-status" : "deletion-recovery-start",
                view.Accepted ? new OperationStatusRequest(view.RequestId) : (object)view.Selection,
                TimeSpan.FromSeconds(8), lifetime.Token, view.Accepted ? null : view.RequestId);
            while (true)
            {
                if (job.Id != view.RequestId || job.Kind != "deletion-recovery" || !Enum.IsDefined(job.State))
                    throw new InvalidDataException("Unexpected recovery job.");
                view.Accepted = true;
                if (job.State == BackgroundOperationState.Succeeded)
                {
                    var result = job.Result?.Deserialize<DeletionRecoveryResult>();
                    var phase = view.Selection.Action == DeletionRecoveryAction.RestoreStagedFiles
                        ? ServerDeletionPhase.RolledBack : ServerDeletionPhase.FilesRemoved;
                    if (result is null || result.OperationId != view.Selection.OperationId
                        || result.ServerId != view.Selection.ServerId || result.Phase != phase)
                        throw new InvalidDataException("Recovery completion could not be verified.");
                }
                view.Message = job.State switch
                {
                    BackgroundOperationState.Succeeded => "Recovery completed. Refreshing server files and notices…",
                    BackgroundOperationState.Failed => job.Error ?? "Recovery failed. Review the notice before trying again.",
                    BackgroundOperationState.Canceled => "Recovery was interrupted. Refresh to check its recorded state.",
                    _ => job.Progress?.Message ?? "Recovery is queued."
                };
                Render();
                if (job.State is BackgroundOperationState.Succeeded or BackgroundOperationState.Failed or BackgroundOperationState.Canceled)
                { view.Finished = true; await RefreshAsync(); view.Message = job.State == BackgroundOperationState.Succeeded ? "Recovery completed." : view.Message; break; }
                await Task.Delay(500, lifetime.Token);
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(view.RequestId),
                    TimeSpan.FromSeconds(8), lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException
            or OperationCanceledException or System.Text.Json.JsonException)
        { view.Message = "Connection lost. Recovery may still be running. Reconnect to check its result."; }
        finally { view.Busy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }
}
