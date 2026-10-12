using Avalonia.Controls;
using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private bool reviewLiveModOrder;
    private sealed class ModOrderJob(ModOrderEdit selection)
    {
        public readonly Guid Id = Guid.NewGuid();
        public readonly ModOrderEdit Selection = selection;
        public bool Busy, Accepted, Finished;
        public string Message = "";
    }
    private readonly Dictionary<Guid, ModOrderJob> modOrderJobs = [];
    private readonly Dictionary<Guid, List<string>> queuedModOrders = [];
    private readonly Dictionary<Guid, string> queuedModOrderNotices = [];
    private readonly HashSet<Guid> queuedModOrderLoading = [];
    private readonly HashSet<Guid> queuedModReplacements = [];

    private async Task LoadQueuedModOrderAsync(Guid id)
    {
        if (!queuedModOrderLoading.Add(id)) return;
        try
        {
            var result = await client!.CallAsync<ModOrderPending>("server-mod-order-read", id, TimeSpan.FromSeconds(8), lifetime.Token);
            if (result.ServerId != id || result.Order is not null && (result.Order.Any(string.IsNullOrWhiteSpace) || result.Order.Distinct().Count() != result.Order.Count))
                throw new InvalidDataException("Unexpected pending order.");
            if (result.Order is null) queuedModOrders.Remove(id); else queuedModOrders[id] = result.Order;
            if (result.Replace) { queuedModReplacements.Add(id); replacingModSelections.Add(id); }
            else { queuedModReplacements.Remove(id); if (!modOrderDrafts.ContainsKey(id)) replacingModSelections.Remove(id); }
            queuedModOrderNotices.Remove(id);
        }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or JsonException or OperationCanceledException)
        { queuedModOrderNotices[id] = "Could not check saved order. Reopen Mods to refresh before editing."; }
        finally { queuedModOrderLoading.Remove(id); if (!lifetime.IsCancellationRequested && modPageServer == id && managementTab == 2) Render(); }
    }

    private void RenderLiveModOrderConfirmation(ServerDefinition server)
    {
        content.Children.Add(Label(replacingModSelections.Contains(server.Id) ? "Save mod changes?" : "Save mod load order?", 30, true));
        content.Children.Add(Label(server.Settings.Name + " · " + server.Edition, 17));
        modOrderJobs.TryGetValue(server.Id, out var job);
        var order = job?.Selection.Order ?? modOrderDrafts.GetValueOrDefault(server.Id, server.Mods);
        var review = new StackPanel { Spacing = 12 };
        for (var i = 0; i < order.Count; i++) review.Children.Add(Label($"{i + 1}.  {order[i]}", 17));
        content.Children.Add(Surface(review));
        content.Children.Add(Label("Changes apply to this map. A stopped server is updated immediately.", 16));
        if (replacingModSelections.Contains(server.Id))
        {
            var removed = server.Mods.Except(order).ToArray();
            if (removed.Length > 0) content.Children.Add(Label("Removed mod files will be deleted when applied: " + string.Join(", ", removed), 16));
        }
        if (job?.Message.Length > 0) content.Children.Add(Label(job.Message, 16));
        var actions = new WrapPanel();
        if (job is { Finished: false })
            actions.Children.Add(ActionButton(job.Busy ? "Saving…" : "Check save result", job.Busy ? null : () => { _ = TrackModOrderAsync(server.Id, job); }, true));
        else
        {
            actions.Children.Add(ActionButton("Cancel", () => { modOrderJobs.Remove(server.Id); modOrderConfirmation = false; Render(); }));
            void Save(SaveChoice choice)
            {
                var selected = server with { Settings = server.Settings.Copy(), Mods = [.. server.Mods] };
                var created = new ModOrderJob(new(selected, [.. order], choice, true, replacingModSelections.Contains(server.Id))); modOrderJobs[server.Id] = created;
                _ = TrackModOrderAsync(server.Id, created);
            }
            actions.Children.Add(ActionButton("Save for next start", client is null ? null : () => Save(SaveChoice.SaveForNextStart)));
            actions.Children.Add(ActionButton("Save and restart this map", client is null ? null : () => Save(SaveChoice.SaveAndRestart), true));
        }
        content.Children.Add(actions);
    }

    private async Task TrackModOrderAsync(Guid serverId, ModOrderJob view)
    {
        if (client is null || view.Busy || view.Finished || lifetime.IsCancellationRequested) return;
        view.Busy = true; view.Message = "Connecting to the manager…"; Render();
        try
        {
            var job = await client.CallAsync<BackgroundOperation>(view.Accepted ? "operation-status" : "server-mod-order-save-start",
                view.Accepted ? new OperationStatusRequest(view.Id) : (object)view.Selection,
                TimeSpan.FromSeconds(8), lifetime.Token, view.Accepted ? null : view.Id);
            while (true)
            {
                if (job.Id != view.Id || job.Kind != "server-mod-order-save" || !Enum.IsDefined(job.State))
                    throw new InvalidDataException("Unexpected mod order operation.");
                view.Accepted = true; view.Message = job.Progress?.Message ?? "Save is queued.";
                if (job.State == BackgroundOperationState.Succeeded)
                {
                    var result = job.Result?.Deserialize<ModOrderEditResult>();
                    if (result is null || result.ServerId != serverId || result.Save is null) throw new InvalidDataException("Unexpected mod order result.");
                    view.Finished = true; view.Message = result.Save.Message;
                    if (result.Save.Saved)
                    {
                        if (result.Save.Pending) queuedModOrders[serverId] = [.. view.Selection.Order]; else queuedModOrders.Remove(serverId);
                        if (result.Save.Pending && view.Selection.Replace) queuedModReplacements.Add(serverId); else queuedModReplacements.Remove(serverId);
                        if (!result.Save.Pending) replacingModSelections.Remove(serverId);
                        modOrderDrafts.Remove(serverId); modOrderJobs.Remove(serverId);
                        if (modPageServer == serverId) modOrderConfirmation = false;
                        await RefreshAsync();
                    }
                    break;
                }
                if (job.State is BackgroundOperationState.Failed or BackgroundOperationState.Canceled)
                { view.Finished = true; view.Message = job.Error ?? "The save stopped. Refresh before retrying."; break; }
                Render(); await Task.Delay(1000, lifetime.Token);
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(view.Id), TimeSpan.FromSeconds(8), lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or JsonException or ArgumentException or OperationCanceledException)
        { view.Message = "Connection lost. Check the same save result before submitting another change."; }
        finally { view.Busy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }
}
