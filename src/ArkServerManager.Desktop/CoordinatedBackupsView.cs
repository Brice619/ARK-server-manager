using Avalonia;
using Avalonia.Controls;
using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private CoordinatedBackupHistory? coordinatedHistory;
    private CoordinatedRestorePoint? coordinatedPoint;
    private bool coordinatedLoading;
    private bool reviewCoordinatedHistory = false;
    private string coordinatedNotice = "";
    private long coordinatedRevision;
    private bool coordinatedTargetChosen;
    private Guid? coordinatedTarget;
    private sealed class CoordinatedCheck
    {
        public Guid? OperationId;
        public bool Busy;
        public string Notice = "";
        public CoordinatedArchiveVerification? Result;
    }
    private readonly Dictionary<(Guid Capture, Guid? Map), CoordinatedCheck> coordinatedChecks = new();

    private void RenderRecordedCoordinatedBackups(ClusterDefinition cluster, ServerDefinition[] members)
    {
        if (coordinatedPoint is { } selected)
        {
            content.Children.Add(ActionButton("← Back to restore points", () => { coordinatedPoint = null; Render(); }));
            var details = new StackPanel { Spacing = 12 };
            details.Children.Add(Label(selected.Capture.CapturedAt.ToLocalTime().ToString("g"), 24, true));
            details.Children.Add(Label("Cluster runtime hour " + selected.RuntimeHour
                + (designPreview ? " · Illustrative point" : " · Recorded, verification required"), 16));
            var maps = new StackPanel { Spacing = 12 };
            foreach (var id in selected.Maps)
            {
                var member = members.FirstOrDefault(s => s.Id == id);
                maps.Children.Add(Label(member is null ? "Removed map · Historical archive" : member.Settings.Name + " · " + MapName(member.Map), 16));
            }
            details.Children.Add(new Expander { Header = selected.Maps.Count + " map snapshots", Content = maps });
            details.Children.Add(Label("Shared transfer data was captured at the same point and is stored separately.", 16));
            var targets = members.Where(s => selected.Maps.Contains(s.Id)).Select(s => (Guid?)s.Id).Append(null).ToArray();
            var options = members.Where(s => selected.Maps.Contains(s.Id)).Select(s => s.Settings.Name + " · " + MapName(s.Map))
                .Append("Shared user data").ToArray();
            details.Children.Add(Label("Choose an archive to verify", 16));
            var chooser = new ComboBox { ItemsSource = options, PlaceholderText = "Choose one map or shared data",
                SelectedIndex = coordinatedTargetChosen ? Array.IndexOf(targets, coordinatedTarget) : -1 };
            chooser.SelectionChanged += (_, _) =>
            {
                if (chooser.SelectedIndex < 0) return;
                coordinatedTarget = targets[chooser.SelectedIndex]; coordinatedTargetChosen = true; Render();
            };
            details.Children.Add(chooser);
            var check = coordinatedTargetChosen ? coordinatedChecks.GetValueOrDefault((selected.Capture.Id, coordinatedTarget)) : null;
            details.Children.Add(ActionButton(check?.Busy == true ? "Checking archive…" : check?.OperationId is not null
                ? "Check verification status" : "Verify archive", !coordinatedTargetChosen || designPreview || client is null || check?.Busy == true
                ? null : () => { _ = CheckCoordinatedArchiveAsync(selected, coordinatedTarget); }));
            if (check?.Notice is { Length: > 0 } notice)
            {
                var message = Label(notice, 15); message.TextWrapping = Avalonia.Media.TextWrapping.Wrap; details.Children.Add(message);
            }
            if (coordinatedTargetChosen && coordinatedTarget is Guid target && members.FirstOrDefault(s => s.Id == target) is { } server)
            {
                var verified = check?.Result is { } result ? new BackupVerificationResult(server.Id, selected.RuntimeHour,
                    result.FileCount, result.TotalBytes, result.VerifiedAt) { Layout = SnapshotLayout.WorldSavedFolders } : null;
                AddWorldRestoreActions(details, server, new(selected.RuntimeHour, "Coordinated map archive", selected.Capture.CapturedAt), verified, selected);
            }
            else if (coordinatedTargetChosen && coordinatedTarget is null)
                AddSharedRestoreAction(details, cluster, new(selected.RuntimeHour, "Coordinated shared archive", selected.Capture.CapturedAt),
                    check?.Result is not null, selected.Capture.Id);
            AddClusterRestoreReview(details, cluster, selected);
            content.Children.Add(Surface(details));
            var note = Label(coordinatedTarget is null ? "Shared restoration requires every cluster map stopped. Map worlds and settings are preserved."
                : "Individual map restoration stops only the selected map. Settings and shared cluster data are preserved.", 16);
            note.TextWrapping = Avalonia.Media.TextWrapping.Wrap; note.Foreground = Muted; content.Children.Add(note);
            return;
        }
        content.Children.Add(ActionButton(coordinatedLoading ? "Loading…" : "Refresh",
            client is null || coordinatedLoading ? null : () => { _ = LoadCoordinatedHistoryAsync(cluster, true); }));
        if (coordinatedNotice.Length > 0) content.Children.Add(Label(coordinatedNotice, 16));
        if (coordinatedHistory is null)
        {
            content.Children.Add(Surface(Label(coordinatedLoading ? "Loading coordinated restore points…"
                : "Restore point history is unavailable. Refresh when connected.", 17)));
            AddOlderMapBackupAccess(cluster, members);
            return;
        }
        if (coordinatedHistory.LastError is { Length: > 0 } error)
            content.Children.Add(new Expander { Header = "Coordinated backup needs attention", Content = Label(error, 15) });
        var rows = new StackPanel { Spacing = 12 };
        foreach (var point in coordinatedHistory.Points.OrderByDescending(p => p.RuntimeHour))
        {
            var row = new StackPanel { Spacing = 8 };
            row.Children.Add(Label(point.Capture.CapturedAt.ToLocalTime().ToString("g"), 20, true));
            var summary = Label(point.Maps.Count + " maps · Runtime hour " + point.RuntimeHour
                + (designPreview ? " · Illustrative" : " · Recorded"), 15);
            summary.Foreground = Muted; row.Children.Add(summary);
            row.Children.Add(ActionButton("Inspect  ›", () => { coordinatedPoint = point; coordinatedTargetChosen = false; Render(); }));
            rows.Children.Add(Surface(row, new Thickness(24, 16)));
        }
        if (coordinatedHistory.Points.Count == 0) rows.Children.Add(Surface(Label("No coordinated restore points recorded yet.", 20, true)));
        content.Children.Add(rows);
        AddOlderMapBackupAccess(cluster, members);
    }

    private void AddOlderMapBackupAccess(ClusterDefinition cluster, ServerDefinition[] members)
    {
        var older = new StackPanel { Spacing = 12 };
        older.Children.Add(Label("Older independent backups keep their original map runtime points. Choose a map to inspect and verify its recorded archives.", 16));
        foreach (var member in members)
            older.Children.Add(ActionButton(member.Settings.Name + " · " + MapName(member.Map), () =>
            {
                selectedCluster = cluster.Id; selectedServer = member.Id; managementTab = 3;
                recoveryViewServer = null; selectedRestorePoint = null; backupViewServer = null; Render();
            }));
        content.Children.Add(new Expander { Header = "Older per-map backups", Content = older });
    }

    private async Task CheckCoordinatedArchiveAsync(CoordinatedRestorePoint point, Guid? map)
    {
        if (client is null || designPreview || lifetime.IsCancellationRequested) return;
        var key = (point.Capture.Id, map);
        if (!coordinatedChecks.TryGetValue(key, out var check)) coordinatedChecks[key] = check = new();
        if (check.Busy) return;
        check.Busy = true; check.Result = null; check.Notice = "Checking archive…"; Refresh();
        try
        {
            BackgroundOperation job;
            if (check.OperationId is Guid existing)
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(existing), TimeSpan.FromSeconds(8), lifetime.Token);
            else
            {
                check.OperationId = Guid.NewGuid();
                job = await client.CallAsync<BackgroundOperation>("coordinated-backup-verify-start",
                    new CoordinatedArchiveRequest(point.ClusterId, point.RuntimeHour, map), TimeSpan.FromSeconds(8), lifetime.Token, check.OperationId);
            }
            while (true)
            {
                if (job.Id != check.OperationId || job.Kind != "coordinated-backup-verification" || !Enum.IsDefined(job.State))
                    throw new InvalidDataException("Unexpected coordinated verification operation.");
                if (job.State is not (BackgroundOperationState.Queued or BackgroundOperationState.Running)) break;
                await Task.Delay(750, lifetime.Token);
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(check.OperationId!.Value), TimeSpan.FromSeconds(8), lifetime.Token);
            }
            if (job.State != BackgroundOperationState.Succeeded || job.Result is null)
            { check.OperationId = null; check.Notice = "Archive verification did not succeed."; return; }
            var result = job.Result.Value.Deserialize<CoordinatedArchiveVerification>() ?? throw new InvalidDataException("Missing verification result.");
            if (result.ClusterId != point.ClusterId || result.ServerId != map || result.RuntimeHour != point.RuntimeHour
                || result.Capture != point.Capture || result.FileCount < 0 || result.TotalBytes < 0 || result.VerifiedAt == default)
                throw new InvalidDataException("Unexpected coordinated archive verification result.");
            check.Result = result; check.OperationId = null;
            check.Notice = "Verified " + result.VerifiedAt.ToLocalTime().ToString("g") + " · " + result.FileCount + " files";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or InvalidOperationException
            or JsonException or OperationCanceledException)
        { check.Notice = "Verification could not be confirmed. Check the same operation's status to continue."; }
        finally { check.Busy = false; Refresh(); }
        void Refresh()
        {
            if (!lifetime.IsCancellationRequested && selectedCluster == point.ClusterId && selectedServer is null
                && managementTab == 3 && !settingsPage && !installationsPage && !creationPage && !activityPage) Render();
        }
    }

    private async Task LoadCoordinatedHistoryAsync(ClusterDefinition cluster, bool refresh = false)
    {
        if (client is null || lifetime.IsCancellationRequested) return;
        var revision = ++coordinatedRevision; coordinatedLoading = true; coordinatedNotice = "";
        if (refresh) Render();
        CoordinatedBackupHistory? result = null; string notice = "";
        try
        {
            result = await client.CallAsync<CoordinatedBackupHistory>("coordinated-backups-history",
                new CoordinatedBackupHistoryRequest(cluster.Id), TimeSpan.FromSeconds(8), lifetime.Token);
            if (result.ClusterId != cluster.Id || result.Edition != cluster.Edition || result.RuntimeTicks < 0 || result.Points is null
                || result.Points.Any(p => p is null || p.ClusterId != cluster.Id || p.Edition != cluster.Edition
                    || p.Capture is null || p.Capture.Id == Guid.Empty || p.Capture.CapturedAt == default
                    || p.RuntimeHour < 1 || p.Maps is null || p.Maps.Count == 0))
                throw new InvalidDataException("Unexpected coordinated restore-point history.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
        catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or InvalidOperationException
            or System.Text.Json.JsonException or OperationCanceledException)
        { notice = "Could not refresh coordinated restore points. Try again when connected."; }
        if (revision != coordinatedRevision || clusterBackupScope != cluster.Id || lifetime.IsCancellationRequested) return;
        coordinatedLoading = false; coordinatedNotice = notice;
        if (result is not null && notice.Length == 0) { coordinatedHistory = result; coordinatedPoint = null; }
        if (selectedCluster == cluster.Id && selectedServer is null && managementTab == 3
            && !settingsPage && !installationsPage && !creationPage && !activityPage) Render();
    }
}
