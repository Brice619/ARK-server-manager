using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private static readonly IBrush Amber = Brush.Parse("#FFBC43");
    private static readonly IBrush Muted = Brush.Parse("#A9B8C7");
    private readonly Button settingsNavigation = new();
    private Border? homeUnderline, installationUnderline, settingsUnderline;
    private static readonly Dictionary<string, Bitmap> Artwork = [];
    private const string HomeIcon = "M2,10 L12,2 L22,10 M5,9 L5,22 L10,22 L10,15 L15,15 L15,22 L20,22 L20,9";
    private const string BoxIcon = "M2,7 L12,2 L22,7 L12,12 Z M2,7 L2,19 L12,24 L22,19 L22,7 M12,12 L12,24";
    private const string ClusterIcon = "M28,16 L10,48 M28,16 L46,48 M10,48 L46,48 M28,2 A7,7 0 1 1 28,16 A7,7 0 1 1 28,2 M10,41 A7,7 0 1 1 10,55 A7,7 0 1 1 10,41 M46,41 A7,7 0 1 1 46,55 A7,7 0 1 1 46,41";
    private const string BellIcon = "M7,18 L7,10 A6,6 0 0 1 19,10 L19,18 L22,21 L4,21 Z M10,24 A3,3 0 0 0 16,24";
    private const string GearIcon = "M10,2 L16,2 L17,6 L21,8 L25,7 L28,12 L25,15 L25,19 L28,22 L25,27 L21,26 L17,28 L16,32 L10,32 L9,28 L5,26 L1,27 L-2,22 L1,19 L1,15 L-2,12 L1,7 L5,8 L9,6 Z M13,11 A6,6 0 1 0 13,23 A6,6 0 1 0 13,11";

    private Control BuildHeader()
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), RowDefinitions = new RowDefinitions("64,0"),
            Height = 64, Margin = new Thickness(26, 0, 24, 0) };
        if (OperatingSystem.IsWindows()) Avalonia.Controls.Chrome.WindowDecorationProperties.SetElementRole(header, Avalonia.Input.WindowDecorationsElementRole.TitleBar);
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 13, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(VectorIcon("M2,32 L18,2 L34,32 M9,22 L27,22", Brush.Parse("#8DD5E4"), 32));
        brand.Children.Add(Label("ARK Server Manager", 19, true)); header.Children.Add(brand);
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        Avalonia.Controls.Chrome.WindowDecorationProperties.SetElementRole(navigation, Avalonia.Input.WindowDecorationsElementRole.User);
        homeUnderline = Navigation(dashboardNavigation, "Dashboard", HomeIcon);
        installationUnderline = Navigation(installationNavigation, "Installations", BoxIcon);
        settingsUnderline = Navigation(settingsNavigation, "App Settings", GearIcon);
        navigation.Children.Add(homeUnderline); navigation.Children.Add(installationUnderline); navigation.Children.Add(settingsUnderline);
        Grid.SetColumn(navigation, 1); header.Children.Add(navigation);
        dashboardNavigation.Click += (_, _) => { ResetPage(); Render(); };
        installationNavigation.Click += async (_, _) =>
        {
            ResetPage(); installationsPage = true; Render();
            if (!installationSearchStarted && client is not null) await SearchInstallationsAsync();
        };
        settingsNavigation.Click += (_, _) => { ResetPage(); settingsPage = true; Render(); };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Controls.Chrome.WindowDecorationProperties.SetElementRole(actions, Avalonia.Input.WindowDecorationsElementRole.User);
        var create = new Button
        {
            Content = "+  Create Server / Cluster", Background = Amber, Foreground = Brush.Parse("#181B1F"),
            FontWeight = FontWeight.SemiBold, FontSize = 14, Padding = new Thickness(18, 12), CornerRadius = new CornerRadius(5),
            IsEnabled = true
        };
        create.Click += (_, _) => { ResetPage(); ResetCreationDraft(); creationPage = true; Render(); };
        create.Classes.Add("accent");
        actions.Children.Add(create);
        var bell = new Grid { Width = 37, Height = 35 };
        bell.Children.Add(VectorIcon(BellIcon, Muted, 25));
        notificationBadgeText = Label("", 10, true); notificationBadgeText.Foreground = Brush.Parse("#181B1F");
        notificationBadgeText.HorizontalAlignment = HorizontalAlignment.Center;
        notificationBadgeText.VerticalAlignment = VerticalAlignment.Center;
        notificationBadge = new Border { Background = Amber, CornerRadius = new CornerRadius(10), MinWidth = 19, Height = 19,
            Padding = new Thickness(4, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -3, -3, 0), Child = notificationBadgeText, IsVisible = false };
        bell.Children.Add(notificationBadge);
        var notifications = new Button { Content = bell, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(8) };
        notificationEntry = notifications; UpdateNotificationBadge();
        Avalonia.Automation.AutomationProperties.SetName(notifications, "Open activity and notifications");
        notifications.Click += async (_, _) => { ResetPage(); activityPage = true; Render(); if (client is not null) await LoadActivityAsync(); };
        actions.Children.Add(notifications); Grid.SetColumn(actions, 2); header.Children.Add(actions);
        if (OperatingSystem.IsWindows()) actions.Children.Add(BuildWindowActions());
        bool? compactHeader = null;
        header.SizeChanged += (_, _) =>
        {
            if (header.Bounds.Width <= 0) return;
            var compact = header.Bounds.Width < (OperatingSystem.IsWindows() ? 1200 : 1070);
            if (compactHeader == compact) return;
            compactHeader = compact; header.Height = compact ? 128 : 64;
            header.RowDefinitions[1].Height = new GridLength(compact ? 64 : 0);
            Grid.SetRow(navigation, compact ? 1 : 0);
            Grid.SetColumn(navigation, compact ? 0 : 1);
            Grid.SetColumnSpan(navigation, compact ? 3 : 1);
        };
        return new Border { Background = Brush.Parse("#17222C"), BorderBrush = Brush.Parse("#2C3945"), BorderThickness = new Thickness(0, 0, 0, 1), Child = header };
    }

    private Control BuildWindowActions()
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        Button Caption(string name, string path, Action action)
        {
            Control symbol = name == "Minimize window" ? new Border { Width = 14, Height = 2, Background = Muted,
                VerticalAlignment = VerticalAlignment.Center } : VectorIcon(path, Muted, 14);
            var button = new Button { Content = symbol, Width = 38, Height = 38, Padding = new Thickness(10),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(4) };
            Avalonia.Automation.AutomationProperties.SetName(button, name); ToolTip.SetTip(button, name);
            button.Click += (_, _) => action();
            button.PointerEntered += (_, _) => button.Background = Brush.Parse(name == "Close window" ? "#B64037" : "#263B4A");
            button.PointerExited += (_, _) => button.Background = Brushes.Transparent;
            return button;
        }
        buttons.Children.Add(Caption("Minimize window", "M2,9 L12,9", () => WindowState = WindowState.Minimized));
        var maximize = Caption("Maximize or restore window", "M2,2 L12,2 L12,12 L2,12 Z", () =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
        buttons.Children.Add(maximize);
        PropertyChanged += (_, change) =>
        {
            if (change.Property == WindowStateProperty) maximize.Content = VectorIcon(WindowState == WindowState.Maximized
                ? "M2,5 L9,5 L9,12 L2,12 Z M5,2 L12,2 L12,9" : "M2,2 L12,2 L12,12 L2,12 Z", Muted, 14);
        };
        buttons.Children.Add(Caption("Close window", "M2,2 L12,12 M12,2 L2,12", Close));
        return buttons;
    }

    private static Border Navigation(Button button, string title, string geometry)
    {
        button.Margin = new Thickness(0); button.Background = Brushes.Transparent; button.BorderThickness = new Thickness(0);
        button.Padding = new Thickness(18, 0); button.Height = 60; button.FontSize = 15;
        var body = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        body.Children.Add(VectorIcon(geometry, Muted, 21)); body.Children.Add(Label(title, 15)); button.Content = body;
        Avalonia.Automation.AutomationProperties.SetName(button, title);
        return new Border { BorderThickness = new Thickness(0, 0, 0, 3), BorderBrush = Brushes.Transparent, Child = button };
    }

    private void ResetPage()
    {
        installationsPage = activityPage = settingsPage = creationPage = false;
        selectedCluster = selectedServer = null;
    }

    private void UpdateNavigation()
    {
        if (homeUnderline is null) return;
        homeUnderline.BorderBrush = !installationsPage && !settingsPage ? Amber : Brushes.Transparent;
        installationUnderline!.BorderBrush = installationsPage ? Amber : Brushes.Transparent;
        settingsUnderline!.BorderBrush = settingsPage ? Amber : Brushes.Transparent;
        foreach (var (button, selected) in new[] { (dashboardNavigation, !installationsPage && !settingsPage), (installationNavigation, installationsPage), (settingsNavigation, settingsPage) })
            if (button.Content is StackPanel panel && panel.Children[0] is Avalonia.Controls.Shapes.Path symbol)
                symbol.Stroke = selected ? Amber : Muted;
    }

    private static Control SectionHeading(string title, string count, double margin)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, margin, 18, 10) };
        row.Children.Add(Label(title, 25, true));
        var rule = new Border { Height = 1, Background = Brush.Parse("#2A3945"), Margin = new Thickness(18, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(rule, 1); row.Children.Add(rule);
        var total = Label(count, 15); total.Foreground = Muted; total.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(total, 2); row.Children.Add(total); return row;
    }

    private Control ServerCard(ServerDefinition server, Action open)
    {
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("190,*,18") };
        var key = server.Map.Contains("fjord", StringComparison.OrdinalIgnoreCase) ? "fjord"
            : server.Map.Contains("ragnarok", StringComparison.OrdinalIgnoreCase) || server.Map.Contains("scorched", StringComparison.OrdinalIgnoreCase) ? "canyon"
            : server.Map.Contains("island", StringComparison.OrdinalIgnoreCase) ? "island" : null;
        Control picture = key is null ? new Border { Background = Brush.Parse("#14212B"), Child = VectorIcon(BoxIcon, Muted, 44) }
            : new Image { Source = LoadArtwork(key), Stretch = Stretch.UniformToFill };
        layout.Children.Add(new Border { CornerRadius = new CornerRadius(5), ClipToBounds = true, Child = picture });
        var information = CardInformation(server.Settings.Name, server.Edition);
        var running = designPreview && server.Settings.Name != "Legacy PvE";
        information.Children.Add(designPreview ? Status(running ? "Running" : "Stopped",
            running ? "#64E581" : "#FF6062") : ObservedProcessStatus(serverId: server.Id));
        var players = designPreview ? server.Settings.Name == "Island Test" ? "8" : server.Settings.Name == "Fjordur Solo" ? "3" : "0" : "—";
        var population = new StackPanel { Spacing = 3, Margin = new Thickness(0, 10, 0, 0) };
        population.Children.Add(Label($"{players} / {server.Settings.MaxPlayers}", 18, true));
        var caption = Label("players", 13); caption.Foreground = Muted; population.Children.Add(caption);
        information.Children.Add(population); Grid.SetColumn(information, 1); layout.Children.Add(information);
        AddChevron(layout); return DesignCard(layout, server.Settings.Name, 190, open);
    }

    private Control ClusterCard(ClusterDefinition cluster, int maps, Action open)
    {
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("112,*,18") };
        var color = cluster.Edition == ArkEdition.ASA ? Brush.Parse("#65B6FF") : Amber;
        var emblem = new Border
        {
            Width = 92, Height = 104, CornerRadius = new CornerRadius(6), Background = Brush.Parse("#101A22"),
            BorderBrush = Brush.Parse("#30414E"), BorderThickness = new Thickness(1), Child = VectorIcon(ClusterIcon, color, 58),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
        };
        layout.Children.Add(emblem);
        var information = CardInformation(cluster.Name, cluster.Edition);
        var partial = cluster.Name == "Modded Event Cluster";
        information.Children.Add(designPreview ? Status(partial ? "Partial" : "Running",
            partial ? "#FFBC43" : "#64E581") : ObservedProcessStatus(clusterId: cluster.Id));
        var mapCount = Label($"{maps} maps", 16); mapCount.Foreground = Muted; mapCount.Margin = new Thickness(0, 9, 0, 0);
        information.Children.Add(mapCount); Grid.SetColumn(information, 1); layout.Children.Add(information);
        AddChevron(layout); return DesignCard(layout, cluster.Name, 152, open);
    }

    private static StackPanel CardInformation(string name, ArkEdition edition)
    {
        var body = new StackPanel { Margin = new Thickness(16, 12, 8, 8), Spacing = 14, VerticalAlignment = VerticalAlignment.Center };
        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = Label(name, 18, true); text.TextWrapping = TextWrapping.NoWrap; text.TextTrimming = TextTrimming.CharacterEllipsis;
        title.Children.Add(text);
        var color = edition == ArkEdition.ASA ? Brush.Parse("#75BCF6") : Amber;
        var badge = new Border { BorderBrush = color, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 4), Margin = new Thickness(8, 0, 0, 0), Child = new TextBlock { Text = edition.ToString(), FontSize = 12, Foreground = color } };
        Grid.SetColumn(badge, 1); title.Children.Add(badge); body.Children.Add(title); return body;
    }

    private static Control Status(string text, string color)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        line.Children.Add(new Border { Width = 13, Height = 13, CornerRadius = new CornerRadius(7), Background = Brush.Parse(color), VerticalAlignment = VerticalAlignment.Center });
        line.Children.Add(new TextBlock { Text = text, FontSize = 16, Foreground = Brush.Parse(color) }); return line;
    }

    private static Button DesignCard(Control body, string name, double height, Action open)
    {
        var button = new Button { Width = 442, Height = height, Margin = new Thickness(0, 0, 18, 10), Padding = new Thickness(9),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
            BorderBrush = Brush.Parse("#2B3A46"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = [new GradientStop(Color.Parse("#1B2832"), 0), new GradientStop(Color.Parse("#142029"), 1)]
            }, Content = body };
        button.Resources["ButtonBackgroundPointerOver"] = Brush.Parse("#1D2D39");
        button.Resources["ButtonBorderBrushPointerOver"] = Amber;
        Avalonia.Automation.AutomationProperties.SetName(button, "Open " + name);
        button.Click += (_, _) => open(); return button;
    }

    private static void AddChevron(Grid grid)
    {
        var arrow = new TextBlock { Text = "›", FontSize = 30, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(arrow, 2); grid.Children.Add(arrow);
    }

    internal static Control VectorIcon(string data, IBrush color, double size) => new Avalonia.Controls.Shapes.Path
    { Data = Geometry.Parse(data), Stroke = color, StrokeThickness = 2, Width = size, Height = size, Stretch = Stretch.Uniform,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    private static Bitmap LoadArtwork(string key)
    {
        if (!Artwork.TryGetValue(key, out var bitmap))
        {
            using var stream = AssetLoader.Open(new Uri($"avares://ArkServerManager.Desktop/Assets/{key}.png"));
            Artwork.Add(key, bitmap = new Bitmap(stream));
        }
        return bitmap;
    }

    private static CatalogDocument PreviewCatalog()
    {
        var main = Guid.NewGuid(); var modded = Guid.NewGuid();
        ServerSettings Settings(string name, int players, int port) => new() { Name = name, MaxPlayers = players, GamePort = port, QueryPort = port + 2, RconPort = port + 3 };
        var servers = new List<ServerDefinition>
        {
            new(Guid.NewGuid(), ArkEdition.ASA, "TheIsland", "preview", Settings("Island Test", 70, 7800)),
            new(Guid.NewGuid(), ArkEdition.ASE, "Ragnarok", "preview", Settings("Legacy PvE", 50, 7810)),
            new(Guid.NewGuid(), ArkEdition.ASE, "Fjordur", "preview", Settings("Fjordur Solo", 30, 7820))
        };
        var maps = new[] { "TheIsland", "ScorchedEarth", "Aberration", "Extinction", "Fjordur", "Genesis2", "Valguero" };
        for (var i = 0; i < maps.Length; i++) servers.Add(new(Guid.NewGuid(), i < 4 ? ArkEdition.ASE : ArkEdition.ASA, maps[i], "preview",
            Settings(MapName(maps[i]), 70, 7900 + i * 10), i < 4 ? main : modded));
        // Illustrative IDs make populated mod screens reachable without downloading or claiming compatibility.
        servers[0] = servers[0] with { Mods = ["1234567890", "2345678901", "3456789012"] };
        servers[1] = servers[1] with { Mods = ["1234567890", "2345678901", "3456789012"] };
        return new(1, [new(main, "Main PvE Cluster", ArkEdition.ASE, Settings("Main PvE Cluster", 70, 8000)),
            new(modded, "Modded Event Cluster", ArkEdition.ASA, Settings("Modded Event Cluster", 70, 8010))], servers);
    }

    private void RenderAppSettings()
    {
        RenderApplicationPreferences();
    }

    private void RenderCreationChoice()
    {
        RenderCreationWizard();
    }
}
