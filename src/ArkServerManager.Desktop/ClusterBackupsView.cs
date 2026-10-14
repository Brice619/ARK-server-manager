using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private Guid? clusterBackupScope;
    private bool clusterSharedBackups;
    private int? clusterRestorePreviewHour;
    private DateTimeOffset clusterBackupPreviewTime;
    private AutomaticBackupState? sharedClusterHistory;
    private bool sharedHistoryLoading;
    private string sharedHistoryNotice = "";
    private long sharedHistoryRevision;
    private bool reviewSharedClusterHistory = false;
    private sealed class SharedArchiveCheck
    {
        public Guid? OperationId;
        public bool Busy;
        public string Notice = "";
        public SharedBackupVerificationResult? Result;
    }
    private readonly Dictionary<(Guid Cluster, long Hour), SharedArchiveCheck> sharedArchiveChecks = new();

    private void RenderClusterBackups(ClusterDefinition cluster, ServerDefinition[] members)
    {
        if (clusterBackupScope != cluster.Id)
        {
            clusterBackupScope = cluster.Id; clusterSharedBackups = false;
            clusterRestorePreviewHour = null;
            sharedClusterHistory = null; sharedHistoryNotice = ""; sharedHistoryLoading = false;
            sharedHistoryRevision++;
            coordinatedHistory = null; coordinatedPoint = null; coordinatedLoading = false; coordinatedNotice = "";
            coordinatedRevision++;
            if (!designPreview && client is not null) _ = LoadSharedClusterHistoryAsync(cluster);
            if (!designPreview && client is not null) _ = LoadCoordinatedHistoryAsync(cluster);
            var now = DateTimeOffset.Now;
            clusterBackupPreviewTime = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset);
        }
        content.Children.Add(Label("Cluster restore points", 32, true));
        var subtitle = Label("Maps and shared user data have separate backups at coordinated times.", 17);
        subtitle.Foreground = Muted; content.Children.Add(subtitle);
        var categories = new WrapPanel { Margin = new Thickness(0, 22, 0, 16) };
        categories.Children.Add(ActionButton("Map backups", () => { clusterSharedBackups = false; Render(); }, !clusterSharedBackups));
        categories.Children.Add(ActionButton("Shared user data", () => { clusterSharedBackups = true; Render(); }, clusterSharedBackups));
        content.Children.Add(categories);
        AddPendingSharedRestoreReview(cluster);
        if (pendingClusterRestore is { } pending && pending.ClusterId == cluster.Id && coordinatedPoint?.Capture.Id != pending.Capture.Id)
        {
            var pendingReview = new StackPanel { Spacing = 12 };
            pendingReview.Children.Add(Label("Cluster restore awaiting confirmation", 18, true));
            AddClusterRestoreReview(pendingReview, cluster, pending); content.Children.Add(Surface(pendingReview));
        }
        if (clusterRecoveryCluster == cluster.Id) { RenderClusterRecovery(cluster); return; }
        content.Children.Add(ActionButton("Cluster restore records", () => { clusterRecoveryCluster = cluster.Id; clusterRecoveryRecords = null; Render(); if (!designPreview && client is not null) _ = LoadClusterRecoveryAsync(cluster); }));
        if (sharedRecoveryCluster == cluster.Id) { RenderSharedRecovery(cluster); return; }
        if (reviewSharedClusterHistory && clusterSharedBackups) { RenderRecordedSharedClusterBackups(cluster); return; }
        if (reviewCoordinatedHistory && !clusterSharedBackups) { RenderRecordedCoordinatedBackups(cluster, members); return; }
        if (!designPreview)
        {
            if (clusterSharedBackups) { RenderRecordedSharedClusterBackups(cluster); return; }
            RenderRecordedCoordinatedBackups(cluster, members);
            return;
        }
        var rows = new StackPanel();
        var headings = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,Auto"), Margin = new Thickness(20, 8, 20, 16) };
        headings.Children.Add(Label("Time", 15));
        var contents = Label("Contents", 15); Grid.SetColumn(contents, 1); headings.Children.Add(contents);
        var status = Label("Status", 15); Grid.SetColumn(status, 2); headings.Children.Add(status);
        var action = Label("Action", 15); action.Width = 115; Grid.SetColumn(action, 3); headings.Children.Add(action);
        rows.Children.Add(headings);
        foreach (var offset in new[] { 1, 2, 3 })
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,Auto"), MinHeight = 76 };
            var timestamp = Label(clusterBackupPreviewTime.AddHours(-offset).ToString("g"), 18, true);
            timestamp.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(timestamp);
            var inventory = Label(clusterSharedBackups ? "Shared user data" : members.Length + " map snapshots", 16);
            inventory.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(inventory, 1); row.Children.Add(inventory);
            var state = Label("Illustrative · Not verified", 15); state.Foreground = Muted;
            state.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(state, 2); row.Children.Add(state);
            var inspect = ActionButton("Inspect  ›", () => { clusterRestorePreviewHour = offset; Render(); });
            inspect.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(inspect, 3); row.Children.Add(inspect);
            rows.Children.Add(new Border { BorderThickness = new Thickness(0, 1, 0, 0),
                BorderBrush = Avalonia.Media.Brush.Parse("#2B3A46"), Padding = new Thickness(20, 8), Child = row });
        }
        content.Children.Add(Surface(rows, new Thickness(14)));
        var note = Label("Design preview · These coordinated points have no backup files.", 13);
        note.Foreground = Muted; note.Margin = new Thickness(0, 16); content.Children.Add(note);
    }

    private void RenderRecordedSharedClusterBackups(ClusterDefinition cluster)
    {
        content.Children.Add(ActionButton("Restore records", designPreview || client is null ? null : () =>
        {
            sharedRecoveryCluster = cluster.Id; _ = LoadSharedRecoveryAsync(cluster); Render();
        }));
        content.Children.Add(ActionButton(sharedHistoryLoading ? "Loading…" : "Refresh",
            client is null || sharedHistoryLoading ? null : () => { _ = LoadSharedClusterHistoryAsync(cluster, true); }));
        if (sharedHistoryNotice.Length > 0) content.Children.Add(Label(sharedHistoryNotice, 16));
        if (sharedClusterHistory is null)
        {
            content.Children.Add(Surface(Label(sharedHistoryLoading ? "Loading shared backup history…"
                : "Shared backup history is unavailable. Refresh when connected.", 17)));
            return;
        }
        if (sharedClusterHistory.LastError is { Length: > 0 } error)
            content.Children.Add(new Expander { Header = "Shared backup needs attention", Content = Label(error, 15) });
        var rows = new StackPanel { Spacing = 12 };
        foreach (var entry in sharedClusterHistory.History.OrderByDescending(h => h.RuntimeHour))
        {
            var row = new StackPanel { Spacing = 7 };
            row.Children.Add(Label(entry.CreatedAt.ToLocalTime().ToString("g"), 20, true));
            var detail = Label("Shared user data · Cluster runtime hour " + entry.RuntimeHour
                + (designPreview ? " · Illustrative" : " · Recorded"), 15);
            detail.Foreground = Muted; row.Children.Add(detail);
            var check = sharedArchiveChecks.GetValueOrDefault((cluster.Id, entry.RuntimeHour));
            row.Children.Add(ActionButton(check?.Busy == true ? "Checking…" : check?.OperationId is not null
                ? "Check verification status" : "Verify archive", designPreview || client is null || check?.Busy == true
                ? null : () => { _ = CheckSharedArchiveAsync(cluster, entry); }));
            if (check?.Notice is { Length: > 0 } notice)
            {
                var message = Label(notice, 15); message.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
                row.Children.Add(message);
            }
            AddSharedRestoreAction(row, cluster, entry, check?.Result is not null);
            rows.Children.Add(Surface(row, new Thickness(24, 16)));
        }
        if (sharedClusterHistory.History.Count == 0)
            rows.Children.Add(Surface(Label("No shared restore points recorded yet.", 20, true)));
        content.Children.Add(rows);
        var note = Label("Verify an archive before restoring shared data. Every cluster map must be stopped; map worlds and settings are preserved.", 15);
        note.TextWrapping = Avalonia.Media.TextWrapping.Wrap; note.Foreground = Muted;
        note.Margin = new Thickness(0, 18); content.Children.Add(note);
    }

    private async Task LoadSharedClusterHistoryAsync(ClusterDefinition cluster, bool refresh = false)
    {
        if (client is null || lifetime.IsCancellationRequested) return;
        var revision = ++sharedHistoryRevision;
        sharedHistoryLoading = true; sharedHistoryNotice = "";
        if (refresh) Render();
        AutomaticBackupState? result = null; string notice = "";
        try
        {
            result = await client.CallAsync<AutomaticBackupState>("cluster-backups-history", new ClusterBackupHistoryRequest(cluster.Id),
                TimeSpan.FromSeconds(8), lifetime.Token);
            if (result.SchemaVersion != 1 || result.ServerId != cluster.Id || result.Edition != cluster.Edition
                || result.Scope != SnapshotScope.SharedCluster || result.History is null)
                throw new InvalidDataException("Unexpected shared backup history.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
        catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or OperationCanceledException
            or InvalidOperationException or System.Text.Json.JsonException)
        { notice = "Could not refresh shared backup history. Try again when connected."; }
        if (revision != sharedHistoryRevision || clusterBackupScope != cluster.Id || lifetime.IsCancellationRequested) return;
        sharedHistoryLoading = false; sharedHistoryNotice = notice;
        if (result is not null && notice.Length == 0) sharedClusterHistory = result;
        if (selectedCluster == cluster.Id && selectedServer is null && managementTab == 3
            && !settingsPage && !installationsPage && !creationPage && !activityPage) Render();
    }

    private async Task CheckSharedArchiveAsync(ClusterDefinition cluster, BackupHistoryEntry point)
    {
        if (client is null || designPreview || lifetime.IsCancellationRequested) return;
        var key = (cluster.Id, point.RuntimeHour);
        if (!sharedArchiveChecks.TryGetValue(key, out var check)) sharedArchiveChecks[key] = check = new();
        if (check.Busy) return;
        check.Busy = true; check.Notice = "Checking shared archive…"; check.Result = null;
        RefreshSharedCheck();
        try
        {
            BackgroundOperation job;
            if (check.OperationId is Guid existing)
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(existing),
                    TimeSpan.FromSeconds(8), lifetime.Token);
            else
            {
                check.OperationId = Guid.NewGuid();
                job = await client.CallAsync<BackgroundOperation>("cluster-backup-verify-start",
                    new SharedBackupVerificationRequest(cluster.Id, point.RuntimeHour), TimeSpan.FromSeconds(8), lifetime.Token, check.OperationId);
            }
            while (true)
            {
                if (job.Id != check.OperationId || job.Kind != "shared-backup-verification" || !Enum.IsDefined(job.State))
                    throw new InvalidDataException("Unexpected shared verification operation.");
                if (job.State is not (BackgroundOperationState.Queued or BackgroundOperationState.Running)) break;
                await Task.Delay(750, lifetime.Token);
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(check.OperationId!.Value),
                    TimeSpan.FromSeconds(8), lifetime.Token);
            }
            if (job.State != BackgroundOperationState.Succeeded || job.Result is null)
            {
                check.OperationId = null; check.Notice = "Shared archive verification did not succeed."; return;
            }
            var result = job.Result.Value.Deserialize<SharedBackupVerificationResult>()
                ?? throw new InvalidDataException("Missing shared verification result.");
            if (result.ClusterId != cluster.Id || result.RuntimeHour != point.RuntimeHour || result.FileCount < 0
                || result.TotalBytes < 0 || result.VerifiedAt == default
                || result.Capture is { } capture && (capture.Id == Guid.Empty || capture.CapturedAt == default))
                throw new InvalidDataException("Unexpected shared verification result.");
            check.Result = result; check.OperationId = null;
            check.Notice = "Verified " + result.VerifiedAt.ToLocalTime().ToString("g") + " · " + result.FileCount + " files";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or InvalidOperationException
            or System.Text.Json.JsonException or OperationCanceledException)
        { check.Notice = "Verification could not be confirmed. Check the same operation's status to continue."; }
        finally { check.Busy = false; RefreshSharedCheck(); }

        void RefreshSharedCheck()
        {
            if (!lifetime.IsCancellationRequested && selectedCluster == cluster.Id && selectedServer is null
                && managementTab == 3 && !settingsPage && !installationsPage && !creationPage && !activityPage) Render();
        }
    }

    private void RenderClusterRestorePreview(ClusterDefinition cluster, ServerDefinition[] members)
    {
        content.Children.Add(ActionButton("← Back to cluster backups", () => { clusterRestorePreviewHour = null; Render(); }));
        content.Children.Add(Label("Inspect restore point", 32, true));
        var summary = new StackPanel { Spacing = 12 };
        Fact(summary, "Cluster", cluster.Name);
        Fact(summary, "Time", clusterBackupPreviewTime.AddHours(-clusterRestorePreviewHour!.Value).ToString("g"));
        Fact(summary, "Contents", clusterSharedBackups ? "Shared user data only" : members.Length + " separate map snapshots");
        Fact(summary, "Verification", "Illustrative point · No archive files exist");
        content.Children.Add(Surface(summary));
        if (!clusterSharedBackups)
        {
            var maps = new StackPanel { Spacing = 12 }; maps.Children.Add(Label("Maps at this point", 23, true));
            foreach (var member in members) maps.Children.Add(Label(member.Settings.Name + " · " + MapName(member.Map), 16));
            content.Children.Add(new Expander { Header = "View included maps", Content = Surface(maps) });
        }
        var choices = new WrapPanel { Margin = new Thickness(0, 16, 0, 8) };
        if (!clusterSharedBackups) choices.Children.Add(ActionButton("Choose one map"));
        choices.Children.Add(ActionButton(clusterSharedBackups ? "Restore shared user data" : "Restore entire cluster", primary: true));
        content.Children.Add(choices);
        var note = Label("Restoration remains unavailable until the coordinated archives and shared data can be verified.", 15);
        note.Foreground = Muted; content.Children.Add(note);
    }
}
