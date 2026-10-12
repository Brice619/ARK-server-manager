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
    private readonly Button dashboardNavigation = new() { Content = "Dashboard", Margin = new Thickness(16, 0) };
    private readonly Button installationNavigation = new() { Content = "Installations", Margin = new Thickness(0, 0, 16, 0) };
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
        if (OperatingSystem.IsWindows())
        {
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaTitleBarHeightHint = 64;
            WindowDecorations = WindowDecorations.BorderOnly;
        }
        Width = 1440; Height = 920; MinWidth = 900; MinHeight = 640;
        if (Screens.Primary is { } screen)
        {
            Width = Math.Min(Width, screen.WorkingArea.Width / screen.Scaling);
            Height = Math.Min(Height, screen.WorkingArea.Height / screen.Scaling - 32);
        }
        FontFamily = new FontFamily("Segoe UI, Inter, sans-serif");
        Background = Brush.Parse("#0F1921");
        designPreview = args is ["--design-preview"];
        if (designPreview) catalog = PreviewCatalog();
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
        content.Children.Clear();
        if (startupNotice is not null)
        {
            var notice = new StackPanel { Spacing = 8 };
            notice.Children.Add(Label(startupNotice, 15));
            notice.Children.Add(ActionButton("Dismiss", () => { this.startupNotice = null; Render(); }));
            content.Children.Add(Surface(notice));
        }
        content.Margin = new Thickness(38, Height < 760 ? 18 : 38, 20, 28);
        creationFooter.Children.Clear(); creationFooter.IsVisible = false;
        AddPendingDeletionNotice();
        connection.IsVisible = !string.IsNullOrWhiteSpace(connection.Text);
        UpdateNavigation();
        dashboardNavigation.Foreground = Brush.Parse(installationsPage ? "#B8C6D4" : "#FFC04D");
        installationNavigation.Foreground = Brush.Parse(installationsPage ? "#FFC04D" : "#B8C6D4");
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
        content.Children.Add(Label("Your ARK Servers", 36, true));
        var subtitle = Label("Your worlds, on your computer.", 18); subtitle.Foreground = Brush.Parse("#A9B8C7");
        content.Children.Add(subtitle);
        AddRecoveryNotice(details: false);
        var standaloneCount = catalog.Servers.Count(s => s.ClusterId is null);
        content.Children.Add(SectionHeading("Standalone Servers", $"{standaloneCount} servers", 14));
        AddServers(catalog.Servers.Where(s => s.ClusterId is null));
        content.Children.Add(SectionHeading("Server Clusters", $"{catalog.Clusters.Count} clusters", 28));
        var clusters = new WrapPanel();
        foreach (var item in catalog.Clusters.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var count = catalog.Servers.Count(s => s.ClusterId == item.Id);
            clusters.Children.Add(ClusterCard(item, count, () =>
            { selectedCluster = item.Id; selectedServer = null; managementTab = 0; Render(); }));
        }
        if (catalog.Clusters.Count == 0) content.Children.Add(Label("No clusters created yet.", 16));
        else content.Children.Add(clusters);
        var footer = Label(designPreview ? "Design preview · No servers are running" : "All servers are hosted on this computer.", 13);
        footer.Foreground = Brush.Parse("#8192A2"); footer.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        footer.Margin = new Thickness(0, 72, 18, 0); content.Children.Add(footer);
    }

    private void AddServers(IEnumerable<ServerDefinition> servers)
    {
        var cards = new WrapPanel();
        var ordered = designPreview ? servers : servers.OrderBy(s => s.Settings.Name, StringComparer.CurrentCultureIgnoreCase);
        foreach (var server in ordered)
            cards.Children.Add(ServerCard(server, () =>
            { selectedServer = server.Id; managementTab = 0; Render(); }));
        if (cards.Children.Count == 0) content.Children.Add(Label("No servers created yet.", 16));
        else content.Children.Add(cards);
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
