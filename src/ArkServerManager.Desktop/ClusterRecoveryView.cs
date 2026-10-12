using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private Guid? clusterRecoveryCluster;
    private List<ClusterRestoreRecord>? clusterRecoveryRecords;
    private bool clusterRecoveryLoading;
    private long clusterRecoveryRevision;
    private string clusterRecoveryNotice = "";
    private readonly Dictionary<Guid, RollbackView> clusterRollbackViews = new();

    private static string ClusterRestorePhaseLabel(ClusterRestorePhase phase) => phase switch
    {
        ClusterRestorePhase.Preparing => "Preparation interrupted",
        ClusterRestorePhase.Staged => "Prepared · Not applied",
        ClusterRestorePhase.Replacing => "Restore interrupted",
        ClusterRestorePhase.RollingBack => "Recovery interrupted",
        ClusterRestorePhase.RolledBack => "Previous data recovered",
        ClusterRestorePhase.Restored => "Restored · Previous data retained",
        ClusterRestorePhase.Started => "Started · Cleanup pending",
        ClusterRestorePhase.Cleaned => "Completed",
        _ => "Needs review"
    };

    private void RenderClusterRecovery(ClusterDefinition cluster)
    {
        content.Children.Add(ActionButton("← Back to cluster backups", () => { clusterRecoveryCluster = null; clusterSharedBackups = false; Render(); }));
        content.Children.Add(Label("Cluster restore recovery", 28, true));
        content.Children.Add(ActionButton(clusterRecoveryLoading ? "Loading…" : "Refresh records", designPreview || client is null || clusterRecoveryLoading
            ? null : () => _ = LoadClusterRecoveryAsync(cluster)));
        if (clusterRecoveryNotice.Length > 0) content.Children.Add(Label(clusterRecoveryNotice, 16));
        if (clusterRecoveryRecords is null) { content.Children.Add(Surface(Label("Load cluster restore records when connected.", 17))); return; }
        if (clusterRecoveryRecords.Count == 0) { content.Children.Add(Surface(Label("No cluster restore operations recorded.", 20, true))); return; }
        var completed = new StackPanel { Spacing = 12 };
        foreach (var record in clusterRecoveryRecords.OrderByDescending(r => r.RuntimeHour))
        {
            var row = new StackPanel { Spacing = 12 };
            row.Children.Add(Label("Runtime point " + record.RuntimeHour + " · " + ClusterRestorePhaseLabel(record.Phase), 20, true));
            var view = clusterRollbackViews.GetValueOrDefault(record.OperationId);
            var preparationOnly = record.Phase is ClusterRestorePhase.Preparing or ClusterRestorePhase.Staged;
            var repairable = record.Phase is ClusterRestorePhase.Preparing or ClusterRestorePhase.Staged or ClusterRestorePhase.Replacing or ClusterRestorePhase.RollingBack or ClusterRestorePhase.RolledBack;
            if (repairable)
            {
                var note = Label(preparationOnly ? "Remove unfinished preparation files. Current worlds, shared data, and settings are preserved. All maps must be stopped." : record.Phase == ClusterRestorePhase.RolledBack ? "Recovery is complete. Temporary-file cleanup can be retried."
                    : "Stop every cluster map before recovery. Worlds and shared data return to its state before this interrupted restore; settings are preserved. All maps remain stopped.", 16);
                note.TextWrapping = TextWrapping.Wrap; row.Children.Add(note);
                row.Children.Add(ActionButton(view?.Busy == true ? "Recovering…" : view?.OperationId is not null ? "Check recovery result"
                    : preparationOnly ? "Discard preparation…" : record.Phase == ClusterRestorePhase.RolledBack ? "Retry cleanup" : "Recover previous cluster data…",
                    designPreview || client is null || view?.Busy == true ? null : () => _ = RecoverClusterAsync(cluster, record)));
            }
            if (view?.Notice is { Length: > 0 } notice)
            { var text = Label(notice, 15); text.TextWrapping = TextWrapping.Wrap; row.Children.Add(text); }
            if (repairable) content.Children.Add(Surface(row)); else completed.Children.Add(Surface(row));
        }
        if (completed.Children.Count > 0) content.Children.Add(new Expander { Header = "Other restore records", Content = completed });
    }

    private async Task LoadClusterRecoveryAsync(ClusterDefinition cluster)
    {
        if (client is null || designPreview || lifetime.IsCancellationRequested) return;
        var revision = ++clusterRecoveryRevision; clusterRecoveryLoading = true; clusterRecoveryNotice = ""; clusterRecoveryRecords = null; Render();
        try
        {
            var records = await client.CallAsync<List<ClusterRestoreRecord>>("cluster-restore-records", cluster.Id,
                TimeSpan.FromSeconds(8), lifetime.Token);
            if (records.Any(r => r is null || r.ClusterId != cluster.Id || r.Edition != cluster.Edition || r.OperationId == Guid.Empty
                || r.RuntimeHour < 1 || !Enum.IsDefined(r.Phase))) throw new InvalidDataException();
            if (revision == clusterRecoveryRevision && clusterRecoveryCluster == cluster.Id) clusterRecoveryRecords = records;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or InvalidOperationException or JsonException or OperationCanceledException)
        { if (revision == clusterRecoveryRevision) clusterRecoveryNotice = "Could not load cluster restore records. Refresh to try again."; }
        finally { if (revision == clusterRecoveryRevision) { clusterRecoveryLoading = false; if (!lifetime.IsCancellationRequested) Render(); } }
    }

    private async Task RecoverClusterAsync(ClusterDefinition cluster, ClusterRestoreRecord record)
    {
        if (client is null || designPreview || lifetime.IsCancellationRequested) return;
        if (!clusterRollbackViews.TryGetValue(record.OperationId, out var view)) clusterRollbackViews[record.OperationId] = view = new();
        if (view.Busy) return;
        view.Busy = true;
        try
        {
            var cleanupOnly = record.Phase is ClusterRestorePhase.Preparing or ClusterRestorePhase.Staged or ClusterRestorePhase.RolledBack;
            if (view.OperationId is null)
            {
                var dialog = new Window { Title = "Recover previous cluster data?", Width = 580, Height = 400,
                    Background = Brush.Parse("#0F1921"), FontFamily = FontFamily, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                var panel = new StackPanel { Margin = new Thickness(28), Spacing = 18 };
                panel.Children.Add(Label(cleanupOnly ? "Remove temporary restore files?" : "Recover previous cluster data?", 26, true));
                var note = Label(cleanupOnly ? "Every cluster map must be stopped. Only temporary restore files are removed; current worlds and shared data are preserved."
                    : cluster.Name + ": every map must be stopped. Recover worlds and shared data from before this interrupted restore. Current settings are preserved. All maps stay stopped.", 16);
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
                job = await client.CallAsync<BackgroundOperation>("cluster-restore-recovery-start", new ClusterRestoreRecoveryRequest(cluster.Id, record.OperationId, true),
                    TimeSpan.FromSeconds(8), lifetime.Token, view.OperationId);
            }
            while (true)
            {
                if (job.Id != view.OperationId || job.Kind != "cluster-restore-recovery" || !Enum.IsDefined(job.State)) throw new InvalidDataException();
                if (job.State is not (BackgroundOperationState.Queued or BackgroundOperationState.Running)) break;
                view.Notice = "Recovering previous cluster data…"; Render(); await Task.Delay(750, lifetime.Token);
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(view.OperationId!.Value), TimeSpan.FromSeconds(8), lifetime.Token);
            }
            if (job.State == BackgroundOperationState.Succeeded)
            {
                var result = job.Result?.Deserialize<ClusterRestoreRecord>();
                if (result is null || result.ClusterId != cluster.Id || result.Edition != cluster.Edition || result.OperationId != record.OperationId
                    || result.RuntimeHour != record.RuntimeHour || result.Capture != record.Capture || !result.Maps.Order().SequenceEqual(record.Maps.Order()) || result.Phase != ClusterRestorePhase.RolledBack) throw new InvalidDataException();
                view.Notice = cleanupOnly ? "Temporary restore files removed. The maps remain stopped." : "Previous cluster data recovered. The maps remain stopped.";
            }
            else view.Notice = "Recovery did not complete. Keep the cluster stopped and review its restore records.";
            view.OperationId = null; await LoadClusterRecoveryAsync(cluster);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or InvalidOperationException or JsonException or OperationCanceledException)
        { view.Notice = "Recovery outcome is unknown. Check the existing request before another attempt."; }
        finally { view.Busy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }
}


