using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private Guid? backupViewServer;
    private AutomaticBackupState? displayedBackups;
    private BackupHistoryEntry? selectedRestorePoint;
    private bool backupsLoading;
    private string backupsMessage = "";
    private long backupRequestRevision;
    private sealed class BackupCheckViewState
    {
        public Guid? OperationId;
        public bool Busy;
        public string Notice = "";
        public BackupVerificationResult? Result;
    }
    private readonly Dictionary<(Guid Server, long Hour), BackupCheckViewState> backupChecks = new();

    private void RenderServerBackups(ServerDefinition server)
    {
        if (backupViewServer != server.Id)
        {
            backupViewServer = server.Id; displayedBackups = null; selectedRestorePoint = null; backupsMessage = "";
            backupRequestRevision++; backupsLoading = false;
            if (designPreview)
            {
                var now = DateTimeOffset.Now;
                displayedBackups = new(1, server.Id, server.Edition, TimeSpan.FromHours(8).Ticks, null, null,
                    [new(8, "hour-00000008.zip", now.AddHours(-1)), new(7, "hour-00000007.zip", now.AddHours(-2)),
                        new(6, "hour-00000006.zip", now.AddHours(-3))], null);
            }
            else if (client is not null) _ = LoadBackupsAsync(server);
        }
        if (recoveryViewServer == server.Id) { RenderRestoreRecovery(server); return; }
        if (selectedRestorePoint is { } point) { RenderRestoreReview(server, point); return; }
        content.Children.Add(Label("Backups", 32, true));
        var subtitle = Label(server.ClusterId is not null ? "Older independent map restore points · Current backups are under the cluster"
            : "Hourly while running · Latest ten runtime-hour positions", 17); subtitle.Foreground = Muted; content.Children.Add(subtitle);
        if (server.ClusterId is Guid owner)
            content.Children.Add(ActionButton("View current cluster backups", () =>
            { selectedServer = null; selectedCluster = owner; managementTab = 3; clusterSharedBackups = false; coordinatedPoint = null; Render(); }));
        content.Children.Add(ActionButton("Review restore recovery", () => { recoveryViewServer = server.Id; _ = LoadRestoreRecoveryAsync(server); Render(); }));
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 18, 18, 10) };
        heading.Children.Add(Label("Restore points", 24, true));
        var reload = ActionButton(backupsLoading ? "Loading…" : "Refresh", client is null || backupsLoading
            ? null : () => { _ = LoadBackupsAsync(server, refreshView: true); });
        Grid.SetColumn(reload, 1); heading.Children.Add(reload); content.Children.Add(heading);
        if (backupsMessage.Length > 0) { var message = Label(backupsMessage, 16); message.Foreground = Amber; content.Children.Add(message); }
        if (displayedBackups is null)
        {
            content.Children.Add(Surface(Label(backupsLoading ? "Loading recorded backup history…" : "Backup history is unavailable. Refresh when the manager is connected.", 17)));
            return;
        }
        if (displayedBackups.LastError is { Length: > 0 } error)
        {
            content.Children.Add(new Expander { Header = "Backup needs attention", Content = Label(error, 15) });
        }
        if (displayedBackups.History.Count == 0)
        {
            var empty = new StackPanel { Spacing = 12 }; empty.Children.Add(Label("No restore points recorded", 24, true));
            empty.Children.Add(Label(server.ClusterId is not null ? "No older independent backups exist for this map. View the cluster for current restore points."
                : "Automatic backups will appear here after running time has reached a backup interval.", 16));
            content.Children.Add(Surface(empty));
        }
        else
        {
            var rows = new StackPanel { Spacing = 12 };
            foreach (var entry in displayedBackups.History.OrderByDescending(h => h.RuntimeHour))
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,Auto"), MinHeight = 66 };
                var timestamp = new StackPanel { Spacing = 7 };
                timestamp.Children.Add(Label(entry.CreatedAt.ToLocalTime().ToString("g"), 18, true));
                var runtime = Label("Runtime hour " + entry.RuntimeHour, 14); runtime.Foreground = Muted; timestamp.Children.Add(runtime); row.Children.Add(timestamp);
                var state = Label(designPreview ? "Illustrative restore point" : "Recorded · Verification required", 16);
                state.Foreground = Muted; state.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(state, 1); row.Children.Add(state);
                var inspect = ActionButton("Inspect  ›", () => { selectedRestorePoint = entry; Render(); });
                inspect.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(inspect, 2); row.Children.Add(inspect);
                rows.Children.Add(Surface(row, new Thickness(24, 16)));
            }
            content.Children.Add(rows);
        }
        var note = Label(designPreview ? "Design preview · These restore points have no backup files."
            : "Recorded history does not prove archive integrity. Archives must be verified before restoration.", 13);
        note.Foreground = Muted; note.Margin = new Thickness(0, 18); content.Children.Add(note);
        content.Children.Add(new Expander { Header = "Backup activity summary", Content = Label("Stopped time does not age backups. Failed backups remain scheduled for retry; failures are reported without stopping the server.", 16) });
    }

    private async Task LoadBackupsAsync(ServerDefinition server, bool refreshView = false)
    {
        if (client is null || lifetime.IsCancellationRequested) return;
        var revision = ++backupRequestRevision; backupsLoading = true; backupsMessage = "";
        if (refreshView) Render();
        AutomaticBackupState? result = null; string message = "";
        try
        {
            result = await client.CallAsync<AutomaticBackupState>("backups-history", new BackupHistoryRequest(server.Id),
                TimeSpan.FromSeconds(8), lifetime.Token);
            if (result.SchemaVersion != 1 || result.ServerId != server.Id || result.Edition != server.Edition || result.History is null)
                throw new InvalidDataException("Unexpected backup history.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
        catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or OperationCanceledException
            or InvalidOperationException or System.Text.Json.JsonException)
        { message = "Could not load backup history. Refresh to try again."; }
        if (revision != backupRequestRevision || lifetime.IsCancellationRequested) return;
        backupsLoading = false; backupsMessage = message;
        if (result is not null && message.Length == 0) displayedBackups = result;
        if (selectedServer == server.Id && managementTab == 3 && !settingsPage && !installationsPage && !creationPage && !activityPage) Render();
    }

    private void RenderRestoreReview(ServerDefinition server, BackupHistoryEntry point)
    {
        var check = backupChecks.GetValueOrDefault((server.Id, point.RuntimeHour));
        content.Children.Add(ActionButton("←  Back to backups", () => { selectedRestorePoint = null; Render(); }));
        content.Children.Add(Label("Restore your ARK", 32, true));
        var subtitle = Label("Review this restore point before continuing.", 17); subtitle.Foreground = Muted; content.Children.Add(subtitle);
        var body = new StackPanel { Spacing = 14 };
        var details = new StackPanel { Spacing = 6 };
        var target = Label(server.Settings.Name + " · " + server.Edition + " · " + MapName(server.Map), 20, true);
        target.TextWrapping = TextWrapping.Wrap; details.Children.Add(target);
        details.Children.Add(Label(point.CreatedAt.ToLocalTime().ToString("g") + " · Runtime hour " + point.RuntimeHour, 16));
        var verification = Label(designPreview ? "Illustrative point · No archive"
            : check?.Result is { } verified ? "Verified " + verified.VerifiedAt.ToLocalTime().ToString("g") : "Not checked", 15);
        verification.Foreground = Muted; details.Children.Add(verification);
        body.Children.Add(details);
        var warning = Label("Only this map stops. Its world and player state will be replaced; settings and shared cluster data are preserved.", 17);
        warning.TextWrapping = TextWrapping.Wrap;
        warning.Foreground = Amber; body.Children.Add(Surface(warning));
        body.Children.Add(new Expander { Header = "Archive details", Content = Label(point.FileName, 15) });
        body.Children.Add(ActionButton(check?.Busy == true ? "Checking archive…" : check?.OperationId is not null ? "Reconnect to verification" : "Verify archive",
            designPreview || client is null || check?.Busy == true ? null : () => { _ = CheckBackupAsync(server, point); }, true));
        if (check?.Busy == true) body.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 5 });
        if (check?.Notice is { Length: > 0 } notice) body.Children.Add(Label(notice, 15));
        if (check?.OperationId is not null && !check.Busy)
            body.Children.Add(new Expander { Header = "Connection options", Content = ActionButton("Start a new archive check", () =>
            { check.OperationId = null; _ = CheckBackupAsync(server, point); }) });
        if (check?.Result is { } result)
            body.Children.Add(Label(result.FileCount + " files checked · " + result.TotalBytes.ToString("N0") + " bytes. Restoration will verify the archive again.", 14));
        AddWorldRestoreActions(body, server, point, check?.Result);
        content.Children.Add(Surface(body, new Thickness(30)));
    }

    private async Task CheckBackupAsync(ServerDefinition server, BackupHistoryEntry point)
    {
        if (client is null || designPreview || lifetime.IsCancellationRequested) return;
        var key = (server.Id, point.RuntimeHour);
        if (!backupChecks.TryGetValue(key, out var view)) backupChecks[key] = view = new();
        if (view.Busy) return;
        view.Busy = true; view.Notice = "Checking archive contents and checksums."; view.Result = null;
        void UpdateView()
        {
            if (!lifetime.IsCancellationRequested && selectedServer == server.Id && managementTab == 3
                && selectedRestorePoint?.RuntimeHour == point.RuntimeHour && !installationsPage && !settingsPage && !creationPage && !activityPage) Render();
        }
        UpdateView();
        try
        {
            BackgroundOperation job;
            if (view.OperationId is Guid existing)
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(existing), TimeSpan.FromSeconds(8), lifetime.Token);
            else
            {
                view.OperationId = Guid.NewGuid();
                job = await client.CallAsync<BackgroundOperation>("backup-verify-start", new BackupVerificationRequest(server.Id, point.RuntimeHour),
                    TimeSpan.FromSeconds(8), lifetime.Token, view.OperationId);
            }
            while (true)
            {
                if (job.Id != view.OperationId || job.Kind != "backup-verification" || !Enum.IsDefined(job.State))
                    throw new InvalidDataException("Unexpected archive verification operation.");
                if (job.State is not (BackgroundOperationState.Queued or BackgroundOperationState.Running)) break;
                await Task.Delay(750, lifetime.Token);
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(view.OperationId!.Value),
                    TimeSpan.FromSeconds(8), lifetime.Token);
            }
            view.OperationId = null;
            if (job.State != BackgroundOperationState.Succeeded || job.Result is null)
            { view.Notice = "Archive verification did not succeed. This point is not ready for restoration."; return; }
            var result = job.Result.Value.Deserialize<BackupVerificationResult>() ?? throw new InvalidDataException("Missing verification result.");
            if (result.ServerId != server.Id || result.RuntimeHour != point.RuntimeHour || result.FileCount < 0 || result.TotalBytes < 0 || result.VerifiedAt == default)
                throw new InvalidDataException("Unexpected archive verification result.");
            view.Result = result; view.Notice = result.Layout == SnapshotLayout.WorldSavedFolders
                ? "World archive verified. Restore checks it again before stopping the map."
                : "Archive verified, but its world-folder layout is unspecified. It needs review before restoration.";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or InvalidOperationException or JsonException)
        { view.Notice = view.OperationId is not null ? "Verification could not be confirmed. Reconnect to check the same operation."
            : "The verification result could not be accepted. Check the archive again."; }
        finally { view.Busy = false; UpdateView(); }
    }
}

