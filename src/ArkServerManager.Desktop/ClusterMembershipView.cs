using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Desktop;
public sealed partial class MainWindow
{
    private bool membershipEditorOpen;
    private ClusterMembershipEdit? pendingMembership;
    private Guid pendingMembershipId;
    private async Task EditClusterMembershipAsync(ServerDefinition server)
    {
        if (client is null || catalog is null || membershipEditorOpen) return;
        if (server.ClusterId is not null && pendingMembership is null) return;
        membershipEditorOpen = true;
        try
        {
            if (pendingMembership is not null)
            {
                await TrackMembershipChangeAsync(pendingMembership, pendingMembershipId, reconnect: true);
                return;
            }
            var reviews = await client.CallAsync<List<ClusterMemberReview>>("cluster-membership-read", null, TimeSpan.FromSeconds(8), lifetime.Token);
            var review = reviews.Single(member => member.ServerId == server.Id);
            if (review.Edition != server.Edition || review.Directory != server.Directory) throw new InvalidDataException("Server changed.");
            if (review.ClusterId is not null) throw new InvalidOperationException("The server already belongs to a cluster.");
            var targets = catalog.Clusters.Where(cluster => cluster.Edition == server.Edition).OrderBy(cluster => cluster.Name).ToArray();
            var dialog = new Window { Title = "Edit cluster membership", Width = 640, Height = 460,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush.Parse("#0F1921"), FontFamily = FontFamily };
            var body = new StackPanel { Margin = new Thickness(28), Spacing = 18 };
            body.Children.Add(Label(server.Settings.Name, 26, true));
            body.Children.Add(Label("Add this server to a cluster and inherit its settings and mods. Its name, ports and passwords stay with it. You can edit map overrides afterward.", 16));
            body.Children.Add(Label("Stop the affected clusters and this server before saving.", 15));
            var picker = new ComboBox { ItemsSource = targets.Select(cluster => cluster.Name).Append("Create a new cluster…").ToArray(),
                SelectedIndex = targets.Length,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
            body.Children.Add(picker);
            var newCluster = new StackPanel { Spacing = 12, IsVisible = false };
            var name = new TextBox { PlaceholderText = "Cluster name", MaxLength = 256 };
            newCluster.Children.Add(name);
            var additional = new List<(ClusterMemberReview Review, CheckBox Choice)>();
            foreach (var candidate in reviews.Where(member => member.ServerId != server.Id && member.Edition == server.Edition && member.ClusterId is null))
            {
                var choice = new CheckBox { Content = catalog.Servers.FirstOrDefault(item => item.Id == candidate.ServerId)?.Settings.Name ?? candidate.ServerId.ToString() };
                additional.Add((candidate, choice)); newCluster.Children.Add(choice);
            }
            body.Children.Add(newCluster);
            newCluster.IsVisible = true;
            picker.SelectionChanged += (_, _) => newCluster.IsVisible = picker.SelectedIndex == targets.Length;
            body.Children.Add(ActionButton("Review change…", () => dialog.Close(true), true));
            body.Children.Add(ActionButton("Cancel", () => dialog.Close(false)));
            dialog.Content = new ScrollViewer { Content = body };
            if (!await dialog.ShowDialog<bool>(this) || picker.SelectedIndex < 0) return;
            var creating = picker.SelectedIndex == targets.Length;
            if (creating && string.IsNullOrWhiteSpace(name.Text)) { lifecycleNotice = "Enter a cluster name before reviewing the change."; return; }
            Guid? target = creating ? Guid.NewGuid() : targets[picker.SelectedIndex].Id;
            if (target == review.ClusterId) return;
            var selection = new List<ClusterMemberReview> { review };
            if (creating) selection.AddRange(additional.Where(item => item.Choice.IsChecked == true).Select(item => item.Review));
            var confirmation = BuildJoiningConfirmation(selection.Count, creating ? name.Text!.Trim() : targets.Single(cluster => cluster.Id == target).Name, creating);
            if (!await confirmation.ShowDialog<bool>(this)) return;
            pendingMembership = new(selection, target, true) { NewClusterName = creating ? name.Text!.Trim() : null }; pendingMembershipId = Guid.NewGuid();
            await TrackMembershipChangeAsync(pendingMembership, pendingMembershipId);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsBasicSaveError(error) || error is ArgumentException)
        { lifecycleNotice = pendingMembership is null ? "Cluster membership could not be reviewed: " + error.Message
            : "Cluster change outcome is unknown. Use Check cluster change to check the original request."; }
        finally { membershipEditorOpen = false; if (!lifetime.IsCancellationRequested) Render(); }
    }

    private async Task TrackMembershipChangeAsync(ClusterMembershipEdit edit, Guid id, bool reconnect = false)
    {
        if (client is null) return;
        BackgroundOperation? job;
        try
        { job = reconnect
            ? (await client.CallAsync<OperationLookup>("operation-status-lookup", new OperationStatusRequest(id), TimeSpan.FromSeconds(8), lifetime.Token)).Operation
            : await client.CallAsync<BackgroundOperation>("cluster-membership-save-start", edit, TimeSpan.FromSeconds(8), lifetime.Token, id); }
        catch (Exception error) when (IsBasicSaveError(error))
        {
            // A lost response must query the same request; never submit a replacement change.
            job = (await client.CallAsync<OperationLookup>("operation-status-lookup", new OperationStatusRequest(id), TimeSpan.FromSeconds(8), lifetime.Token)).Operation;
        }
        while (true)
        {
            if (job is null) { await ReconcileMembershipAsync(edit); return; }
            if (job.Id != id || job.Kind != "cluster-membership-save" || !Enum.IsDefined(job.State)) throw new InvalidDataException();
            if (job.State == BackgroundOperationState.Succeeded)
            {
                var result = job.Result?.Deserialize<List<ClusterMemberReview>>();
                if (result is null || result.Count != edit.Expected.Count || result.Any(member => member.ClusterId != edit.TargetClusterId
                    || !edit.Expected.Any(expected => expected.ServerId == member.ServerId && expected.Directory == member.Directory && expected.Edition == member.Edition)))
                    throw new InvalidDataException();
                pendingMembership = null;
                lifecycleNotice = "Cluster joining and inherited settings saved."; await RefreshAsync(); return;
            }
            if (job.State is BackgroundOperationState.Failed or BackgroundOperationState.Canceled)
            { pendingMembership = null; lifecycleNotice = job.Error ?? "Cluster joining needs review. Check its recorded recovery before trying again."; await RefreshAsync(); return; }
            lifecycleNotice = job.Progress?.Message ?? "Saving cluster membership…"; Render();
            await Task.Delay(500, lifetime.Token);
            job = (await client.CallAsync<OperationLookup>("operation-status-lookup", new OperationStatusRequest(id), TimeSpan.FromSeconds(8), lifetime.Token)).Operation;
        }
    }

    private async Task ReconcileMembershipAsync(ClusterMembershipEdit edit)
    {
        if (client is null) return;
        var current = await client.CallAsync<List<ClusterMemberReview>>("cluster-membership-read", null, TimeSpan.FromSeconds(8), lifetime.Token);
        var state = ClusterMembershipReconciliation.Evaluate(edit, current);
        await ReadJoiningRecoveryAsync();
        pendingMembership = null;
        if (joiningRecovery.Any(r => r.ClusterId == edit.TargetClusterId && r.ServerIds.Any(id => edit.Expected.Any(member => member.ServerId == id))))
        {
            lifecycleNotice = "Cluster joining is still recorded. Use Finish cluster joining to complete inherited settings; the original request was not repeated.";
            await RefreshAsync(); return;
        }
        lifecycleNotice = state switch
        {
            ClusterMembershipCurrentState.RequestedMembershipPresent => "The requested cluster membership is present. The original operation result is unavailable.",
            ClusterMembershipCurrentState.OriginalMembershipPresent => "The original cluster membership is still present. The change was not repeated; review it before saving again.",
            _ => "Cluster membership has changed. The original operation result is unavailable; review the current membership before making another change."
        };
        await RefreshAsync();
    }

    private Window BuildJoiningConfirmation(int count, string targetName, bool creating)
    {
        var confirmation = new Window { Title = "Save cluster membership?", Width = 540, Height = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush.Parse("#0F1921"), FontFamily = FontFamily };
        var body = new StackPanel { Margin = new Thickness(28), Spacing = 18 };
        body.Children.Add(Label("Add " + count + " server(s) to " + targetName + "?", 24, true));
        body.Children.Add(Label("Existing shared transfer data will remain in its current cluster.", 16));
        body.Children.Add(Label(creating ? "The first selected server supplies the new cluster defaults. Other selected servers inherit them."
            : "Joining servers inherit the cluster settings and mod list. Their names, ports and passwords are preserved.", 16));
        body.Children.Add(ActionButton("Save change", () => confirmation.Close(true), true));
        body.Children.Add(ActionButton("Cancel", () => confirmation.Close(false)));
        confirmation.Content = new ScrollViewer { Content = body,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        return confirmation;
    }

    public static Window CreateJoiningConfirmationDesignReview()
        => new MainWindow(["--design-preview"]).BuildJoiningConfirmation(2, "Main PvE Cluster", false);
}
