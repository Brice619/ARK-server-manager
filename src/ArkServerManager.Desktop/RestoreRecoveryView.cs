using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private Guid? recoveryViewServer;
    private List<WorldRestoreRecord>? recoveryRecords;
    private bool recoveryLoading;
    private string recoveryNotice = "";
    private long recoveryRevision;
    private sealed class RollbackView
    {
        public Guid? OperationId;
        public bool Busy;
        public string Notice = "";
    }
    private readonly Dictionary<Guid, RollbackView> rollbackViews = new();

    private void RenderRestoreRecovery(ServerDefinition server)
    {
        content.Children.Add(ActionButton("← Back to backups", () => { recoveryViewServer = null; Render(); }));
        content.Children.Add(Label("Restore recovery", 32, true));
        content.Children.Add(Label(server.Settings.Name + " · " + MapName(server.Map), 18));
        content.Children.Add(ActionButton(recoveryLoading ? "Loading…" : "Refresh records", client is null || recoveryLoading
            ? null : () => { _ = LoadRestoreRecoveryAsync(server); }));
        if (recoveryNotice.Length > 0) content.Children.Add(Label(recoveryNotice, 16));
        if (recoveryRecords is null) { content.Children.Add(Surface(Label("Load restore records when the manager is connected.", 17))); return; }
        if (recoveryRecords.Count == 0) { content.Children.Add(Surface(Label("No restore operations recorded for this map.", 20, true))); return; }
        foreach (var record in recoveryRecords.OrderBy(r => r.Phase is WorldRestorePhase.Replacing or WorldRestorePhase.RollingBack ? 0 : 1))
        {
            var row = new StackPanel { Spacing = 12 };
            row.Children.Add(Label("Runtime point " + record.RuntimeHour + " · " + RestorePhaseLabel(record.Phase), 20, true));
            var view = rollbackViews.GetValueOrDefault(record.OperationId);
            if (record.Phase is WorldRestorePhase.Replacing or WorldRestorePhase.RollingBack or WorldRestorePhase.RolledBack)
            {
                var note = Label(record.Phase == WorldRestorePhase.RolledBack ? "Previous world recovered. Temporary-file cleanup can be retried."
                    : "Stop this map before recovery. Rollback returns its world and players to the state before this restore attempt.", 16);
                note.TextWrapping = TextWrapping.Wrap; row.Children.Add(note);
                if (record.OriginalWorldFolders is null && record.Phase != WorldRestorePhase.RolledBack)
                    row.Children.Add(Label("This older record requires recovery review; original folder inventory is missing.", 15));
                else row.Children.Add(ActionButton(view?.Busy == true ? "Recovering…" : view?.OperationId is not null
                    ? "Check recovery result" : record.Phase == WorldRestorePhase.RolledBack ? "Retry cleanup" : "Recover previous world…",
                    designPreview || client is null || view?.Busy == true ? null : () => { _ = RollbackWorldAsync(server, record); }));
            }
            if (view?.Notice is { Length: > 0 } notice)
            { var message = Label(notice, 15); message.TextWrapping = TextWrapping.Wrap; row.Children.Add(message); }
            content.Children.Add(Surface(row));
        }
    }

    private async Task LoadRestoreRecoveryAsync(ServerDefinition server)
    {
        if (client is null || designPreview || lifetime.IsCancellationRequested) return;
        var revision = ++recoveryRevision; recoveryLoading = true; recoveryNotice = ""; recoveryRecords = null; Render();
        try
        {
            var records = await client.CallAsync<List<WorldRestoreRecord>>("server-world-restore-records", server.Id, TimeSpan.FromSeconds(8), lifetime.Token);
            if (records.Any(r => r is null || r.ServerId != server.Id || r.Edition != server.Edition
                || r.OperationId == Guid.Empty || r.RuntimeHour < 1 || !Enum.IsDefined(r.Phase))) throw new InvalidDataException();
            if (revision == recoveryRevision && recoveryViewServer == server.Id) recoveryRecords = records;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or InvalidOperationException or JsonException or OperationCanceledException)
        { if (revision == recoveryRevision) recoveryNotice = "Could not load restore records. Refresh to try again."; }
        finally { if (revision == recoveryRevision) { recoveryLoading = false; if (!lifetime.IsCancellationRequested) Render(); } }
    }

    private async Task RollbackWorldAsync(ServerDefinition server, WorldRestoreRecord record)
    {
        if (client is null || designPreview || lifetime.IsCancellationRequested) return;
        if (!rollbackViews.TryGetValue(record.OperationId, out var view)) rollbackViews[record.OperationId] = view = new();
        if (view.Busy) return;
        view.Busy = true;
        try
        {
            if (view.OperationId is null)
            {
                var dialog = new Window { Title = "Recover previous world?", Width = 560, Height = 360, Background = Brush.Parse("#0F1921"),
                    FontFamily = FontFamily, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                var panel = new StackPanel { Margin = new Thickness(28), Spacing = 18 };
                panel.Children.Add(Label("Recover previous world?", 26, true));
                var note = Label(server.Settings.Name + " must be stopped. Recovery returns this map to its state before the interrupted restore. Settings and sibling maps are preserved. The map stays stopped.", 16);
                note.TextWrapping = TextWrapping.Wrap; panel.Children.Add(note);
                panel.Children.Add(ActionButton("Recover", () => dialog.Close(true), true));
                panel.Children.Add(ActionButton("Cancel", () => dialog.Close(false))); dialog.Content = new ScrollViewer { Content = panel };
                if (!await dialog.ShowDialog<bool>(this)) return;
            }
            BackgroundOperation job;
            if (view.OperationId is Guid existing)
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(existing), TimeSpan.FromSeconds(8), lifetime.Token);
            else
            {
                view.OperationId = Guid.NewGuid();
                job = await client.CallAsync<BackgroundOperation>("world-restore-rollback-start", new WorldRollbackRequest(server.Id, record.OperationId, true),
                    TimeSpan.FromSeconds(8), lifetime.Token, view.OperationId);
            }
            while (true)
            {
                if (job.Id != view.OperationId || job.Kind != "world-restore-rollback" || !Enum.IsDefined(job.State)) throw new InvalidDataException();
                if (job.State is not (BackgroundOperationState.Queued or BackgroundOperationState.Running)) break;
                view.Notice = job.Progress?.Message ?? "Recovering previous world…"; Render(); await Task.Delay(750, lifetime.Token);
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(view.OperationId!.Value), TimeSpan.FromSeconds(8), lifetime.Token);
            }
            if (job.State == BackgroundOperationState.Succeeded)
            {
                var result = job.Result?.Deserialize<WorldRollbackResult>();
                if (result is null || result.ServerId != server.Id || result.RestoreOperationId != record.OperationId
                    || result.Phase != WorldRestorePhase.RolledBack) throw new InvalidDataException();
                view.Notice = "Previous world recovered; map remains stopped.";
            }
            else view.Notice = "Recovery did not complete. Keep the map stopped and review its restore records.";
            view.OperationId = null; await LoadRestoreRecoveryAsync(server);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or InvalidOperationException or JsonException or OperationCanceledException)
        { view.Notice = "Recovery outcome is unknown. Check the existing operation; nothing will be resent."; }
        finally { view.Busy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }
}
