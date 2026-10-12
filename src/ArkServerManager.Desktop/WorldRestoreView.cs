using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private WorldRestoreRequest? pendingWorldRestore;
    private BackupHistoryEntry? pendingWorldRestorePoint;
    private CoordinatedRestorePoint? pendingCoordinatedRestore;
    private Guid worldRestoreRequestId;
    private bool worldRestoreBusy, worldRestoreAttempted;
    private string worldRestoreNotice = "";

    private void AddWorldRestoreActions(StackPanel body, ServerDefinition server, BackupHistoryEntry point, BackupVerificationResult? verified,
        CoordinatedRestorePoint? coordinated = null)
    {
        if (worldRestoreNotice.Length > 0)
        { var notice = Label(worldRestoreNotice, 15); notice.TextWrapping = TextWrapping.Wrap; body.Children.Add(notice); }
        var matches = pendingWorldRestore is null || pendingWorldRestore.ServerId == server.Id && pendingWorldRestore.RuntimeHour == point.RuntimeHour
            && pendingCoordinatedRestore?.Capture.Id == coordinated?.Capture.Id;
        creationFooter.IsVisible = true;
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
        actions.Children.Add(ActionButton("Restore records", designPreview ? null : () =>
        {
            selectedServer = server.Id; managementTab = 3; recoveryViewServer = server.Id;
            _ = LoadRestoreRecoveryAsync(server); Render();
        }));
        actions.Children.Add(ActionButton(pendingWorldRestore is null ? "Restore world…" : "Check restore result",
            designPreview || client is null || worldRestoreBusy || !matches
                || pendingWorldRestore is null && verified?.Layout != SnapshotLayout.WorldSavedFolders ? null
                : () => _ = RestoreWorldAsync(server, point, coordinated), true));
        if (pendingWorldRestore is not null && !matches)
            actions.Children.Add(ActionButton("Return to pending restore", () =>
            {
                managementTab = 3;
                if (pendingCoordinatedRestore is { } captured)
                {
                    selectedServer = null; selectedCluster = captured.ClusterId; clusterBackupScope = captured.ClusterId;
                    clusterSharedBackups = false; coordinatedPoint = captured; coordinatedTargetChosen = true;
                    coordinatedTarget = pendingWorldRestore.ServerId;
                }
                else { selectedServer = pendingWorldRestore.ServerId; backupViewServer = pendingWorldRestore.ServerId; selectedRestorePoint = pendingWorldRestorePoint; }
                Render();
            }));
        if (pendingWorldRestore is not null && !worldRestoreBusy)
            actions.Children.Add(ActionButton("Dismiss unknown result", () =>
            {
                pendingWorldRestore = null; pendingWorldRestorePoint = null; pendingCoordinatedRestore = null;
                worldRestoreNotice = "The restore may still complete. Refresh server status and inspect restore records before another attempt.";
                Render();
            }));
        creationFooter.Children.Add(actions);
    }

    private async Task RestoreWorldAsync(ServerDefinition server, BackupHistoryEntry point, CoordinatedRestorePoint? coordinated = null)
    {
        if (client is null || worldRestoreBusy || designPreview) return;
        worldRestoreBusy = true;
        try
        {
            if (pendingWorldRestore is null)
            {
                var dialog = new Window { Title = "Restore this world?", Width = 580, Height = 450, CanResize = false,
                    Background = Brush.Parse("#0F1921"), FontFamily = FontFamily, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                var panel = new StackPanel { Margin = new Thickness(28), Spacing = 18 };
                panel.Children.Add(Label("Restore this world?", 27, true));
                var summary = Label($"{server.Settings.Name} · {MapName(server.Map)}\n{point.CreatedAt.ToLocalTime():g} · Runtime hour {point.RuntimeHour}\n\nOnly this map will stop. World and player data return to this point. Settings and shared cluster data stay as they are. Previous world data remains until a successful start.", 16);
                summary.TextWrapping = TextWrapping.Wrap; panel.Children.Add(summary);
                var restart = new CheckBox { Content = "Start this map after restoration", IsChecked = managedRuntimeStatus.Any(s => s.ServerId == server.Id && s.State == ServerState.Running) };
                panel.Children.Add(restart);
                panel.Children.Add(ActionButton("Restore world", () => dialog.Close(true), true));
                panel.Children.Add(ActionButton("Cancel", () => dialog.Close(false)));
                dialog.Content = new ScrollViewer { Content = panel };
                if (!await dialog.ShowDialog<bool>(this)) return;
                pendingWorldRestore = new(server.Id, point.RuntimeHour, restart.IsChecked == true, true);
                pendingWorldRestorePoint = point; worldRestoreRequestId = Guid.NewGuid(); worldRestoreAttempted = false;
                pendingCoordinatedRestore = coordinated;
            }
            BackgroundOperation job;
            if (!worldRestoreAttempted)
            {
                worldRestoreAttempted = true;
                if (pendingCoordinatedRestore is { } capture)
                    job = await client.CallAsync<BackgroundOperation>("coordinated-world-restore-start",
                        new CoordinatedWorldRestoreRequest(capture.ClusterId, pendingWorldRestore.ServerId, capture.RuntimeHour,
                            capture.Capture.Id, pendingWorldRestore.Restart, true), TimeSpan.FromSeconds(8), lifetime.Token, worldRestoreRequestId);
                else job = await client.CallAsync<BackgroundOperation>("server-world-restore-start", pendingWorldRestore,
                        TimeSpan.FromSeconds(8), lifetime.Token, worldRestoreRequestId);
            }
            else job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(worldRestoreRequestId), TimeSpan.FromSeconds(8), lifetime.Token);
            while (true)
            {
                if (job.Id != worldRestoreRequestId || job.Kind != (pendingCoordinatedRestore is null ? "server-world-restore" : "coordinated-world-restore")
                    || !Enum.IsDefined(job.State)) throw new InvalidDataException();
                if (job.State == BackgroundOperationState.Succeeded)
                {
                    var result = job.Result?.Deserialize<WorldRestoreOperationResult>();
                    if (result is null || result.ServerId != pendingWorldRestore.ServerId || result.RuntimeHour != pendingWorldRestore.RuntimeHour || result.Restore is null)
                        throw new InvalidDataException();
                    worldRestoreNotice = result.Restore.Message;
                    if (pendingCoordinatedRestore is { } completed) coordinatedChecks.Remove((completed.Capture.Id, server.Id));
                    pendingWorldRestore = null; pendingWorldRestorePoint = null; pendingCoordinatedRestore = null;
                    backupChecks.Remove((server.Id, point.RuntimeHour));
                    await RefreshAsync(); break;
                }
                if (job.State is BackgroundOperationState.Failed or BackgroundOperationState.Canceled)
                {
                    worldRestoreNotice = "Restore did not complete. Inspect server status and restore records before another attempt.";
                    if (pendingCoordinatedRestore is { } completed) coordinatedChecks.Remove((completed.Capture.Id, server.Id));
                    pendingWorldRestore = null; pendingWorldRestorePoint = null; pendingCoordinatedRestore = null;
                    backupChecks.Remove((server.Id, point.RuntimeHour)); await RefreshAsync(); break;
                }
                worldRestoreNotice = job.Progress?.Message ?? "Restoring the selected map…"; Render();
                await Task.Delay(1000, lifetime.Token);
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(worldRestoreRequestId), TimeSpan.FromSeconds(8), lifetime.Token);
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or InvalidOperationException or ArgumentException or JsonException or OperationCanceledException)
        { worldRestoreNotice = pendingWorldRestore is null ? "The restore review could not be completed." : "Restore outcome is unknown. Check the existing request; nothing will be resent."; }
        finally { worldRestoreBusy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }
}
