using Avalonia.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly LocalManagerClient? client;
    private readonly string? managerRoot;
    private readonly StackPanel content = new() { Margin = new Thickness(38, 38, 20, 28), Spacing = 10 };
    private readonly StackPanel creationFooter = new() { Margin = new Thickness(38, 12, 38, 18), Spacing = 8, IsVisible = false };
    private readonly TextBlock connection = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button refresh = new() { Content = "Refresh", MinWidth = 100 };
    private ScrollViewer? pageScroll;
    private CatalogDocument? catalog;
    private RegisteredProcessStatus? processStatus;
    private Guid? selectedCluster;
    private Guid? selectedServer;
    private bool settingsPage;
    private bool creationPage;
    private readonly bool designPreview;
    private string? startupNotice;
    private readonly string? startupUpdateJournal;
    private bool startupUpdateConfirmed;

    public MainWindow(string[]? args = null, string? startupNotice = null, string? startupUpdateJournal = null)
    {
        this.startupNotice = startupNotice;
        this.startupUpdateJournal = startupUpdateJournal;
        Title = "ARK Server Manager";
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        using (var icon = AssetLoader.Open(new Uri("avares://ArkServerManager.Desktop/Assets/ark-logo.png"))) Icon = new WindowIcon(icon);
        if (OperatingSystem.IsWindows())
        {
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaTitleBarHeightHint = 64;
            WindowDecorations = WindowDecorations.BorderOnly;
        }
        Width = 1536; Height = 1024; MinWidth = 900; MinHeight = 640;
        if (Screens.Primary is { } screen)
        {
            Width = Math.Min(Width, screen.WorkingArea.Width / screen.Scaling);
            Height = Math.Min(Height, screen.WorkingArea.Height / screen.Scaling - 32);
        }
        FontFamily = new FontFamily("Segoe UI, Inter, sans-serif");
        Background = new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.4, RelativeUnit.Relative),
            GradientStops = [new GradientStop(Color.Parse("#101F2C"), 0), new GradientStop(Color.Parse("#0B1620"), 1)]
        };
        designPreview = DesignReviewSupport.Enabled && args is ["--design-preview"];
#if DESIGN_REVIEW
        if (designPreview) catalog = PreviewCatalog();
#endif
        if (args is ["--data-root", var root])
        {
            try { managerRoot = Path.GetFullPath(root); client = new LocalManagerClient(managerRoot); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            { connection.Text = "The configured server folder is invalid. Check your installation settings."; }
        }
        else if (!designPreview)
        {
            try
            {
                var installed = InstalledAppSettingsFile.Read(AppContext.BaseDirectory);
                if (installed is not null)
                { managerRoot = installed.ServerRoot; client = new LocalManagerClient(managerRoot); }
                else connection.Text = "Choose your server folder during installation to get started.";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
                or NotSupportedException or System.Text.Json.JsonException)
            { connection.Text = "Your installed server folder could not be loaded. Check your installation settings."; }
        }
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        layout.Margin = OffScreenMargin;
        PropertyChanged += (_, change) => { if (change.Property == OffScreenMarginProperty) layout.Margin = OffScreenMargin; };
        layout.Children.Add(BuildHeader());
        refresh.IsEnabled = client is not null;
        refresh.Click += async (_, _) => { if (installationsPage) await SearchInstallationsAsync(); else if (activityPage) await LoadActivityAsync(); else await RefreshAsync(); };
        connection.Margin = new Thickness(32, 12, 32, 0);
        connection.Foreground = Brush.Parse("#B8C6D4");
        Grid.SetRow(connection, 1); layout.Children.Add(connection);
        var scroll = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        pageScroll = scroll;
        ConfigureScrollViewer(scroll);
        Grid.SetRow(scroll, 2); layout.Children.Add(scroll);
        Grid.SetRow(creationFooter, 3); layout.Children.Add(creationFooter);
        Content = layout;
        Opened += async (_, _) => { Render(); await StartNotificationsAsync(); if (client is not null) await RefreshAsync(); };
        Closed += (_, _) => { notificationTimer?.Stop(); lifetime.Cancel(); desktopNotifications?.Dispose(); modPreview?.Dispose(); modPreview = null; };
        Closing += ConfirmWindowClose;
        SizeChanged += (_, change) =>
        { if ((change.PreviousSize.Height < 760) != (change.NewSize.Height < 760)) Render(); };
    }

    private async Task RefreshAsync()
    {
        if (client is null || lifetime.IsCancellationRequested) return;
        refresh.IsEnabled = false;
        connection.Text = "Connecting to your background manager…";
        try
        {
            await ManagerStartup.EnsureReadyAsync(client, managerRoot!, lifetime.Token);
            var latest = await client.CallAsync<CatalogDocument>("catalog", null, TimeSpan.FromSeconds(8), lifetime.Token);
            if (latest.SchemaVersion != 1 || latest.Servers is null || latest.Clusters is null)
                throw new InvalidDataException("Unsupported catalog.");
            catalog = latest;
            overviewModes.Clear();
            await StartSelectedServersOnceAsync();
            if (startupUpdateJournal is not null && !startupUpdateConfirmed)
            {
                try
                {
                    var confirmation = await AppUpdateTransaction.ConfirmAsync(startupUpdateJournal, AppContext.BaseDirectory,
                        _ => Task.FromResult(IsVisible && catalog is not null && client is not null), lifetime.Token);
                    startupUpdateConfirmed = confirmation.Phase == AppUpdatePhase.Confirmed;
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException
                    or UnauthorizedAccessException or System.Text.Json.JsonException)
                { startupNotice = "The app opened, but its update confirmation needs attention. The recovery copy has been kept."; }
            }
            managedRuntimeStatus = [];
            try
            {
                var status = await client.CallAsync<List<MemberOperationResult>>("lifecycle-status", null, TimeSpan.FromSeconds(8), lifetime.Token);
                if (status is not null && status.All(s => s is not null && Enum.IsDefined(s.State))) managedRuntimeStatus = status;
            }
            catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or System.Text.Json.JsonException)
            { managedRuntimeStatus = []; }
            processStatus = null;
            try
            {
                processStatus = await client.CallAsync<RegisteredProcessStatus>("server-process-status", null,
                    TimeSpan.FromSeconds(8), lifetime.Token);
                if (processStatus.MeasuredAt == default || processStatus.Servers is null
                    || processStatus.Servers.Any(s => !Enum.IsDefined(s.State) || s.ProcessCount < 0)
                    || processStatus.Servers.Select(s => s.ServerId).Distinct().Count() != processStatus.Servers.Count)
                    processStatus = null;
            }
            catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or System.Text.Json.JsonException)
            { processStatus = null; }
            connection.Text = "";
            await ReadJoiningRecoveryAsync();
            await ReadImportRecoveryAsync();
            Render();
            await LoadActivityAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException or InvalidOperationException or System.Text.Json.JsonException)
        {
            processStatus = null;
            managedRuntimeStatus = [];
            connection.Text = catalog is null
                ? "The background manager could not start or connect. Check your installation and refresh."
                : "Connection lost. Showing the last loaded server list; refresh to reconnect.";
        }
        finally { if (!lifetime.IsCancellationRequested) refresh.IsEnabled = true; }
    }

    private void Render()
    {
        overviewStatusLabels.Clear();
        if (headerCreateButton is not null) headerCreateButton.IsVisible = selectedServer is null && selectedCluster is null;
        ApplyScreenFade();
        content.Children.Clear();
        if (startupNotice is not null)
        {
            var notice = new StackPanel { Spacing = 8 };
            notice.Children.Add(Label(startupNotice, 15));
            notice.Children.Add(ActionButton("Dismiss", () => { this.startupNotice = null; Render(); }));
            content.Children.Add(Surface(notice));
        }
        var dashboard = !installationsPage && !activityPage && !settingsPage && !creationPage && selectedServer is null && selectedCluster is null;
        content.Margin = dashboard ? new Thickness(44, 6, 44, 0) : selectedCluster is not null && selectedServer is null ? new Thickness(56, 53, 56, 28) : new Thickness(44, 24, 44, 28);
        if (pageScroll is not null) { pageScroll.VerticalScrollBarVisibility = dashboard ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto; if (dashboard) pageScroll.Offset = default; }
        Background = new RadialGradientBrush { Center = new RelativePoint(0.5, 0.4, RelativeUnit.Relative), GradientStops = [new(Color.Parse(selectedCluster is not null && selectedServer is null ? "#002C42" : "#101F2C"), 0), new(Color.Parse(selectedCluster is not null && selectedServer is null ? "#0B1824" : "#0B1620"), 1)] };
        content.Spacing = dashboard || selectedServer is not null || selectedCluster is not null ? 6 : 10;
        creationFooter.Children.Clear(); creationFooter.IsVisible = false;
        AddPendingDeletionNotice();
        connection.IsVisible = !string.IsNullOrWhiteSpace(connection.Text);
        if (installationsPage) { RenderInstallations(); return; }
        if (activityPage) { RenderActivity(); return; }
        if (settingsPage) { RenderAppSettings(); return; }
        if (creationPage) { RenderCreationChoice(); return; }
        if (catalog is null)
        {
            content.Children.Add(Label("Your ARK Servers", 32, true));
            content.Children.Add(Label("Your servers and clusters will appear when the background manager connects.", 16));
            return;
        }
        if (designPreview && selectedServer == ReviewServerId && reviewServerVisible) { RenderReviewServer(); return; }
        if (selectedServer is Guid serverId && catalog.Servers.FirstOrDefault(s => s.Id == serverId) is { } server)
        {
            RenderServerDetails(server);
            return;
        }
        if (selectedCluster is Guid clusterId && catalog.Clusters.FirstOrDefault(c => c.Id == clusterId) is { } cluster)
        {
            RenderClusterDetails(cluster);
            return;
        }
        selectedServer = null; selectedCluster = null;
        content.Children.Add(SectionHeading("Your ARK Servers", "", 0));

        AddRecoveryNotice(details: false);

        content.Children.Add(DashboardSection("SOLO SERVERS", 0));
        AddServers(catalog.Servers.Where(s => s.ClusterId is null));
        content.Children.Add(DashboardSection("SERVER CLUSTERS", 8));
        var clusters = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 0 };
        foreach (var item in catalog.Clusters.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            clusters.Children.Add(ClusterCard(item, () =>
            { selectedCluster = item.Id; selectedServer = null; managementTab = 0; Render(); }));
        }
        if (catalog.Clusters.Count == 0) content.Children.Add(Label("No clusters created yet.", 16));
        else content.Children.Add(CreateCardRail(clusters));

    }

    private void AddServers(IEnumerable<ServerDefinition> servers)
    {
        var cards = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 0 };
        var ordered = designPreview ? servers : servers.OrderBy(s => s.Settings.Name, StringComparer.CurrentCultureIgnoreCase);
        foreach (var server in ordered)
            cards.Children.Add(ServerCard(server, () =>
            { selectedServer = server.Id; managementTab = 0; Render(); }));
        if (cards.Children.Count == 0) content.Children.Add(Label("No servers created yet.", 16));
        else content.Children.Add(CreateCardRail(cards));
    }

    private void AddBack()
    {
        var back = new Button { Content = selectedServer is not null && selectedCluster is not null ? "← Back to cluster" : "← Back to dashboard" };
        back.Click += (_, _) =>
        {
            if (selectedServer is not null) selectedServer = null;
            else selectedCluster = null;
            managementTab = 0;
            Render();
        };
        content.Children.Add(back);
    }

    private static Button Card(string name, string description, Action open)
    {
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(Label(name, 20, true));
        body.Children.Add(Label(description, 16));
        body.Children.Add(new TextBlock { Text = "Status unavailable  ›", Foreground = Brush.Parse("#B8C6D4") });
        var card = new Button
        {
            Width = 330, MinHeight = 150, Margin = new Thickness(0, 0, 18, 18), Padding = new Thickness(24),
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Background = Brush.Parse("#1B2732"), Content = body
        };
        card.Click += (_, _) => open();
        Avalonia.Automation.AutomationProperties.SetName(card, $"Open {name}, {description}");
        return card;
    }

    internal static TextBlock Label(string text, double size, bool bold = false) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap,
        FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal
    };
}
