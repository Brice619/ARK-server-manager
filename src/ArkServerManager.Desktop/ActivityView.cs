using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private bool activityPage;
    private bool activityBusy;
    private bool activityStale;
    private ActivityHistory? history;
    private string activityMessage = "Activity has not been loaded.";
    private int activityFilter;

    private async Task LoadActivityAsync()
    {
        if (client is null || activityBusy || lifetime.IsCancellationRequested) return;
        activityBusy = true;
        refresh.IsEnabled = false;
        try
        {
            await LoadRecoveryAsync();
            await LoadInstallationInventoryAsync();
            var latest = await client.CallAsync<ActivityHistory>("activity", null, TimeSpan.FromSeconds(8), lifetime.Token);
            if (latest.Entries is null || latest.UnreadableEntries < 0) throw new InvalidDataException("Invalid activity history.");
            NotifyNewActivity(latest);
            history = latest; activityStale = false;
            if (activityPage) { newActivityCount = 0; UpdateNotificationBadge(); }
            activityMessage = latest.Entries.Count == 0 ? "No recorded activity yet." : "Recent recorded activity";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or System.Text.Json.JsonException)
        {
            activityStale = true;
            activityMessage = history is null ? "Activity could not be loaded. Refresh to try again."
                : "Showing previously loaded activity. Refresh to reconnect.";
        }
        finally
        {
            activityBusy = false;
            if (!lifetime.IsCancellationRequested) { refresh.IsEnabled = client is not null; Render(); }
        }
    }

    private void AddActivityPreview()
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 12, 0, 0) };
        header.Children.Add(Label("Recent Activity", 24, true));
        var view = new Button { Content = "View activity" };
        view.Click += async (_, _) =>
        {
            activityPage = true; installationsPage = false; Render();
            await LoadActivityAsync();
        };
        Grid.SetColumn(view, 1); header.Children.Add(view); content.Children.Add(header);
        if (history is null || activityStale || history.Entries.Count == 0) content.Children.Add(Label(activityMessage, 14));
        foreach (var entry in history?.Entries.OrderByDescending(e => e.Time).Take(3) ?? [])
            content.Children.Add(ActivityRow(entry, compact: true));
    }

    private void RenderActivity()
    {
        var back = new Button { Content = "← Back to dashboard" };
        back.Click += (_, _) => { activityPage = false; selectedCluster = null; selectedServer = null; Render(); };
        content.Children.Add(back);
        content.Children.Add(Label("Activity", 32, true));
        AddRecoveryNotice(details: true);
        AddRecoveryJobs();
        AddInstallationInventory();
        content.Children.Add(Label(activityMessage, 16));
        var filter = new ComboBox { ItemsSource = new[] { "All activity", "Warnings and errors", "Errors" }, SelectedIndex = activityFilter, MinWidth = 240 };
        filter.SelectionChanged += (_, _) => { if (filter.SelectedIndex != activityFilter) { activityFilter = filter.SelectedIndex; Render(); } };
        content.Children.Add(filter);
        if (history is null) return;
        if (history.OlderEntriesOmitted) content.Children.Add(Label("Showing the most recent history. Older records remain in the activity log.", 14));
        if (history.UnreadableEntries > 0) content.Children.Add(Label($"{history.UnreadableEntries} log records could not be read. The readable records appear below.", 14));
        var entries = history.Entries.Where(e => activityFilter switch
        {
            1 => e.Severity is ActivitySeverity.Warning or ActivitySeverity.Error,
            2 => e.Severity == ActivitySeverity.Error,
            _ => true
        }).OrderByDescending(e => e.Time).ToArray();
        if (entries.Length == 0 && history.Entries.Count > 0) content.Children.Add(Label("No activity matches this filter.", 16));
        foreach (var entry in entries) content.Children.Add(ActivityRow(entry, compact: false));
    }

    private Control ActivityRow(ActivityEntry entry, bool compact)
    {
        var body = new StackPanel { Spacing = 8 };
        var color = entry.Severity switch { ActivitySeverity.Error => "#FF7777", ActivitySeverity.Warning => "#FFC04D", _ => "#B8C6D4" };
        var heading = Label($"{entry.Time.ToLocalTime():g} · {entry.Severity} · {entry.Category}", 14);
        heading.Foreground = Brush.Parse(color); body.Children.Add(heading);
        var message = compact && entry.Message.Length > 180 ? entry.Message[..180] + "…" : entry.Message;
        body.Children.Add(Label(message, 16));
        if (entry.Stage == "automatic-server-update" && entry.ServerId is Guid updateServerId
            && catalog?.Servers.FirstOrDefault(s => s.Id == updateServerId) is { } updateServer)
        {
            var review = new Button { Content = "Review server update", IsEnabled = client is not null && !updateCheckBusy };
            review.Click += (_, _) =>
            {
                activityPage = false; installationsPage = false; settingsPage = false; creationPage = false;
                selectedCluster = null; selectedServer = updateServer.Id; managementTab = 0;
                BeginServerUpdateCheck(updateServer);
            };
            body.Children.Add(review);
        }
        if (!compact)
        {
            var details = new StackPanel { Spacing = 6 };
            if (entry.ServerId is Guid serverId)
                details.Children.Add(Label("Server: " + (catalog?.Servers.FirstOrDefault(s => s.Id == serverId)?.Settings.Name ?? "Previously managed server"), 14));
            if (entry.Stage is not null) details.Children.Add(Label("Stage: " + entry.Stage, 14));
            if (entry.ExitCode is int exitCode) details.Children.Add(Label("Exit code: " + exitCode, 14));
            if (entry.OperationId is Guid operation) details.Children.Add(Label("Operation: " + operation, 14));
            if (details.Children.Count > 0) body.Children.Add(new Expander { Header = "Details", Content = details });
        }
        return new Border { Background = Brush.Parse("#1B2732"), Padding = new Thickness(18), CornerRadius = new CornerRadius(8), Child = body };
    }
}
