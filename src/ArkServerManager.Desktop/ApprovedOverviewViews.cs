using Avalonia;
using Avalonia.Input.Platform;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private const string DatabaseGlyph = "M3,6 A9,4 0 1 0 21,6 A9,4 0 1 0 3,6 M3,6 L3,21 A9,4 0 0 0 21,21 L21,6 M3,13 A9,4 0 0 0 21,13";
    private const string ClockGlyph = "M12,1 A11,11 0 1 0 12,23 A11,11 0 1 0 12,1 M12,5 L12,12 L18,15";
    private const string PlayersGlyph = "M9,1 A4,4 0 1 0 9,9 A4,4 0 1 0 9,1 M2,22 L2,17 A7,6 0 0 1 16,17 L16,22 Z M18,2 A4,4 0 0 1 18,10 M20,13 A6,6 0 0 1 24,19 L24,22";
    private static readonly Guid ReviewServerId = Guid.Parse("b2c47c87-394e-4ab2-8262-af11f4dd951c");
    // These fixture fields are reachable only from --design-preview and native review checks.
    private bool reviewServerVisible;
    private bool reviewServerRunning;
    private readonly HashSet<Guid> overviewPreviewRunning = [];
    private readonly Dictionary<Guid, string> overviewModes = [];
    private bool IsReviewServer(Guid id) => designPreview && id == ReviewServerId;
    private ServerDefinition ReviewServer() => !designPreview ? throw new InvalidOperationException("Design fixtures are unavailable in the installed app.") : new(ReviewServerId, ArkEdition.ASE, "TheIsland", "",
        new() { Name = "The Island (Dummy)", MaxPlayers = 5, GamePort = 7777, QueryPort = 27015, RconPort = 32330,
            Values = new() { ["ServerPVE"] = "True" } }) { Mods = reviewSavedMods ?? ["10001", "10002", "10003", "10004", "10005"] };

    // Viewports reuse the supplied artwork directly, without regenerating it or
    // baking labels and controls into the image.
    private static Control ReferenceArtwork(string asset, Rect area)
    {
        var canvas = new Canvas { Width = area.Width, Height = area.Height, ClipToBounds = true };
        var image = new Image { Source = LoadArtwork(asset), Width = 1536, Height = 1024, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapInterpolationMode(image, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
        Canvas.SetLeft(image, -area.X); Canvas.SetTop(image, -area.Y); canvas.Children.Add(image);
        return new Viewbox { Stretch = Stretch.UniformToFill, Child = canvas, ClipToBounds = true };
    }

    private static Control ApprovedMapArtwork(string map, bool portrait = false)
    {
        if (map.Contains("island", StringComparison.OrdinalIgnoreCase))
            return portrait ? ReferenceArtwork("approved-solo-overview", new(53, 379, 841, 542))
                : ReferenceArtwork("approved-cluster-overview", new(68, 464, 419, 198));
        if (map.Contains("scorched", StringComparison.OrdinalIgnoreCase))
            return ReferenceArtwork("approved-cluster-overview", new(520, 464, 413, 198));
        if (map.Contains("aberration", StringComparison.OrdinalIgnoreCase))
            return ReferenceArtwork("approved-cluster-overview", new(966, 464, 408, 198));
        return MapPicture(map);
    }

    private Border OverviewFrame(Control body, Thickness? padding = null) => new()
    {
        Background = new LinearGradientBrush { StartPoint = new(0, 0, RelativeUnit.Relative), EndPoint = new(1, 1, RelativeUnit.Relative),
            GradientStops = [new(Color.Parse(selectedCluster is not null && selectedServer is null ? "#002437" : "#10212F"), 0), new(Color.Parse(selectedCluster is not null && selectedServer is null ? "#001C2C" : "#0B1A26"), 1)] },
        BorderBrush = Brush.Parse("#2B4D64"), BorderThickness = new(1), CornerRadius = new(8),
        Padding = padding ?? new Thickness(22), Child = body
    };

    private static Button OverviewAction(string text, string color, Action? action, double width = double.NaN)
    {
        var button = new Button { Content = text, Width = width, Height = 53, Padding = new(20, 10), FontSize = 21,
            Foreground = Brush.Parse(color), Background = Brush.Parse("#0D202C"), BorderBrush = Brush.Parse(color),
            BorderThickness = new(1), CornerRadius = new(6), IsEnabled = action is not null };
        button.Resources["ButtonBackgroundPointerOver"] = Brush.Parse(color is "#FFD84B" or "#FFDA43" ? "#654707" : color is "#96FFA5" or "#A2FFAE" ? "#145E30" : color is "#FF655D" or "#FF625C" ? "#641A1D" : "#234253");
        button.Resources["ButtonBorderBrushPointerOver"] = Brush.Parse(color);
        button.Resources["ButtonForegroundDisabled"] = Brush.Parse("#7293B8");
        button.Resources["ButtonBackgroundDisabled"] = Brush.Parse("#10202E");
        button.Resources["ButtonBorderBrushDisabled"] = Brush.Parse("#2B4D64");
        var parts = text.Split("  ", 2); var label = parts.Length == 2 ? parts[1] : text;
        Avalonia.Automation.AutomationProperties.SetName(button, text == "‹" ? "Scroll maps left" : text == "›" ? "Scroll maps right" : label);
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        button.HorizontalContentAlignment = HorizontalAlignment.Center; button.VerticalContentAlignment = VerticalAlignment.Center;
        if (parts.Length == 2)
        {
            var symbol = parts[0] switch { "▶" => "M3,2 L20,12 L3,22 Z", "■" => "M3,3 L21,3 L21,21 L3,21 Z", "⟳" => "M4,9 A9,9 0 1 1 4,17 M4,2 L4,10 L12,10", "＋" => "M12,1 L12,23 M1,12 L23,12", _ => "M5,7 L6,23 L18,23 L19,7 M2,6 L22,6 M8,6 L8,2 L16,2 L16,6 M10,10 L10,20 M14,10 L14,20" };
            var icon = VectorIcon(symbol, Brush.Parse(color), 22); if (parts[0] is "▶" or "■") ((Avalonia.Controls.Shapes.Path)icon).Fill = Brush.Parse(color);
            content.Children.Add(icon);
        }
        var actionText = Label(label, text is "‹" or "›" ? 38 : 21); actionText.VerticalAlignment = VerticalAlignment.Center; actionText.TextWrapping = TextWrapping.NoWrap; content.Children.Add(actionText); button.Content = content;
        if (text is "‹" or "›")
        {
            var chevron = (Avalonia.Controls.Shapes.Path)VectorIcon(text == "‹" ? "M16,2 L6,12 L16,22" : "M6,2 L16,12 L6,22", Brush.Parse(color), 22);
            chevron.StrokeThickness = 3; button.Content = chevron; button.Padding = new(0);
            button.Resources["ButtonBackgroundPointerOver"] = Brush.Parse("#123E55");
            button.PropertyChanged += (_, e) => { if (e.Property == IsEnabledProperty) chevron.Stroke = Brush.Parse(button.IsEnabled ? color : "#345C74"); };
        }
        button.Background = new LinearGradientBrush { StartPoint = new(0, 0, RelativeUnit.Relative), EndPoint = new(0, 1, RelativeUnit.Relative), GradientStops = [new(Color.Parse(color is "#FFD84B" or "#FFDA43" ? "#4B3504" : color is "#96FFA5" or "#A2FFAE" ? "#0E4724" : color == "#FF655D" || color == "#FF625C" ? "#491012" : "#102433"), 0), new(Color.Parse("#0B1820"), 1)] };
        button.TemplateApplied += (_, e) =>
        {
            if (button.IsEnabled && parts.Length == 2 && label != "Delete" && e.NameScope.Find<Avalonia.Controls.Presenters.ContentPresenter>("PART_ContentPresenter") is { } presenter)
                presenter.BoxShadow = BoxShadows.Parse("inset 0 0 10 0 " + (color is "#FFD84B" or "#FFDA43" ? "#66500A" : color is "#96FFA5" or "#A2FFAE" ? "#185C2D" : color is "#FF655D" or "#FF625C" ? "#611C1E" : "#123D56"));
        };
        if (action is not null) button.Click += (_, e) => { e.Handled = true; action(); };
        return button;
    }

    private bool MapProcessActive(ServerDefinition server) => designPreview && overviewPreviewRunning.Contains(server.Id) || managedRuntimeStatus.Any(s => s.ServerId == server.Id && s.State is ServerState.Running or ServerState.Starting or ServerState.Stopping or ServerState.Recovering)
        || processStatus?.Servers.Any(s => s.ServerId == server.Id && s.State == RegisteredProcessState.Detected) == true;

    private static string GameMode(ServerSettings settings, string? ini = null)
    {
        var value = settings.Values.GetValueOrDefault("ServerPVE");
        if (ini is not null) value = new IniDocument(ini).GetValues("ServerSettings", "ServerPVE").LastOrDefault() ?? value;
        return bool.TryParse(value, out var pve) ? pve ? "PvE" : "PvP" : "";
    }

    private string ServerGameMode(ServerDefinition server)
    {
        var mode = GameMode(server.Settings); if (mode.Length > 0) return mode;
        if (overviewModes.TryGetValue(server.Id, out mode)) return mode;
        try
        {
            var path = Path.Combine(server.Directory, "ShooterGame", "Saved", "Config", OperatingSystem.IsWindows() ? "WindowsServer" : "LinuxServer", "GameUserSettings.ini");
            var inSettings = false;
            foreach (var raw in File.ReadLines(path).Take(10000))
            {
                var line = raw.Trim();
                if (line.StartsWith('[')) inSettings = line.Equals("[ServerSettings]", StringComparison.OrdinalIgnoreCase);
                if (inSettings && line.StartsWith("ServerPVE=", StringComparison.OrdinalIgnoreCase) && bool.TryParse(line[10..].Trim(), out var pve)) mode = pve ? "PvE" : "PvP";
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        overviewModes[server.Id] = mode ?? ""; return mode ?? "";
    }

    private void ApprovedDetailHeader(string name, ArkEdition edition, ServerSettings settings, ServerDefinition? server = null, ClusterDefinition? cluster = null)
    {
        var back = new Button { Content = "‹ All Servers", FontSize = 20, Foreground = Brush.Parse("#37CBFF"), Margin = cluster is null ? new Thickness(0) : new Thickness(0, 8, 0, 0),
            Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(0), Height = 30, HorizontalAlignment = HorizontalAlignment.Left };
        back.Click += (_, _) => { ResetPage(); Render(); }; content.Children.Add(back);
        back.Resources["ButtonBackgroundPointerOver"] = Brushes.Transparent;
        back.Resources["ButtonBackgroundPressed"] = Brushes.Transparent;
        back.VerticalContentAlignment = VerticalAlignment.Center;
        if (cluster is null)
        {
            var backContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            backContent.Children.Add(VectorIcon("M16,3 L7,12 L16,21", Brush.Parse("#37CBFF"), 16));
            var text = Label("All Servers", 20); text.Foreground = Brush.Parse("#37CBFF"); text.VerticalAlignment = VerticalAlignment.Center; backContent.Children.Add(text); back.Content = backContent;
            Avalonia.Automation.AutomationProperties.SetName(back, "All Servers");
        }
        if (cluster is not null)
        {
            var backContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
            var arrow = VectorIcon("M23,12 L2,12 M9,5 L2,12 L9,19", Brush.Parse("#35EEFF"), 20); arrow.Margin = new(4, 0, 0, 0); backContent.Children.Add(arrow);
            var text = Label("All Servers", 20); text.Foreground = Brush.Parse("#35EEFF"); text.VerticalAlignment = VerticalAlignment.Center; backContent.Children.Add(text); back.Content = backContent;
            Avalonia.Automation.AutomationProperties.SetName(back, "All Servers");
        }
        var row = new Grid { ColumnDefinitions = new("*,Auto"), Height = 68, Margin = new(0, cluster is null ? 4 : -4, 0, 0) };
        var title = Label(server is not null ? MapName(server.Map) : name, cluster is null ? 49 : 49.5, true); title.FontWeight = FontWeight.Bold;
        title.LetterSpacing = cluster is null ? 1.4 : 1.3; title.Margin = new Thickness(cluster is null ? 2 : 0, cluster is null ? -3 : 1, 0, 0);
        title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis; row.Children.Add(title);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, VerticalAlignment = VerticalAlignment.Center, Margin = cluster is null ? new Thickness(0, 6, 0, 0) : new Thickness(0) };
        var dummy = server is not null && IsReviewServer(server.Id);
        var members = cluster is null ? server is null ? [] : new[] { server } : catalog!.Servers.Where(s => s.ClusterId == cluster.Id).ToArray();
        var target = members.FirstOrDefault();
        var active = dummy ? reviewServerRunning : members.Any(MapProcessActive);
        var available = !dummy && (designPreview || client is not null) && target is not null && !lifecycleBusy && pendingLifecycle is null;
        void PreviewLifecycle(bool start) { foreach (var member in members) { if (start) overviewPreviewRunning.Add(member.Id); else overviewPreviewRunning.Remove(member.Id); } Render(); }
        Action? Start() => dummy ? reviewServerRunning ? null : () => { reviewServerRunning = true; Render(); }
            : available && !members.All(MapProcessActive) ? designPreview ? () => PreviewLifecycle(true) : () => _ = RunLifecycleActionAsync(target!.Id, LifecycleAction.Start, cluster is null) : null;
        Action? Stop() => dummy ? reviewServerRunning ? () => { reviewServerRunning = false; Render(); } : null
            : available && active ? designPreview ? () => PreviewLifecycle(false) : () => _ = RunLifecycleActionAsync(target!.Id, LifecycleAction.Stop, cluster is null) : null;
        actions.Children.Add(OverviewAction(cluster is null ? "▶  Start Server" : "▶  Start All", cluster is null ? "#FFD84B" : "#96FFA5", Start(), cluster is null ? 222 : 180));
        actions.Children.Add(OverviewAction(cluster is null ? "■  Stop" : "■  Stop All", cluster is null ? "#7293B8" : "#FF655D", Stop(), cluster is null ? 132 : 180));
        if (cluster is null)
            actions.Children.Add(OverviewAction("⟳  Restart", "#7293B8", available && active ? designPreview ? () => Render() : () => _ = RunLifecycleActionAsync(server!.Id, LifecycleAction.Restart, true) : null, 158));
        else actions.Children.Add(OverviewAction("＋  Add Map", "#FFDA43", designPreview ? () => { ResetPage(); ResetCreationDraft(); creationPage = true; draftCluster = true; draftEdition = cluster.Edition; Render(); } : client is not null && !clusterCreationBusy ? () => _ = CreateClusterMapAsync(cluster) : null, 186));
        var deletion = OverviewAction("♜  Delete", "#FF625C", server is null ? designPreview ? () => { catalog!.Clusters.RemoveAll(c => c.Id == cluster!.Id); catalog.Servers.RemoveAll(s => s.ClusterId == cluster!.Id); ResetPage(); Render(); } : null : dummy
            ? () => { reviewServerVisible = false; ResetPage(); Render(); }
            : () => _ = ReviewServerDeletionAsync(server), cluster is null ? 114 : 140);
        if (cluster is not null) ToolTip.SetTip(deletion, "Cluster deletion has not been implemented. Your maps and shared data are preserved.");
        else { deletion.Background = Brushes.Transparent; deletion.BorderThickness = new(0); deletion.Padding = new(10); }
        if (dummy) ToolTip.SetTip(actions, "Isolated design fixture. No game process or network ports are created.");
        actions.Children.Add(deletion); Grid.SetColumn(actions, 1); row.Children.Add(actions); content.Children.Add(row);
        bool? compact = null;
        row.SizeChanged += (_, _) =>
        {
            if (row.Bounds.Width <= 0) return; var next = row.Bounds.Width < 1250; if (next == compact) return; compact = next;
            row.RowDefinitions = new(next ? "68,60" : "68"); row.Height = next ? 128 : 68;
            Grid.SetRow(actions, next ? 1 : 0); Grid.SetColumn(actions, next ? 0 : 1); Grid.SetColumnSpan(actions, next ? 2 : 1);
        };
        var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 17, Margin = cluster is null ? new Thickness(3, -2, 0, 0) : new Thickness(1, 0, 0, 0) };
        Border Badge(Control value)
        {
            var badge = new Border { Child = value, Padding = new(20, 7), Height = 41, CornerRadius = new(5), Background = Brush.Parse("#0E2331"), BorderBrush = Brush.Parse("#2B4D64"), BorderThickness = new(1) };
            if (value is StackPanel line && line.Children.LastOrDefault() is TextBlock text && text.Foreground is ISolidColorBrush brush)
            {
                text.FontSize = 20; if (line.Children.FirstOrDefault() is Border dot) { dot.Width = dot.Height = 16; dot.CornerRadius = new(8); }
                var color = brush.Color; badge.Background = new SolidColorBrush(Color.FromArgb(35, color.R, color.G, color.B)); badge.BorderBrush = new SolidColorBrush(Color.FromArgb(115, color.R, color.G, color.B));
            }
            return badge;
        }
        badges.Children.Add(Badge(Label(edition.ToString(), 20)));
        var mode = server is not null ? ServerGameMode(server) : GameMode(settings, cluster?.Defaults?.UserSettings);
        if (mode.Length == 0 && members.FirstOrDefault() is { } first) mode = ServerGameMode(first);
        if (mode.Length > 0) badges.Children.Add(Badge(Label(mode, 20)));
        // Cluster health is already shown on each map. Do not duplicate it as Partial.
        if (server is not null) badges.Children.Add(Badge(dummy ? Status(reviewServerRunning ? "Running" : "Stopped", reviewServerRunning ? "#8DFFA8" : "#FF5549") : designPreview ? Status("Stopped", "#FF5549") : ObservedProcessStatus(serverId: server.Id)));
        content.Children.Add(badges);
        if (lifecycleNotice.Length > 0) content.Children.Add(Label(lifecycleNotice, 15));
    }

    private void ApprovedTabs(string[] labels, int[]? values = null)
    {
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        var clusterTabs = selectedCluster is not null && selectedServer is null;
        var widths = clusterTabs ? new[] { 140d, 164d, 120d, 170d } : new[] { 148d, 148d, 124d, 156d, 148d, 100d };
        for (var i = 0; i < labels.Length; i++)
        {
            var value = values?[i] ?? i;
            var button = new Button { Content = labels[i], FontSize = 20, FontWeight = FontWeight.Normal,
                Width = widths[i], Height = clusterTabs ? 42 : 43, Padding = new Thickness(10, 4), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent, BorderThickness = new(0), Foreground = Brushes.White };
            button.Resources["ButtonBackgroundPointerOver"] = Brushes.Transparent;
            button.Resources["ButtonBackgroundPressed"] = Brushes.Transparent;
            button.Click += (_, _) => { managementTab = value; Render(); };
            Avalonia.Automation.AutomationProperties.SetName(button, labels[i]);
            tabs.Children.Add(new Border { BorderBrush = managementTab == value ? Brush.Parse(clusterTabs ? "#B8EDFF" : "#45F2FF") : Brushes.Transparent,
                BorderThickness = new(0, 0, 0, 3), Child = button });
        }
        content.Children.Add(new Border { ZIndex=1, Margin = clusterTabs ? new Thickness(0, 14, 0, 9) : new Thickness(0, 18, 0, 13), BorderBrush = Brush.Parse("#426680"), BorderThickness = new(0, 0, 0, 1), Child = tabs });
        detailBodyStart = content.Children.Count;
    }

    private Button ClickableOverview(Control body, string name, Action action, Thickness? padding = null)
    {
        var frame = OverviewFrame(body, padding);
        var button = new Button { Content = frame, Padding = new(0), Background = Brushes.Transparent, BorderThickness = new(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        button.Resources["ButtonBackgroundPointerOver"] = Brushes.Transparent;
        button.PointerEntered += (_, _) => frame.BorderBrush = Brush.Parse("#72CBF4");
        button.PointerExited += (_, _) => frame.BorderBrush = Brush.Parse("#2B4D64");
        Avalonia.Automation.AutomationProperties.SetName(button, name); button.Click += (_, _) => action(); return button;
    }

    private string ConnectionAddress(ServerDefinition server)
    {
        if (IsReviewServer(server.Id)) return "192.168.1.50:7777";
        var address = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any)))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a) && !(a.GetAddressBytes()[0] == 169 && a.GetAddressBytes()[1] == 254))?.ToString() ?? "127.0.0.1";
        return address + ":" + server.Settings.GamePort;
    }

    private void ApprovedServerOverview(ServerDefinition server)
    {
        var grid = new Grid { ColumnDefinitions = new("3*,14,2*"), Height = 562 };
        grid.Children.Add(OverviewFrame(ApprovedMapArtwork(server.Map, true), new(9)));
        var side = new Grid { RowDefinitions = new("266,18,278") };
        var facts = new StackPanel { Spacing = 0 };
        var factsTitle = Label("Server Overview", 29, true); factsTitle.LetterSpacing = 0.5; factsTitle.Height = 38; facts.Children.Add(factsTitle);
        facts.Children.Add(new Border { Height = 1, Background = Brush.Parse("#2B4D64"), Margin = new(0, 10, 0, 4) });
        void OverviewFact(string label, string value)
        {
            var row = new Grid { ColumnDefinitions = new("250,*"), Height = 61 };
            row.SizeChanged += (_, _) => row.ColumnDefinitions[0].Width = new GridLength(Math.Min(250, row.Bounds.Width * 0.5));
            var caption = Label(label, 19); caption.LetterSpacing = 0.7; caption.Margin = new(2, 0, 0, 0); caption.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(caption);
            var text = Label(value, 20); text.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(text, 1); row.Children.Add(text);
            facts.Children.Add(new Border { BorderBrush = Brush.Parse("#2B4D64"), BorderThickness = new(0, 0, 0, 1), Child = row });
        }
        OverviewFact("Player limit", server.Settings.MaxPlayers.ToString()); OverviewFact("Selected mods", server.Mods.Count.ToString());
        var addressRow = new Grid { ColumnDefinitions = new("250,*"), Height = 58 };
        addressRow.SizeChanged += (_, _) => addressRow.ColumnDefinitions[0].Width = new GridLength(Math.Min(250, (addressRow.Bounds.Width - 38) * 0.53));
        var captionAddress = Label("Connection address", 19); captionAddress.LetterSpacing = 0.7; captionAddress.Margin = new(2, 0, 0, 0); captionAddress.VerticalAlignment = VerticalAlignment.Center; addressRow.Children.Add(captionAddress);
        var address = ConnectionAddress(server); var addressText = Label(address, 20); addressText.LetterSpacing = 0.7; addressText.TextWrapping = TextWrapping.NoWrap; addressText.TextTrimming = TextTrimming.CharacterEllipsis; addressText.VerticalAlignment = VerticalAlignment.Center; ToolTip.SetTip(addressText, IsReviewServer(server.Id) ? "Illustrative connection address for the UI dummy." : address);
        var addressContents = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, VerticalAlignment = VerticalAlignment.Center }; addressContents.Children.Add(addressText); Grid.SetColumn(addressContents, 1); addressRow.Children.Add(addressContents);
        var copy = new Button { Content = VectorIcon("M8,2 L22,2 L22,18 L18,18 M2,7 L17,7 L17,23 L2,23 Z", Brush.Parse("#32B5FF"), 24), Foreground = Brush.Parse("#32B5FF"), Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(5) };
        Avalonia.Automation.AutomationProperties.SetName(copy, "Copy connection address");
        copy.Click += async (_, e) => { e.Handled = true; if (Clipboard is not null) await Clipboard.SetTextAsync(address); };
        addressContents.Children.Add(copy); facts.Children.Add(addressRow);
        side.Children.Add(ClickableOverview(facts, "Open server settings", () => { managementTab = 1; Render(); }, new(22, 16)));
        var lower = new Grid { ColumnDefinitions = new("0.88*,14,1.12*") };
        Control PanelHeading(string glyph, string text) { var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 }; var icon = (Avalonia.Controls.Shapes.Path)VectorIcon(glyph, Brushes.White, 32); icon.StrokeThickness = 2.8; row.Children.Add(icon); var title = Label(text, 24, true); title.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(title); return row; }
        var backup = new StackPanel { Spacing = 30 }; backup.Children.Add(PanelHeading(DatabaseGlyph, "Backups"));
        backup.Children.Add(Label(IsReviewServer(server.Id) ? "No backup yet" : backupViewServer == server.Id && displayedBackups is { } state
            ? state.History.Count == 0 ? "No backup yet" : state.History.Count + " backups" : "View backups", 20));
        lower.Children.Add(ClickableOverview(backup, "Open server backups", () => { managementTab = 3; Render(); }, new(22, 30)));
        var activity = new StackPanel { Spacing = 36 }; activity.Children.Add(PanelHeading(ClockGlyph, "Recent Activity"));
        if (IsReviewServer(server.Id))
        {
            var timeline = new Grid { ColumnDefinitions = new("24,*"), RowDefinitions = new("55,55") };
            var line = new Border { Width = 3, Height = 94, Background = Brush.Parse("#203D55"), HorizontalAlignment = HorizontalAlignment.Center }; Grid.SetRowSpan(line, 2); timeline.Children.Add(line);
            for (var i = 0; i < 2; i++) { var dot = new Border { Width = 20, Height = 20, CornerRadius = new(10), Background = Brush.Parse(i == 0 ? "#269DFF" : "#FF5549"), VerticalAlignment = VerticalAlignment.Top }; Grid.SetRow(dot, i); timeline.Children.Add(dot); var text = Label(i == 0 ? "Dummy preview created" : reviewServerRunning ? "Dummy is running" : "Server is stopped", 19); text.Margin = new(29, 0, 0, 0); Grid.SetColumn(text, 1); Grid.SetRow(text, i); timeline.Children.Add(text); }
            activity.Children.Add(timeline);
        }
        else
        {
            var entries = history?.Entries.Where(e => e.ServerId == server.Id).OrderByDescending(e => e.Time).Take(2).ToArray() ?? [];
            foreach (var entry in entries) activity.Children.Add(Label(entry.Message, 18));
            if (entries.Length == 0) activity.Children.Add(Label("View recorded activity", 18));
        }
        var activityTile = ClickableOverview(activity, "Open server activity", () => { if (IsReviewServer(server.Id)) { managementTab = 6; Render(); } else { ResetPage(); activityPage = true; Render(); _ = LoadActivityAsync(); } }, new(22, 30));
        Grid.SetColumn(activityTile, 2); lower.Children.Add(activityTile); Grid.SetRow(lower, 2); side.Children.Add(lower);
        Grid.SetColumn(side, 2); grid.Children.Add(side); content.Children.Add(grid);
        bool? stacked = null;
        grid.SizeChanged += (_, _) =>
        {
            if (grid.Bounds.Width <= 0) return; var next = grid.Bounds.Width < 1100; if (next == stacked) return; stacked = next;
            grid.ColumnDefinitions = new(next ? "*,0,0" : "3*,14,2*"); grid.RowDefinitions = new(next ? "562,18,562" : "562");
            grid.Height = next ? 1142 : 562; Grid.SetColumn(side, next ? 0 : 2); Grid.SetRow(side, next ? 2 : 0);
        };
    }

    private void RenderReviewServer()
    {
        if (!designPreview) throw new InvalidOperationException("Design fixtures are unavailable in the installed app.");
        var dummy = ReviewServer(); ApprovedDetailHeader(dummy.Settings.Name, dummy.Edition, dummy.Settings, server: dummy);
        ApprovedTabs(["Overview", "Settings", "Mods", "Backups", "Players", "Console"]);
        if (managementTab == 0) { ApprovedServerOverview(dummy); return; }
        if (managementTab == 1) { if(advancedEditorServer==dummy.Id)RenderReviewConfigurationEditor(dummy);else ApprovedServerSettings(dummy); return; }
        if (managementTab == 2) { RenderServerMods(dummy); return; }
        if (managementTab == 3) { ApprovedBackups(dummy); return; }
        if (managementTab == 4) { ApprovedPlayers(dummy); return; }
        if (managementTab == 5) { ApprovedConsole(dummy); return; }
        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(Label(managementTab == 6 ? "Recent Activity" : new[] { "Overview", "Settings", "Mods", "Backups", "Players", "Console" }[managementTab], 30, true));
        panel.Children.Add(Label("Isolated design fixture · No game process or network ports are created.", 18));
        panel.Children.Add(Label(managementTab switch { 1 => "ASE · PvE · Player limit: 5", 2 => dummy.Mods.Count + " illustrative mod selections", 3 => "No backup yet", 4 => "No players connected", 5 => "The dummy has no game console.", _ => "Dummy preview created. " + (reviewServerRunning ? "Dummy is running." : "Server is stopped.") }, 20));
        content.Children.Add(OverviewFrame(panel));
    }

    private void ApprovedClusterOverview(ClusterDefinition cluster, ServerDefinition[] members)
    {
        var cards = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foreach (var member in members)
        {
            var body = new StackPanel { Spacing = 5 };
            body.Children.Add(new Border { Height = 198, CornerRadius = new(6), ClipToBounds = true, Child = ApprovedMapArtwork(member.Map) });
            var titleRow = new Grid { ColumnDefinitions = new("*,Auto"), Height = 46, Margin = new(4, 0, 4, 0) };
            var name = Label(MapName(member.Map), 25, true); name.VerticalAlignment = VerticalAlignment.Center; name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; titleRow.Children.Add(name);
            var running = MapProcessActive(member);
            var action = OverviewAction(running ? "■  Stop" : "▶  Start", running ? "#FF625C" : "#A2FFAE",
                designPreview ? () => { if (running) overviewPreviewRunning.Remove(member.Id); else overviewPreviewRunning.Add(member.Id); Render(); } : client is not null && !lifecycleBusy && pendingLifecycle is null ? () => _ = RunLifecycleActionAsync(member.Id, running ? LifecycleAction.Stop : LifecycleAction.Start, true) : null, 138);
            action.Height = 46; Grid.SetColumn(action, 1); titleRow.Children.Add(action); body.Children.Add(titleRow);
            var status = designPreview ? Status(running ? "Running" : "Stopped", running ? "#8DFFA8" : "#FF5549") : ObservedProcessStatus(serverId: member.Id);
            if (status is StackPanel statusLine) { if (statusLine.Children.LastOrDefault() is TextBlock statusText) statusText.FontSize = 20; if (statusLine.Children.FirstOrDefault() is Border dot) { dot.Width = dot.Height = 20; dot.CornerRadius = new(10); } }
            status.Margin = new(8, 0, 4, 0);
            body.Children.Add(status);
            var players = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 11, Margin = new(8, 0, 4, 0) }; players.Children.Add(VectorIcon(PlayersGlyph, Brushes.White, 24)); players.Children.Add(Label(designPreview && running ? "2 / " + member.Settings.MaxPlayers + " players" : playerObservations.TryGetValue(member.Id, out var population) && population.Result is { } observed ? observed.Players.Count + " / " + member.Settings.MaxPlayers + " players" : "Player limit: " + member.Settings.MaxPlayers, 19)); body.Children.Add(players);
            var frame = OverviewFrame(body, new(10));
            var card = new Button { Width = 440, Height = 338, Content = frame, Padding = new(0), BorderThickness = new(0), Background = Brushes.Transparent,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
            Avalonia.Automation.AutomationProperties.SetName(card, "Open " + MapName(member.Map));
            card.Click += (_, e) => { if (e.Source is Button source && source != card) return; selectedServer = member.Id; managementTab = 0; Render(); };
            frame.PointerEntered += (_, _) => frame.BorderBrush = Brush.Parse("#72CBF4"); frame.PointerExited += (_, _) => frame.BorderBrush = Brush.Parse("#2B4D64"); cards.Children.Add(card);
        }
        var rail = CreateCardRail(cards);
        var heading = new Grid { ColumnDefinitions = new("*,Auto"), Height = 44, Margin = new(0, 0, 0, 8) };
        var headingText = Label("Maps in this cluster", 32, true); headingText.VerticalAlignment = VerticalAlignment.Center; heading.Children.Add(headingText);
        var arrows = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        var previous = OverviewAction("‹", "#A6DEF9", () => SmoothScrollTo(rail, rail.Offset.X - 452), 52);
        var next = OverviewAction("›", "#A6DEF9", () => SmoothScrollTo(rail, rail.Offset.X + 452), 52); previous.Height = next.Height = 44;
        void UpdateArrows() { previous.IsEnabled = rail.Offset.X > 0.5; next.IsEnabled = rail.Offset.X + rail.Viewport.Width < rail.Extent.Width - 0.5; }
        rail.ScrollChanged += (_, _) => UpdateArrows(); rail.SizeChanged += (_, _) => UpdateArrows();
        arrows.Children.Add(previous); arrows.Children.Add(next); Grid.SetColumn(arrows, 1); heading.Children.Add(arrows); content.Children.Add(heading); content.Children.Add(rail);

    }

    private void ApprovedClusterDetails(ClusterDefinition cluster)
    {
        var members = catalog!.Servers.Where(s => s.ClusterId == cluster.Id).ToArray();
        if (managementTab == 3 && clusterBackupScope == cluster.Id && clusterRestorePreviewHour is not null) { RenderClusterRestorePreview(cluster, members); return; }
        ApprovedDetailHeader(cluster.Name, cluster.Edition, cluster.Settings, cluster: cluster);
        ApprovedTabs(["Overview", "Transfers", "Backups", "Activity"], [0, 1, 3, 6]);
        if (managementTab == 0) { ApprovedClusterOverview(cluster, members); return; }
        if (managementTab == 3) { if(reviewSharedClusterHistory||reviewCoordinatedHistory||clusterRecoveryCluster==cluster.Id||sharedRecoveryCluster==cluster.Id)RenderClusterBackups(cluster,members);else ApprovedClusterBackups(cluster, members); return; }
        if (managementTab == 1) { ApprovedClusterTransfers(cluster,members); return; }
        if (managementTab == 6) { ApprovedClusterActivity(cluster,members); return; }
        managementTab = 0; ApprovedClusterOverview(cluster, members);
    }
}
