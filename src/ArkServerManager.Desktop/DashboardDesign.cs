using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private Button? headerCreateButton;
    private static readonly IBrush Amber = Brush.Parse("#FFBC43");
    private static readonly IBrush Muted = Brush.Parse("#A9B8C7");
    private static readonly Dictionary<string, Bitmap> Artwork = [];
    private const string BoxIcon = "M2,7 L12,2 L22,7 L12,12 Z M2,7 L2,19 L12,24 L22,19 L22,7 M12,12 L12,24";
    private const string ClusterIcon = "M28,16 L10,48 M28,16 L46,48 M10,48 L46,48 M28,2 A7,7 0 1 1 28,16 A7,7 0 1 1 28,2 M10,41 A7,7 0 1 1 10,55 A7,7 0 1 1 10,41 M46,41 A7,7 0 1 1 46,55 A7,7 0 1 1 46,41";
    private const string GearIcon = "M10,2 L16,2 L17,6 L21,8 L25,7 L28,12 L25,15 L25,19 L28,22 L25,27 L21,26 L17,28 L16,32 L10,32 L9,28 L5,26 L1,27 L-2,22 L1,19 L1,15 L-2,12 L1,7 L5,8 L9,6 Z M13,11 A6,6 0 1 0 13,23 A6,6 0 1 0 13,11";

    private Control BuildHeader()
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), RowDefinitions = new RowDefinitions("102"),
            Height = 102, Background = Brushes.Transparent, Margin = new Thickness(44, 0, 44, 0) };
        if (OperatingSystem.IsWindows())
        {
            Avalonia.Controls.Chrome.WindowDecorationProperties.SetElementRole(header, Avalonia.Input.WindowDecorationsElementRole.User);
            PixelPoint? dragStart = null;
            PixelPoint windowStart = default;
            header.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(header).Properties.IsLeftButtonPressed || e.Source is not Visual source
                    || source.GetVisualAncestors().Prepend(source).Any(element => element is Button)) return;
                if (e.ClickCount == 2)
                    WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                else
                {
                    var pointerPosition = this.PointToScreen(e.GetPosition(this));
                    if (WindowState == WindowState.Maximized)
                    {
                        var horizontalFraction = e.GetPosition(this).X / Bounds.Width;
                        WindowState = WindowState.Normal;
                        Position = new PixelPoint(pointerPosition.X - (int)(Width * RenderScaling * horizontalFraction),
                            pointerPosition.Y - (int)(30 * RenderScaling));
                    }
                    dragStart = pointerPosition;
                    windowStart = Position;
                    e.Pointer.Capture(header);
                }
                e.Handled = true;
            };
            header.PointerMoved += (_, e) =>
            {
                if (dragStart is not { } start || !e.GetCurrentPoint(header).Properties.IsLeftButtonPressed) return;
                var pointerPosition = this.PointToScreen(e.GetPosition(this));
                Position = new PixelPoint(windowStart.X + pointerPosition.X - start.X,
                    windowStart.Y + pointerPosition.Y - start.Y);
            };
            header.PointerReleased += (_, e) => { dragStart = null; e.Pointer.Capture(null); };
            header.PointerCaptureLost += (_, _) => dragStart = null;
        }
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var logo = new Image { Source = LoadArtwork("ark-logo"), Width = 108, Height = 108, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapInterpolationMode(logo, BitmapInterpolationMode.HighQuality); brand.Children.Add(logo);
        // Display the supplied wordmark at its original proportions. The viewport
        // selects the lettering from the source image without altering the asset.
        const double wordmarkScale = 340d / 1076d;
        var wordmarkCanvas = new Canvas { Width = 340, Height = 40, ClipToBounds = true };
        var wordmark = new Image { Source = LoadArtwork("ark-wordmark-source"),
            Width = 1983 * wordmarkScale, Height = 793 * wordmarkScale, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapInterpolationMode(wordmark, BitmapInterpolationMode.HighQuality);
        Canvas.SetLeft(wordmark, -674 * wordmarkScale);
        Canvas.SetTop(wordmark, -338 * wordmarkScale);
        wordmarkCanvas.Children.Add(wordmark);
        wordmarkCanvas.VerticalAlignment = VerticalAlignment.Center;
        wordmarkCanvas.Margin = new Thickness(0, 30, 0, 0);
        brand.Children.Add(wordmarkCanvas);
        var brandButton = new Button { Content = brand, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), Height = 108, VerticalAlignment = VerticalAlignment.Top };
        brandButton.Resources["ButtonBackgroundPointerOver"] = Brushes.Transparent;
        brandButton.Resources["ButtonBackgroundPressed"] = Brushes.Transparent;
        Avalonia.Automation.AutomationProperties.SetName(brandButton, "ARK Server Manager menu");
        ToolTip.SetTip(brandButton, "App menu");
        var menu = new ContextMenu();
        MenuItem Entry(string title, Action action)
        {
            var item = new MenuItem { Header = title }; item.Click += (_, _) => action(); menu.Items.Add(item); return item;
        }
        Entry("Your ARK Servers", () => { ResetPage(); Render(); });
        var installations = Entry("Installations", () => { ResetPage(); installationsPage = true; Render(); });
        installations.Click += async (_, _) => { if (!installationSearchStarted && client is not null) await SearchInstallationsAsync(); };
        Entry("App Settings", () => { ResetPage(); settingsPage = true; Render(); });
        var activity = activityMenuEntry = Entry("Activity", () => { ResetPage(); activityPage = true; Render(); });
        activity.Click += async (_, _) => { if (client is not null) await LoadActivityAsync(); };
        menu.Items.Add(new Separator());
        Entry("Minimize window", () => WindowState = WindowState.Minimized);
        Entry("Maximize or restore window", () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
        Entry("Close window", Close);
        brandButton.ContextMenu = menu;
        brandButton.Click += (_, _) => menu.Open(brandButton);
        header.Children.Add(brandButton);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 35, 0, 0) };
        Avalonia.Controls.Chrome.WindowDecorationProperties.SetElementRole(actions, Avalonia.Input.WindowDecorationsElementRole.User);
        var create = headerCreateButton = new Button
        {
            Content = "+   Create Server / Cluster", Width = 296, Height = 53, Background = Brush.Parse("#493608"), Foreground = Brush.Parse("#FFE68A"), BorderBrush = Brush.Parse("#FFD23F"), BorderThickness = new Thickness(1),
            FontWeight = FontWeight.SemiBold, FontSize = 20, Padding = new Thickness(20, 12), CornerRadius = new CornerRadius(7),
            IsEnabled = true
        };
        Avalonia.Automation.AutomationProperties.SetName(create, "Create Server / Cluster");
        create.Click += (_, _) => { ResetPage(); ResetCreationDraft(); creationPage = true; Render(); };
        create.Resources["ButtonBackgroundPointerOver"] = Brush.Parse("#665015"); create.Resources["ButtonBorderBrushPointerOver"] = Brush.Parse("#FFE68A");
        actions.Children.Add(create);
        Grid.SetColumn(actions, 2); header.Children.Add(actions);
        header.SizeChanged += (_, _) =>
        {
            var compact = header.Bounds.Width < 760 && create.IsVisible;
            header.Height = compact ? 170 : 102;
            actions.Margin = new Thickness(0, compact ? 112 : 35, 0, 0);
        };
        return header;
    }
    private void ResetPage()
    {
        installationsPage = activityPage = settingsPage = creationPage = false;
        selectedCluster = selectedServer = null;
    }

    private static Control DashboardSection(string title, double margin)
    {
        var text = Label(title, 20, true); text.Foreground = Brush.Parse("#6D9DC6");
        text.LetterSpacing = 3; text.Margin = new Thickness(0, 0, 0, 0);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, margin, 0, 0) };
        row.Children.Add(text);
        if (title == "SERVER CLUSTERS")
        {
            var rule = new Border { Height = 1, Background = Brush.Parse("#2A4358"), Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(rule, 1); row.Children.Add(rule);
        }
        return row;
    }

    private static Control SectionHeading(string title, string count, double margin)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, margin, 0, title == "Your ARK Servers" ? 0 : 10) };
        var heading = Label(title, title == "Your ARK Servers" ? 46 : 25, true);
        if (title == "Your ARK Servers") heading.LetterSpacing = 1.8;
        row.Children.Add(heading);
        var rule = new Border { Height = 1, Background = Brush.Parse("#2A3945"), Margin = new Thickness(24, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(rule, 1); row.Children.Add(rule);
        var total = Label(count, 15); total.Foreground = Muted; total.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(total, 2); row.Children.Add(total); return row;
    }

    private static string? MapArtwork(string map) => map.ToLowerInvariant() switch
    {
        var m when m.Contains("island") => "island",
        var m when m.Contains("scorched") => "canyon",
        var m when m.Contains("fjord") => "fjord",
        var m when m.Contains("aberration") => "cavern-v1",
        var m when m.Contains("extinction") => "ruined-city-v1",
        _ => "island" // Temporary available landscape until dedicated map art is supplied.
    };

    private static Control MapPicture(string map)
    {
        if (map.Contains("island", StringComparison.OrdinalIgnoreCase) || map.Contains("scorched", StringComparison.OrdinalIgnoreCase) || map.Contains("aberration", StringComparison.OrdinalIgnoreCase))
            return ApprovedMapArtwork(map);
        var key = MapArtwork(map);
        return key is null ? new Border { Background = Brush.Parse("#14212B"), Child = Label(MapName(map), 20) }
            : new Image { Source = LoadArtwork(key), Stretch = Stretch.UniformToFill };
    }

    private Control ServerCard(ServerDefinition server, Action open)
        => LandscapeCard(server.Settings.Name, server.Edition, MapPicture(server.Map),
            MapName(server.Map), designPreview ? Status("Stopped", "#FF5549") : ObservedProcessStatus(serverId: server.Id), open);

    private Control ClusterCard(ClusterDefinition cluster, Action open)
    {
        var members = catalog!.Servers.Where(s => s.ClusterId == cluster.Id).ToArray();
        Control picture;
        if (members.Length == 1) picture = MapPicture(members[0].Map);
        else if (members.Length == 0) picture = new Border { Background = Brush.Parse("#14212B") };
        else
        {
            var collage = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions(members.Length > 2 ? "*,*" : "*") };
            for (var i = 0; i < Math.Min(members.Length, 4); i++)
            {
                var image = MapPicture(members[i].Map);
                                if (members.Length == 3)
                {
                    Grid.SetColumn(image, i == 0 ? 0 : 1); Grid.SetRow(image, i == 2 ? 1 : 0);
                    if (i == 0) Grid.SetRowSpan(image, 2);
                }
                else { Grid.SetColumn(image, i % 2); Grid.SetRow(image, i / 2); }
                collage.Children.Add(image);
            }
            picture = collage;
        }
        return LandscapeCard(cluster.Name, cluster.Edition, picture,
            string.Join(" · ", members.Select(s => MapName(s.Map))),
            designPreview ? Status("Stopped", "#FF5549") : ObservedProcessStatus(clusterId: cluster.Id), open);
    }

    private static Control LandscapeCard(string name, ArkEdition edition, Control picture, string maps, Control status, Action open)
    {
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new Border { Height = 208, CornerRadius = new CornerRadius(5), ClipToBounds = true, Child = picture });
        var title = Label(name, 24, true); title.Margin = new Thickness(6, 0, 6, 0); title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis;
        body.Children.Add(title);
        var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(6, 0, 6, 0) };
        badges.Children.Add(new Border { Background = Brush.Parse("#1D2D3E"), BorderBrush = Brush.Parse("#2B4255"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(12, 5), Child = Label(edition.ToString(), 16) });
        badges.Children.Add(new Border { Background = Brush.Parse("#14212B"), BorderBrush = Brush.Parse("#34424D"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(10, 5), Child = status });
                if (status is StackPanel statusLine && statusLine.Children.LastOrDefault() is TextBlock statusText && statusText.Foreground is ISolidColorBrush statusColor)
        {
            var badge = (Border)badges.Children[1]; var color = statusColor.Color;
            badge.Background = new SolidColorBrush(Color.FromArgb(24, color.R, color.G, color.B));
            badge.BorderBrush = new SolidColorBrush(Color.FromArgb(65, color.R, color.G, color.B));
        }
        body.Children.Add(badges);
        var mapLine = new Grid { ColumnDefinitions = new ColumnDefinitions("24,*"), Margin = new Thickness(9, 0, 6, 0), MinHeight = 24 };
        mapLine.Children.Add(VectorIcon("M2,4 L9,1 L16,4 L23,1 L23,20 L16,23 L9,20 L2,23 Z M9,1 L9,20 M16,4 L16,23", Muted, 24));
        var mapText = Label(maps, 16); mapText.TextWrapping = TextWrapping.NoWrap; mapText.Foreground = Muted; mapText.TextTrimming = TextTrimming.CharacterEllipsis;
        mapText.VerticalAlignment = VerticalAlignment.Center; mapText.Margin = new Thickness(15, 0, 0, 0); Grid.SetColumn(mapText, 1); mapLine.Children.Add(mapText); body.Children.Add(mapLine);
        return DesignCard(body, name, 352, open);
    }
    private static Control Status(string text, string color)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        line.Children.Add(new Border { Width = 13, Height = 13, CornerRadius = new CornerRadius(7), Background = Brush.Parse(color), VerticalAlignment = VerticalAlignment.Center });
        line.Children.Add(new TextBlock { Text = text, FontSize = 16, Foreground = Brush.Parse(color) }); return line;
    }

    private static Button DesignCard(Control body, string name, double height, Action open)
    {
        var button = new Button { Width = 472, Height = height, Margin = new Thickness(0, 0, 16, 10), Padding = new Thickness(9),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
            BorderBrush = Brush.Parse("#2A455B"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = [new GradientStop(Color.Parse("#0F1D29"), 0), new GradientStop(Color.Parse("#0D1923"), 1)]
            }, Content = body };
        button.Resources["ButtonBackgroundPointerOver"] = Brush.Parse("#1D2D39");
        button.Resources["ButtonBorderBrushPointerOver"] = Brush.Parse("#65A9D3");
        Avalonia.Automation.AutomationProperties.SetName(button, "Open " + name);
        button.Click += (_, _) => open(); return button;
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
