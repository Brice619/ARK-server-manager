using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private bool automaticStartupRequested;
    private Guid? automaticStartupRequest;

    private async Task StartSelectedServersOnceAsync()
    {
        if (designPreview || client is null || automaticStartupRequested) return;
        automaticStartupRequested = true;
        automaticStartupRequest = Guid.NewGuid();
        try
        {
            var job = await client.CallAsync<BackgroundOperation>("server-startup-run", null,
                TimeSpan.FromSeconds(8), lifetime.Token, automaticStartupRequest);
            if (job.Id != automaticStartupRequest || job.Kind != "server-automatic-startup" || !Enum.IsDefined(job.State))
                throw new InvalidDataException();
            // The owned background job continues if this window closes. Refresh never resubmits it.
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or JsonException)
        { startupNotice = "Automatic startup could not be confirmed. Check server status and activity before starting a server manually."; }
    }

    private async Task ChooseStartupServersAsync()
    {
        if (designPreview || client is null) return;
        try
        {
            var preferences = await client.CallAsync<ServerStartupPreferences>("server-startup-read", null, TimeSpan.FromSeconds(8), lifetime.Token);
            preferences.Validate();
            var current = await client.CallAsync<CatalogDocument>("catalog", null, TimeSpan.FromSeconds(8), lifetime.Token);
            var (dialog, servers, clusters) = BuildStartupSelection(current, preferences);
            if (!await dialog.ShowDialog<bool>(this)) return;
            var selection = new ServerStartupPreferences(1, servers.Where(p => p.Value.IsChecked == true).Select(p => p.Key).ToList(),
                clusters.Where(p => p.Value.IsChecked == true).Select(p => p.Key).ToList());
            var confirm = new Window { Title = "Save automatic startup?", Width = 560, Height = 320, CanResize = false,
                Background = Brush.Parse("#0F1921"), FontFamily = FontFamily, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var body = new StackPanel { Spacing = 20, Margin = new Thickness(28) };
            body.Children.Add(Label("Save automatic startup?", 25, true));
            var summary = Label(selection.Servers.Count + " standalone server(s) and " + selection.Clusters.Count + " cluster(s) will start the next time the app opens. This does not start them now.", 17);
            summary.TextWrapping = TextWrapping.Wrap; body.Children.Add(summary);
            body.Children.Add(ActionButton("Save selection", () => confirm.Close(true), true));
            body.Children.Add(ActionButton("Cancel", () => confirm.Close(false))); confirm.Content = body;
            if (!await confirm.ShowDialog<bool>(this)) return;
            var saved = await client.CallAsync<ServerStartupPreferences>("server-startup-save", new SaveServerStartupPreferences(selection, true), TimeSpan.FromSeconds(8), lifetime.Token);
            saved.Validate(); startupPreferenceNotice = "Automatic server startup selection saved for the next app launch.";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or ArgumentException or JsonException)
        { startupPreferenceNotice = "Automatic startup preferences could not be confirmed. Reopen the selection to check what is saved."; }
        if (!lifetime.IsCancellationRequested) Render();
    }

    private (Window Dialog, Dictionary<Guid, CheckBox> Servers, Dictionary<Guid, CheckBox> Clusters) BuildStartupSelection(CatalogDocument current, ServerStartupPreferences preferences)
    {
            var dialog = new Window { Title = "Automatic server startup", Width = 650, Height = 610, MinWidth = 540, MinHeight = 420,
                Background = Brush.Parse("#0F1921"), FontFamily = FontFamily, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(26) };
            var header = new StackPanel { Spacing = 12, Margin = new Thickness(0, 0, 0, 20) };
            header.Children.Add(Label("Start when the app opens", 26, true));
            var explanation = Label("Select standalone servers or whole clusters. New maps in a selected cluster follow its startup choice. Already running servers keep running.", 16);
            explanation.TextWrapping = TextWrapping.Wrap; header.Children.Add(explanation); layout.Children.Add(header);
            var choices = new StackPanel { Spacing = 14 };
            var servers = new Dictionary<Guid, CheckBox>(); var clusters = new Dictionary<Guid, CheckBox>();
            choices.Children.Add(Label("Standalone servers", 20, true));
            foreach (var server in current.Servers.Where(s => s.ClusterId is null))
            {
                var check = new CheckBox { Content = server.Settings.Name + " · " + server.Edition, IsChecked = preferences.Servers.Contains(server.Id) };
                servers.Add(server.Id, check); choices.Children.Add(check);
            }
            if (servers.Count == 0) choices.Children.Add(Label("No standalone servers yet.", 16));
            choices.Children.Add(Label("Clusters", 20, true));
            foreach (var cluster in current.Clusters)
            {
                var check = new CheckBox { Content = cluster.Name + " · " + cluster.Edition, IsChecked = preferences.Clusters.Contains(cluster.Id) };
                clusters.Add(cluster.Id, check); choices.Children.Add(check);
            }
            if (clusters.Count == 0) choices.Children.Add(Label("No clusters yet.", 16));
            var scroll = new ScrollViewer { Content = choices, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
            actions.Children.Add(ActionButton("Cancel", () => dialog.Close(false)));
            actions.Children.Add(ActionButton("Review selection", () => dialog.Close(true), true));
            Grid.SetRow(actions, 2); layout.Children.Add(actions); dialog.Content = layout;
        return (dialog, servers, clusters);
    }

    public static Window CreateServerStartupDesignReview()
    {
        var owner = new MainWindow(["--design-preview"]);
        return owner.BuildStartupSelection(owner.catalog!, new(1, [], [owner.catalog!.Clusters[0].Id])).Dialog;
    }
}
