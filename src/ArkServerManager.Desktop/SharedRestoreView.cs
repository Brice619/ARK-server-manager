using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private sealed class SharedRestoreReview
    {
        public Guid? OperationId;
        public bool Busy;
        public string Notice = "";
        public BackupHistoryEntry? Point;
    }
    private readonly Dictionary<(Guid Cluster, long Hour, Guid? Capture), SharedRestoreReview> sharedRestoreReviews = new();

    private void AddSharedRestoreAction(StackPanel body, ClusterDefinition cluster, BackupHistoryEntry point,
        bool verified, Guid? captureId = null)
    {
        var key = (cluster.Id, point.RuntimeHour, captureId);
        var review = sharedRestoreReviews.GetValueOrDefault(key);
        if (!verified && review is null) return;
        if (review?.Notice is { Length: > 0 } notice)
        {
            var text = Label(notice, 15); text.TextWrapping = TextWrapping.Wrap; body.Children.Add(text);
        }
        var anotherPending = sharedRestoreReviews.Any(pair => pair.Key != key && pair.Value.OperationId is not null);
        body.Children.Add(ActionButton(review?.Busy == true ? "Restoring…" : review?.OperationId is not null
            ? "Check restore result" : "Restore shared data…",
            designPreview || client is null || review?.Busy == true || anotherPending
                || review?.OperationId is null && !verified ? null
                : () => _ = RestoreSharedDataAsync(cluster, point, captureId), true));
        if (anotherPending) body.Children.Add(Label("Another shared restore is awaiting its result. Return to that restore point to check it.", 15));
    }

    private async Task RestoreSharedDataAsync(ClusterDefinition cluster, BackupHistoryEntry point, Guid? captureId)
    {
        if (client is null || designPreview || lifetime.IsCancellationRequested) return;
        var key = (cluster.Id, point.RuntimeHour, captureId);
        if (!sharedRestoreReviews.TryGetValue(key, out var review)) sharedRestoreReviews[key] = review = new();
        if (review.Busy || sharedRestoreReviews.Any(pair => pair.Key != key && pair.Value.OperationId is not null)) return;
        review.Busy = true;
        review.Point ??= point;
        try
        {
            if (review.OperationId is null)
            {
                var dialog = new Window { Title = "Restore shared cluster data?", Width = 580, Height = 450,
                    CanResize = false, Background = Brush.Parse("#0F1921"), FontFamily = FontFamily,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner };
                var panel = new StackPanel { Margin = new Thickness(28), Spacing = 18 };
                panel.Children.Add(Label("Restore shared cluster data?", 27, true));
                var summary = Label($"{cluster.Name}\n{point.CreatedAt.ToLocalTime():g} · Runtime hour {point.RuntimeHour}\n\nStop every map in this cluster before continuing. Shared player and transfer data will return to this point. Map worlds and settings stay as they are. The maps remain stopped. Previous shared data stays until all cluster maps start successfully.", 16);
                summary.TextWrapping = TextWrapping.Wrap; panel.Children.Add(summary);
                panel.Children.Add(ActionButton("Restore shared data", () => dialog.Close(true), true));
                panel.Children.Add(ActionButton("Cancel", () => dialog.Close(false)));
                dialog.Content = new ScrollViewer { Content = panel };
                if (!await dialog.ShowDialog<bool>(this)) return;
                review.OperationId = Guid.NewGuid();
                await Poll(await client.CallAsync<BackgroundOperation>("shared-transfer-restore-start",
                    new SharedRestoreRequest(cluster.Id, point.RuntimeHour, captureId, true),
                    TimeSpan.FromSeconds(8), lifetime.Token, review.OperationId));
            }
            else await Poll(await client.CallAsync<BackgroundOperation>("operation-status",
                new OperationStatusRequest(review.OperationId.Value), TimeSpan.FromSeconds(8), lifetime.Token));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or InvalidOperationException
            or ArgumentException or JsonException or OperationCanceledException)
        { review.Notice = review.OperationId is null ? "Restore review could not be completed."
            : "The restore outcome is unknown. Check this request's result before another attempt."; }
        finally { review.Busy = false; RefreshSharedRestore(); }

        async Task Poll(BackgroundOperation job)
        {
            while (true)
            {
                if (job.Id != review.OperationId || job.Kind != "shared-transfer-restore" || !Enum.IsDefined(job.State))
                    throw new InvalidDataException("Unexpected shared restore operation.");
                if (job.State == BackgroundOperationState.Succeeded)
                {
                    var result = job.Result?.Deserialize<SharedTransferRestoreRecord>();
                    if (result is null || result.OperationId != review.OperationId || result.ClusterId != cluster.Id
                        || result.Edition != cluster.Edition || result.RuntimeHour != point.RuntimeHour || result.Phase != WorldRestorePhase.Replaced)
                        throw new InvalidDataException("Unexpected shared restore result.");
                    review.Notice = "Shared data restored. The cluster maps remain stopped. Previous shared data is retained until all maps start successfully.";
                }
                else if (job.State is BackgroundOperationState.Failed or BackgroundOperationState.Canceled)
                    review.Notice = "Shared restoration did not complete. Check that all maps are stopped and review restore records before another attempt.";
                else
                {
                    review.Notice = "Restoring shared cluster data…"; RefreshSharedRestore();
                    await Task.Delay(750, lifetime.Token);
                    job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(review.OperationId!.Value),
                        TimeSpan.FromSeconds(8), lifetime.Token); continue;
                }
                review.OperationId = null;
                sharedArchiveChecks.Remove((cluster.Id, point.RuntimeHour));
                if (captureId is Guid captured) coordinatedChecks.Remove((captured, null));
                await RefreshAsync(); return;
            }
        }
        void RefreshSharedRestore()
        {
            if (!lifetime.IsCancellationRequested && selectedCluster == cluster.Id && selectedServer is null && managementTab == 3
                && !settingsPage && !installationsPage && !creationPage && !activityPage) Render();
        }
    }

    private void AddPendingSharedRestoreReview(ClusterDefinition cluster)
    {
        foreach (var pending in sharedRestoreReviews.Where(pair => pair.Key.Cluster == cluster.Id && pair.Value.OperationId is not null))
        {
            var row = new StackPanel { Spacing = 12 };
            row.Children.Add(Label("Shared restore awaiting confirmation · Runtime hour " + pending.Key.Hour, 18, true));
            if (pending.Value.Point is { } point)
                AddSharedRestoreAction(row, cluster, point, false, pending.Key.Capture);
            content.Children.Add(Surface(row));
        }
    }
}
