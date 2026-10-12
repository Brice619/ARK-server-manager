using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private CoordinatedRestorePoint? pendingClusterRestore;
    private Guid clusterRestoreRequestId;
    private bool clusterRestoreBusy;
    private readonly Dictionary<Guid, string> clusterRestoreNotices = new();

    private void AddClusterRestoreReview(StackPanel body, ClusterDefinition cluster, CoordinatedRestorePoint point)
    {
        var panel = new StackPanel { Spacing = 12 };
        var description = Label("Restore every map and shared transfer data together. All maps must be stopped. Each archive is checked before replacement; settings stay current.", 16);
        description.TextWrapping = TextWrapping.Wrap; panel.Children.Add(description);
        if (clusterRestoreNotices.GetValueOrDefault(point.Capture.Id) is { Length: > 0 } notice)
        { var text = Label(notice, 15); text.TextWrapping = TextWrapping.Wrap; panel.Children.Add(text); }
        var matches = pendingClusterRestore is null || pendingClusterRestore.Capture.Id == point.Capture.Id;
        var currentMaps = catalog!.Servers.Where(s => s.ClusterId == cluster.Id).Select(s => s.Id).OrderBy(id => id);
        var membershipMatches = point.Maps.OrderBy(id => id).SequenceEqual(currentMaps);
        if (!membershipMatches) panel.Children.Add(Label("Cluster maps have changed. Use individual map or shared-data restoration for this point.", 15));
        panel.Children.Add(ActionButton(clusterRestoreBusy ? "Restoring…" : pendingClusterRestore is null ? "Restore entire cluster…" : "Check cluster restore result",
            designPreview || client is null || clusterRestoreBusy || !matches || pendingClusterRestore is null && !membershipMatches
                ? null : () => _ = RestoreClusterAsync(cluster, point), true));
        if (!matches) panel.Children.Add(ActionButton("Return to pending cluster restore", () =>
        {
            selectedServer = null; selectedCluster = pendingClusterRestore!.ClusterId; managementTab = 3;
            clusterBackupScope = pendingClusterRestore.ClusterId; clusterSharedBackups = false; coordinatedPoint = pendingClusterRestore;
            coordinatedTargetChosen = false; Render();
        }));
        body.Children.Add(new Expander { Header = "Whole cluster restore", Content = panel });
    }

    private async Task RestoreClusterAsync(ClusterDefinition cluster, CoordinatedRestorePoint point)
    {
        if (designPreview || client is null || clusterRestoreBusy || lifetime.IsCancellationRequested) return;
        if (pendingClusterRestore is not null && pendingClusterRestore.Capture.Id != point.Capture.Id) return;
        clusterRestoreBusy = true;
        try
        {
            BackgroundOperation job;
            if (pendingClusterRestore is null)
            {
                var dialog = new Window { Title = "Restore entire cluster?", Width = 600, Height = 440, CanResize = false,
                    Background = Brush.Parse("#0F1921"), FontFamily = FontFamily, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                var panel = new StackPanel { Margin = new Thickness(28), Spacing = 18 };
                panel.Children.Add(Label("Restore entire cluster?", 27, true));
                var text = Label($"{cluster.Name} · {point.Maps.Count} maps\n{point.Capture.CapturedAt.ToLocalTime():g}\n\nStop all cluster maps before continuing. Every world and shared transfer data will return to this point. Settings are preserved. Maps remain stopped, and previous data is retained until every map starts successfully.", 16);
                text.TextWrapping = TextWrapping.Wrap; panel.Children.Add(text);
                panel.Children.Add(ActionButton("Restore entire cluster", () => dialog.Close(true), true));
                panel.Children.Add(ActionButton("Cancel", () => dialog.Close(false))); dialog.Content = new ScrollViewer { Content = panel };
                if (!await dialog.ShowDialog<bool>(this)) return;
                pendingClusterRestore = point; clusterRestoreRequestId = Guid.NewGuid();
                job = await client.CallAsync<BackgroundOperation>("cluster-world-restore-start",
                    new CoordinatedClusterRestoreRequest(cluster.Id, point.RuntimeHour, point.Capture.Id, true),
                    TimeSpan.FromSeconds(8), lifetime.Token, clusterRestoreRequestId);
            }
            else job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(clusterRestoreRequestId),
                TimeSpan.FromSeconds(8), lifetime.Token);
            while (true)
            {
                if (job.Id != clusterRestoreRequestId || job.Kind != "cluster-world-restore" || !Enum.IsDefined(job.State)) throw new InvalidDataException();
                if (job.State == BackgroundOperationState.Succeeded)
                {
                    var result = job.Result?.Deserialize<ClusterRestoreRecord>();
                    if (result is null || result.OperationId != clusterRestoreRequestId || result.ClusterId != point.ClusterId
                        || result.Edition != point.Edition || result.RuntimeHour != point.RuntimeHour || result.Capture != point.Capture
                        || result.Phase != ClusterRestorePhase.Restored || !result.Maps.OrderBy(id => id).SequenceEqual(point.Maps.OrderBy(id => id)))
                        throw new InvalidDataException();
                    clusterRestoreNotices[point.Capture.Id] = "Cluster restored. Maps remain stopped; previous data stays until all maps start successfully.";
                }
                else if (job.State is BackgroundOperationState.Failed or BackgroundOperationState.Canceled)
                    clusterRestoreNotices[point.Capture.Id] = "Restore did not complete. Keep the cluster stopped and review its restore records before continuing.";
                else
                {
                    clusterRestoreNotices[point.Capture.Id] = "Restoring the cluster…"; Render(); await Task.Delay(750, lifetime.Token);
                    job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(clusterRestoreRequestId),
                        TimeSpan.FromSeconds(8), lifetime.Token); continue;
                }
                pendingClusterRestore = null;
                foreach (var key in coordinatedChecks.Keys.Where(k => k.Capture == point.Capture.Id).ToArray()) coordinatedChecks.Remove(key);
                await RefreshAsync(); break;
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or InvalidOperationException
            or ArgumentException or JsonException or OperationCanceledException)
        { clusterRestoreNotices[point.Capture.Id] = pendingClusterRestore is null ? "Restore review could not be completed."
            : "Restore outcome is unknown. Check this existing request before another attempt."; }
        finally { clusterRestoreBusy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }
}
