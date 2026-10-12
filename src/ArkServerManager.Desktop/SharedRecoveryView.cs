using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private Guid? sharedRecoveryCluster;
    private List<SharedTransferRestoreRecord>? sharedRecoveryRecords;
    private bool sharedRecoveryLoading;
    private long sharedRecoveryRevision;
    private string sharedRecoveryNotice = "";
    private readonly Dictionary<Guid, RollbackView> sharedRollbackViews = new();

    private static string RestorePhaseLabel(WorldRestorePhase phase) => phase switch
    {
        WorldRestorePhase.Prepared => "Ready to restore",
        WorldRestorePhase.Replacing => "Restore interrupted",
        WorldRestorePhase.RollingBack => "Recovery interrupted",
        WorldRestorePhase.RolledBack => "Previous data recovered",
        WorldRestorePhase.Replaced => "Restored · Previous data retained",
        WorldRestorePhase.Started => "Started · Cleanup pending",
        WorldRestorePhase.Cleaned => "Completed",
        _ => "Needs review"
    };

    private void RenderSharedRecovery(ClusterDefinition cluster)
    {
        content.Children.Add(ActionButton("← Back to shared backups", () => { sharedRecoveryCluster = null; clusterSharedBackups = true; Render(); }));
        content.Children.Add(Label("Shared restore recovery", 28, true));
        content.Children.Add(ActionButton(sharedRecoveryLoading ? "Loading…" : "Refresh records", designPreview || client is null || sharedRecoveryLoading
            ? null : () => _ = LoadSharedRecoveryAsync(cluster)));
        if (sharedRecoveryNotice.Length > 0) content.Children.Add(Label(sharedRecoveryNotice, 16));
        if (sharedRecoveryRecords is null) { content.Children.Add(Surface(Label("Load shared restore records when connected.", 17))); return; }
        if (sharedRecoveryRecords.Count == 0) { content.Children.Add(Surface(Label("No shared restore operations recorded.", 20, true))); return; }
        var completed = new StackPanel { Spacing = 12 };
        foreach (var record in sharedRecoveryRecords.OrderByDescending(r => r.RuntimeHour))
        {
            var row = new StackPanel { Spacing = 12 };
            row.Children.Add(Label("Runtime point " + record.RuntimeHour + " · " + RestorePhaseLabel(record.Phase), 20, true));
            var view = sharedRollbackViews.GetValueOrDefault(record.OperationId);
            var repairable = record.Phase is WorldRestorePhase.Replacing or WorldRestorePhase.RollingBack or WorldRestorePhase.RolledBack;
            if (repairable)
            {
                var note = Label(record.Phase == WorldRestorePhase.RolledBack ? "Recovery is complete. Temporary-file cleanup can be retried."
                    : "Stop every cluster map before recovery. Shared data returns to its state before this interrupted restore; worlds and settings stay as they are.", 16);
                note.TextWrapping = TextWrapping.Wrap; row.Children.Add(note);
                row.Children.Add(ActionButton(view?.Busy == true ? "Recovering…" : view?.OperationId is not null ? "Check recovery result"
                    : record.Phase == WorldRestorePhase.RolledBack ? "Retry cleanup" : "Recover previous shared data…",
                    designPreview || client is null || view?.Busy == true ? null : () => _ = RollbackSharedAsync(cluster, record)));
            }
            if (view?.Notice is { Length: > 0 } notice)
            { var text = Label(notice, 15); text.TextWrapping = TextWrapping.Wrap; row.Children.Add(text); }
            if (repairable) content.Children.Add(Surface(row)); else completed.Children.Add(Surface(row));
        }
        if (completed.Children.Count > 0) content.Children.Add(new Expander { Header = "Other restore records", Content = completed });
    }

    private async Task LoadSharedRecoveryAsync(ClusterDefinition cluster)
    {
        if (client is null || designPreview || lifetime.IsCancellationRequested) return;
        var revision = ++sharedRecoveryRevision; sharedRecoveryLoading = true; sharedRecoveryNotice = ""; sharedRecoveryRecords = null; Render();
        try
        {
            var records = await client.CallAsync<List<SharedTransferRestoreRecord>>("shared-transfer-restore-records", cluster.Id,
                TimeSpan.FromSeconds(8), lifetime.Token);
            if (records.Any(r => r is null || r.ClusterId != cluster.Id || r.Edition != cluster.Edition || r.OperationId == Guid.Empty
                || r.RuntimeHour < 1 || !Enum.IsDefined(r.Phase))) throw new InvalidDataException();
            if (revision == sharedRecoveryRevision && sharedRecoveryCluster == cluster.Id) sharedRecoveryRecords = records;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or InvalidOperationException or JsonException or OperationCanceledException)
        { if (revision == sharedRecoveryRevision) sharedRecoveryNotice = "Could not load shared restore records. Refresh to try again."; }
        finally { if (revision == sharedRecoveryRevision) { sharedRecoveryLoading = false; if (!lifetime.IsCancellationRequested) Render(); } }
    }

    private async Task RollbackSharedAsync(ClusterDefinition cluster, SharedTransferRestoreRecord record)
    {
        if (client is null || designPreview || lifetime.IsCancellationRequested) return;
        if (!sharedRollbackViews.TryGetValue(record.OperationId, out var view)) sharedRollbackViews[record.OperationId] = view = new();
        if (view.Busy) return;
        view.Busy = true;
        try
        {
            if (view.OperationId is null)
            {
                var dialog = new Window { Title = "Recover previous shared data?", Width = 580, Height = 400,
                    Background = Brush.Parse("#0F1921"), FontFamily = FontFamily, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                var panel = new StackPanel { Margin = new Thickness(28), Spacing = 18 };
                panel.Children.Add(Label(record.Phase == WorldRestorePhase.RolledBack ? "Retry temporary cleanup?" : "Recover previous shared data?", 26, true));
                var note = Label(record.Phase == WorldRestorePhase.RolledBack ? "Every cluster map must be stopped. Only temporary restore files are removed; current shared data is preserved."
                    : cluster.Name + ": every map must be stopped. Recover shared data from before this interrupted restore. Map worlds and settings are preserved. All maps stay stopped.", 16);
                note.TextWrapping = TextWrapping.Wrap; panel.Children.Add(note);
                panel.Children.Add(ActionButton("Continue", () => dialog.Close(true), true));
                panel.Children.Add(ActionButton("Cancel", () => dialog.Close(false))); dialog.Content = new ScrollViewer { Content = panel };
                if (!await dialog.ShowDialog<bool>(this)) return;
            }
            BackgroundOperation job;
            if (view.OperationId is Guid existing)
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(existing), TimeSpan.FromSeconds(8), lifetime.Token);
            else
            {
                view.OperationId = Guid.NewGuid();
                job = await client.CallAsync<BackgroundOperation>("shared-transfer-rollback-start", new SharedRollbackRequest(cluster.Id, record.OperationId, true),
                    TimeSpan.FromSeconds(8), lifetime.Token, view.OperationId);
            }
            while (true)
            {
                if (job.Id != view.OperationId || job.Kind != "shared-transfer-rollback" || !Enum.IsDefined(job.State)) throw new InvalidDataException();
                if (job.State is not (BackgroundOperationState.Queued or BackgroundOperationState.Running)) break;
                view.Notice = "Recovering previous shared data…"; Render(); await Task.Delay(750, lifetime.Token);
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(view.OperationId!.Value), TimeSpan.FromSeconds(8), lifetime.Token);
            }
            if (job.State == BackgroundOperationState.Succeeded)
            {
                var result = job.Result?.Deserialize<SharedTransferRestoreRecord>();
                if (result is null || result.ClusterId != cluster.Id || result.Edition != cluster.Edition || result.OperationId != record.OperationId
                    || result.RuntimeHour != record.RuntimeHour || result.Phase != WorldRestorePhase.RolledBack) throw new InvalidDataException();
                view.Notice = "Previous shared data recovered. The maps remain stopped.";
            }
            else view.Notice = "Recovery did not complete. Keep the cluster stopped and review its restore records.";
            view.OperationId = null; await LoadSharedRecoveryAsync(cluster);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or InvalidOperationException or JsonException or OperationCanceledException)
        { view.Notice = "Recovery outcome is unknown. Check the existing request before another attempt."; }
        finally { view.Busy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }
}
