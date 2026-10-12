using Avalonia.Controls;
using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Desktop;
public sealed partial class MainWindow
{
    private sealed class ClusterDefaultEditor
    {
        public ClusterDefaultsReview? Review;
        public string Name = "", Capacity = "70", Notice = "Load cluster defaults to edit them.";
        public bool Busy, Attempted;
        public ClusterDefaultsEdit? Pending;
        public Guid RequestId;
    }
    private readonly Dictionary<Guid, ClusterDefaultEditor> clusterDefaultEditors = [];
    private void RenderClusterDefaults(ClusterDefinition cluster)
    {
        if (!clusterDefaultEditors.TryGetValue(cluster.Id, out var view)) clusterDefaultEditors[cluster.Id] = view = new();
        var body = new StackPanel { Spacing = 16 };
        body.Children.Add(Label("Defaults for new maps", 23, true));
        body.Children.Add(Label("New maps inherit the default player capacity. Existing maps keep their current settings.", 16));
        body.Children.Add(Label(view.Notice, 15));
        body.Children.Add(ActionButton("Add a new map…", client is null || clusterCreationBusy ? null : () => _ = CreateClusterMapAsync(cluster)));
        body.Children.Add(ActionButton("Edit shared settings…", client is null || sharedSettingsBusy || clusterPresetBusy || pendingClusterPreset is not null ? null : () => _ = EditSharedClusterSettingsAsync(cluster)));
        body.Children.Add(ActionButton(pendingClusterPreset is null ? "Apply preset to cluster maps…" : "Check cluster preset result",
            client is null || clusterPresetBusy ? null : () => _ = ApplyClusterPresetAsync(cluster)));
        if (clusterPresetNotice.Length > 0) body.Children.Add(Label(clusterPresetNotice, 15));
        if (view.Review is not null)
        {
            var name = new TextBox { Text = view.Name, MaxLength = 256, IsEnabled = !view.Busy && view.Pending is null };
            name.TextChanged += (_, _) => view.Name = name.Text ?? "";
            body.Children.Add(DraftField("Cluster name", name, "Shown in the dashboard."));
            var capacity = new TextBox { Text = view.Capacity, MaxLength = 9, IsEnabled = !view.Busy && view.Pending is null };
            capacity.TextChanged += (_, _) => view.Capacity = capacity.Text ?? "";
            body.Children.Add(DraftField("Default maximum players", capacity, "Each newly added map can override this."));
            creationFooter.IsVisible = true;
            creationFooter.Children.Add(ActionButton(view.Pending is null ? "Save changes…" : "Check existing save", client is null || view.Busy ? null : () => _ = SaveClusterDefaultsAsync(cluster, view), true));
        }
        if (view.Pending is null) body.Children.Add(ActionButton("Load current defaults", client is null || view.Busy ? null : () => _ = LoadClusterDefaultsAsync(cluster, view)));
        else if (!view.Busy) body.Children.Add(ActionButton("Dismiss unknown result", () =>
        { view.Pending = null; view.Review = null; view.Notice = "The save may still complete. Reload defaults before another change."; Render(); }));
        content.Children.Add(Surface(body));
    }
    private async Task LoadClusterDefaultsAsync(ClusterDefinition cluster, ClusterDefaultEditor view)
    {
        if (client is null || view.Busy || view.Pending is not null) return;
        view.Busy = true; Render();
        try
        {
            var review = await client.CallAsync<ClusterDefaultsReview>("cluster-defaults-read", cluster.Id, TimeSpan.FromSeconds(8), lifetime.Token);
            if (review.ClusterId != cluster.Id || review.Edition != cluster.Edition) throw new InvalidDataException();
            view.Review = review; view.Name = review.Name; view.Capacity = review.MaxPlayers.ToString(); view.Notice = "Review and confirm before saving.";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsBasicSaveError(error)) { view.Notice = "Defaults could not be loaded. Try again."; }
        finally { view.Busy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }
    private async Task SaveClusterDefaultsAsync(ClusterDefinition cluster, ClusterDefaultEditor view)
    {
        if (client is null || view.Busy || view.Review is null) return;
        view.Busy = true;
        try
        {
            if (view.Pending is null)
            {
                if (!int.TryParse(view.Capacity, out var capacity) || capacity < 1 || string.IsNullOrWhiteSpace(view.Name))
                { view.Notice = "Enter a cluster name and positive player capacity."; return; }
                var dialog = new Window { Title = "Save cluster defaults?", Width = 520, Height = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                var panel = new StackPanel { Spacing = 16, Margin = new Avalonia.Thickness(24) };
                panel.Children.Add(Label("Save the cluster name and defaults for new maps? Existing map settings stay as they are.", 18));
                panel.Children.Add(ActionButton("Save changes", () => dialog.Close(true), true)); panel.Children.Add(ActionButton("Cancel", () => dialog.Close(false)));
                dialog.Content = panel; if (!await dialog.ShowDialog<bool>(this)) return;
                view.Pending = new(view.Review, view.Name, capacity, true); view.RequestId = Guid.NewGuid(); view.Attempted = false;
            }
            Render();
            BackgroundOperation job;
            if (!view.Attempted)
            {
                view.Attempted = true;
                job = await client.CallAsync<BackgroundOperation>("cluster-defaults-save-start", view.Pending, TimeSpan.FromSeconds(8), lifetime.Token, view.RequestId);
            }
            else job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(view.RequestId), TimeSpan.FromSeconds(8), lifetime.Token);
            while (true)
            {
                if (job.Id != view.RequestId || job.Kind != "cluster-defaults-save") throw new InvalidDataException();
                if (job.State == BackgroundOperationState.Succeeded)
                {
                    var result = job.Result?.Deserialize<ClusterDefaultsReview>();
                    if (result is null || result.ClusterId != cluster.Id) throw new InvalidDataException();
                    view.Pending = null; view.Review = result; view.Name = result.Name; view.Capacity = result.MaxPlayers.ToString(); view.Notice = "Cluster defaults saved.";
                    await RefreshAsync(); break;
                }
                if (job.State is BackgroundOperationState.Failed or BackgroundOperationState.Canceled)
                { view.Pending = null; view.Review = null; view.Notice = "Save failed. Reload current defaults before editing."; break; }
                await Task.Delay(500, lifetime.Token);
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(view.RequestId), TimeSpan.FromSeconds(8), lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsBasicSaveError(error)) { view.Notice = "Save outcome unknown. Check the existing request before trying again."; }
        finally { view.Busy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }
}
