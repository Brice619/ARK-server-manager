using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private int managementTab;
    private static string MapName(string map) => ServerMapCatalog.DisplayName(map);
    private static string? MapArt(string map) => map.Contains("fjord", StringComparison.OrdinalIgnoreCase) ? "fjord"
        : map.Contains("aberration", StringComparison.OrdinalIgnoreCase) ? "cavern-v1"
        : map.Contains("extinction", StringComparison.OrdinalIgnoreCase) || map.Contains("genesis", StringComparison.OrdinalIgnoreCase) || map.Equals("Gen2", StringComparison.OrdinalIgnoreCase) ? "ruined-city-v1"
        : map.Contains("valguero", StringComparison.OrdinalIgnoreCase) || map.Contains("colony", StringComparison.OrdinalIgnoreCase) ? "fjord"
        : map.Contains("center", StringComparison.OrdinalIgnoreCase) || map.Contains("crystal", StringComparison.OrdinalIgnoreCase) || map.Contains("astraeos", StringComparison.OrdinalIgnoreCase) ? "island"
        : map.Contains("ragnarok", StringComparison.OrdinalIgnoreCase) || map.Contains("scorched", StringComparison.OrdinalIgnoreCase) ? "canyon"
        : map.Contains("island", StringComparison.OrdinalIgnoreCase) ? "island" : null;

    internal static Border Surface(Control child, Thickness? padding = null) => new()
    {
        Background = Brush.Parse("#17252F"), BorderBrush = Brush.Parse("#2B3A46"),
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7),
        Padding = padding ?? new Thickness(24), Child = child
    };

    internal static Button ActionButton(string title, Action? action = null, bool primary = false)
    {
        var button = new Button { Content = title, FontSize = 15, Padding = new Thickness(18, 12),
            CornerRadius = new CornerRadius(5), BorderBrush = Brush.Parse("#405363"), BorderThickness = new Thickness(1),
            Background = primary ? Amber : Brush.Parse("#1B2934"), Foreground = primary ? Brush.Parse("#181B1F") : Brushes.White };
        button.IsEnabled = action is not null;
        if (primary) button.Classes.Add("accent");
        if (action is not null) button.Click += (_, _) => action();
        return button;
    }

    internal static Control MapPicture(string map, double width, double height)
    {
        var key = MapArt(map);
        return new Border { Width = width, Height = height, CornerRadius = new CornerRadius(5), ClipToBounds = true,
            Background = Brush.Parse("#101C26"),
            Child = key is not null ? new Image { Source = LoadArtwork(key), Stretch = Stretch.UniformToFill }
                : VectorIcon(BoxIcon, Muted, 48) };
    }

    private void DetailTabs(string[] titles)
    {
        var tabs = new WrapPanel { Margin = new Thickness(0, 18, 0, 14) };
        for (var i = 0; i < titles.Length; i++)
        {
            var index = i;
            var button = new Button { Content = titles[i], FontSize = 16, Padding = new Thickness(22, 14),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Foreground = managementTab == i ? Amber : Muted };
            button.Click += (_, _) => { managementTab = index; Render(); };
            Avalonia.Automation.AutomationProperties.SetName(button, titles[i]);
            tabs.Children.Add(new Border { BorderBrush = managementTab == i ? Amber : Brushes.Transparent,
                BorderThickness = new Thickness(0, 0, 0, 3), Child = button });
        }
        content.Children.Add(new Border { BorderBrush = Brush.Parse("#2B3A46"), BorderThickness = new Thickness(0, 0, 0, 1), Child = tabs });
    }

    private void DetailHeader(string name, ArkEdition edition, string map, bool cluster, int maps = 0, ServerDefinition? server = null)
    {
        AddBack();
        var compact = Height < 760;
        var header = new WrapPanel { Margin = new Thickness(0, compact ? 8 : 20, 0, 0) };
        Control artwork = cluster
            ? Surface(VectorIcon(ClusterIcon, edition == ArkEdition.ASA ? Brush.Parse("#65B6FF") : Amber, 64), new Thickness(20))
            : MapPicture(map, 240, 166);
        artwork.Margin = new Thickness(0, 0, 24, 12); if (!compact) header.Children.Add(artwork);
        var information = new StackPanel { Spacing = compact ? 4 : 12, MinWidth = 300, Margin = new Thickness(0, compact ? 0 : 12, 24, 12) };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        title.Children.Add(Label(name, compact ? 24 : 32, true));
        var badge = Label(edition.ToString(), 15); badge.Foreground = edition == ArkEdition.ASA ? Brush.Parse("#75BCF6") : Amber;
        title.Children.Add(new Border { BorderBrush = badge.Foreground, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 5), VerticalAlignment = VerticalAlignment.Center, Child = badge });
        information.Children.Add(title);
        information.Children.Add(designPreview ? Status(cluster && name == "Modded Event Cluster" ? "Partial" : name == "Legacy PvE" ? "Stopped" : "Running",
            name == "Legacy PvE" ? "#FF6062" : cluster && name == "Modded Event Cluster" ? "#FFBC43" : "#64E581")
            : ObservedProcessStatus(serverId: server?.Id, clusterId: cluster ? selectedCluster : null));
        var description = Label(cluster ? maps + " maps" : MapName(map), 17); description.Foreground = Muted;
        information.Children.Add(description); header.Children.Add(information);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, compact ? 12 : 42, 0, 12) };
        var actionTarget = server?.Id ?? (cluster ? catalog?.Servers.FirstOrDefault(s => s.ClusterId == selectedCluster)?.Id : null);
        foreach (var (label, action) in new[] { ("▶  Start", LifecycleAction.Start), ("■  Stop", LifecycleAction.Stop), ("↻  Restart", LifecycleAction.Restart) })
            actions.Children.Add(ActionButton(label, designPreview || client is null || lifecycleBusy || pendingLifecycle is not null || actionTarget is null
                ? null : () => { _ = RunLifecycleActionAsync(actionTarget.Value, action, memberOnly: !cluster); }));
        if (pendingLifecycle is not null && !lifecycleBusy)
        {
            actions.Children.Add(ActionButton("Check action", () => { _ = RunLifecycleActionAsync(pendingLifecycle.ServerId, pendingLifecycle.Action, pendingLifecycleMemberOnly); }));
            actions.Children.Add(ActionButton("Dismiss unknown result", () =>
            {
                pendingLifecycle = null;
                lifecycleNotice = "Previous action result dismissed. It may have run; refresh status before another action. Nothing was resent.";
                Render();
            }));
        }
        var more = ActionButton("•••  More actions", () => { });
        var menu = new ContextMenu();
        var membership = new MenuItem { Header = pendingMembership is null ? "Add to a cluster…" : "Check cluster change", IsEnabled = server is not null && client is not null && !designPreview };
        membership.Click += (_, _) => { if (server is not null) _ = EditClusterMembershipAsync(server); };
        if (server is not null && (pendingMembership?.Expected.Any(member => member.ServerId == server.Id) == true
            || server.ClusterId is null && !joiningRecovery.Any(r => r.ServerIds.Contains(server.Id)))) menu.Items.Add(membership);
        if (server is not null && joiningRecovery.Any(r => r.ServerIds.Contains(server.Id)))
        {
            var finishJoining = new MenuItem { Header = "Finish cluster joining…", IsEnabled = !joiningRecoveryBusy };
            finishJoining.Click += (_, _) => _ = FinishJoiningAsync(server.Id);
            menu.Items.Add(finishJoining);
        }
        var repair = new MenuItem { Header = pendingRepair is null ? "Check files…" : "Check file-repair result", IsEnabled = server is not null && client is not null && !designPreview && !repairBusy };
        repair.Click += (_, _) => { if (server is not null) _ = RepairServerFilesAsync(server); }; menu.Items.Add(repair);
        var preset = new MenuItem { Header = "Apply preset…", IsEnabled = server is not null && client is not null && !designPreview && !applicationPresetBusy };
        preset.Click += (_, _) => { if (server is not null) BeginPresetApplication(server); }; menu.Items.Add(preset);
        var update = new MenuItem { Header = "Update server", IsEnabled = server is not null && !designPreview && client is not null && !updateCheckBusy };
        update.Click += (_, _) => { if (server is not null) BeginServerUpdateCheck(server); };
        menu.Items.Add(update);
        var deletion = new MenuItem { Header = "Delete server", IsEnabled = server is not null };
        deletion.Click += (_, _) => { if (server is not null) _ = ReviewServerDeletionAsync(server); };
        menu.Items.Add(deletion);
        more.ContextMenu = menu; more.Click += (_, _) => menu.Open(more); actions.Children.Add(more);
        header.Children.Add(actions); content.Children.Add(header);
        if (lifecycleNotice.Length > 0)
        {
            var notice = Label(lifecycleNotice, 14); notice.TextWrapping = TextWrapping.Wrap; notice.Foreground = Muted;
            content.Children.Add(notice);
        }
    }

    private void RenderServerDetails(ServerDefinition server)
    {
        if (updateViewServer == server.Id) { RenderServerUpdate(server); return; }
        if (scheduleServer == server.Id) { RenderRestartSchedule(server); return; }
        if (presetApplicationServer == server.Id) { RenderPresetApplication(server); return; }
        if (managementTab == 1 && advancedEditorServer == server.Id)
        { RenderAdvancedEditor(server); return; }
        if (managementTab == 2 && modPageServer == server.Id && configuredMod is { } configurationMod)
        { RenderModConfiguration(server, configurationMod); return; }
        if (managementTab == 3 && backupViewServer == server.Id && selectedRestorePoint is not null)
        { RenderServerBackups(server); return; }
        if (managementTab is 1 or 4 or 5 && Height < 760)
        {
            content.Children.Add(ActionButton("← Back to dashboard", () => { selectedServer = null; Render(); }));
            content.Children.Add(Label(server.Settings.Name + " · " + server.Edition + " · " + MapName(server.Map), 23, true));
        }
        else DetailHeader(server.Settings.Name, server.Edition, server.Map, false, server: server);
        DetailTabs(["Overview", "Settings", "Mods", "Backups", "Players", "Console"]);
        if (managementTab == 0) { ServerOverview(server); return; }
        if (managementTab == 1) { ReadOnlyServerSettings(server); return; }
        if (managementTab == 2) { RenderServerMods(server); return; }
        if (managementTab == 3) { RenderServerBackups(server); return; }
        if (managementTab == 4) { RenderPlayers(server); return; }
        if (managementTab == 5) { RenderConsole(server); return; }
        var title = new[] { "Overview", "Settings", "Mods", "Backups", "Players", "Console" }[managementTab];
        content.Children.Add(Label(title, 28, true));
        content.Children.Add(Surface(Label("This view is still being connected to the background manager.", 16)));
    }

    private static void Fact(StackPanel body, string name, string value)
    {
        var line = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 10) };
        var caption = Label(name, 14); caption.Foreground = Muted; line.Children.Add(caption);
        line.Children.Add(Label(value, 17)); body.Children.Add(line);
    }

    private void ServerOverview(ServerDefinition server)
    {
        var panels = new WrapPanel();
        var world = new StackPanel { Spacing = 14 }; world.Children.Add(Label("World", 23, true));
        var worldRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 22 };
        worldRow.Children.Add(MapPicture(server.Map, 170, 166));
        var facts = new StackPanel(); Fact(facts, "Map", MapName(server.Map));
        Fact(facts, "Session name", server.Settings.Name); Fact(facts, "Game edition", server.Edition.ToString());
        worldRow.Children.Add(facts); world.Children.Add(worldRow);
        var worldPanel = Surface(world); worldPanel.Width = 642; worldPanel.MinHeight = 278; worldPanel.Margin = new Thickness(0, 0, 22, 16); panels.Children.Add(worldPanel);
        var connectionFacts = new StackPanel { Spacing = 14 }; connectionFacts.Children.Add(Label("Connection", 23, true));
        Fact(connectionFacts, "This computer", "127.0.0.1:" + server.Settings.GamePort);
        var runtime = managedRuntimeStatus.SingleOrDefault(status => status.ServerId == server.Id);
        Fact(connectionFacts, "Server status", runtime is null ? "Refresh to check" : runtime.State.ToString());
        Fact(connectionFacts, "Join password", accessEditors.TryGetValue(server.Id, out var access) && access.Review is { } reviewed
            ? reviewed.JoinPasswordConfigured ? "Configured" : "None" : "Review in Access settings");
        connectionFacts.Children.Add(ActionButton("Access settings  ›", () => { managementTab = 1; settingsCategory = 2; Render(); }));
        Fact(connectionFacts, "Hosting", "Local server (this computer)");
        var connectionPanel = Surface(connectionFacts); connectionPanel.Width = 642; connectionPanel.MinHeight = 278;
        connectionPanel.Margin = new Thickness(0, 0, 22, 16); panels.Children.Add(connectionPanel); content.Children.Add(panels);
        var activity = new StackPanel { Spacing = 16 }; activity.Children.Add(Label("Recent activity", 23, true));
        activity.Children.Add(Label(designPreview ? "Design preview · No live server events" : "Open activity to see recorded events.", 16));
        activity.Children.Add(ActionButton("View activity  ›", () => { ResetPage(); activityPage = true; Render(); }));
        content.Children.Add(Surface(activity));
        var note = Label("Use the configured game port to connect from this computer. Network access is managed in server settings.", 13);
        note.Foreground = Muted; content.Children.Add(note);
    }

    private int settingsCategory;
    private void ReadOnlyServerSettings(ServerDefinition server)
    {
        var heading = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        if (Height >= 760) heading.Children.Add(Label("Server Settings", 28, true));
        var scope = Label(server.ClusterId is null ? "Settings for this server." : "Settings for this map. Cluster-wide settings are managed in the cluster.", 16);
        scope.Foreground = Muted;
        var advanced = ActionButton("Advanced Editor  ›", () =>
        { advancedReturnMod = null; advancedEditorServer = server.Id; advancedEditorFile = 0; Render(); });
        if (Height >= 760) { heading.Children.Add(advanced); content.Children.Add(heading); }
        var categories = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        var names = new[] { "General", "Network", "Access", "Updates" };
        for (var i = 0; i < names.Length; i++)
        {
            var index = i;
            categories.Children.Add(ActionButton(names[i], () => { settingsCategory = index; Render(); }, settingsCategory == i));
        }
        if (Height < 760) categories.Children.Add(advanced);
        if (server.ClusterId is not null) categories.Children.Add(ActionButton("Map overrides…", designPreview || clusterOverrideBusy ? null : () => _ = EditClusterOverridesAsync(server)));
        content.Children.Add(categories);
        var body = new StackPanel { Spacing = 12 };
        if (Height >= 760) body.Children.Add(Label(names[settingsCategory] + " Settings", 23, true));
        switch (settingsCategory)
        {
            case 0:
                AddBasicSettings(body, server, false);
                ReadOnlyField(body, "Server folder", designPreview ? "Illustrative preview" : server.Directory);
                AddWelcomeSettings(body, server);
                body.Children.Add(ActionButton("Restart schedule  ›", () => { scheduleServer = server.Id; Render(); }));
                break;
            case 1:
                AddBasicSettings(body, server, true);
                if (server.Edition == ArkEdition.ASE) body.Children.Add(ActionButton("Player client access…", client is null || aseClientBusy ? null : () => _ = EditAseClientsAsync(server)));
                else body.Children.Add(ActionButton("Player platforms…", client is null || asaClientBusy ? null : () => _ = EditAsaClientsAsync(server)));
                AddLocalNetworkConnections(body, server);
                break;
            case 2:
                AddAccessSettings(body, server);
                break;
            case 3:
                Fact(body, "Server updates", "Running servers ask before stopping to update.");
                Fact(body, "Queued updates", "Apply when the server stops.");
                break;
        }
        content.Children.Add(Surface(body));
        var note = Label("Basic and access settings ask when to apply changes. Welcome settings require this map stopped.", 13);
        note.Foreground = Muted; content.Children.Add(note);
    }

    private static void ReadOnlyField(StackPanel body, string title, string value)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*"), MaxWidth = 880, HorizontalAlignment = HorizontalAlignment.Left };
        var label = Label(title, 16); label.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(label);
        var field = new TextBox { Text = value, IsReadOnly = true, MinWidth = 320, FontSize = 16,
            Background = Brush.Parse("#101C26"), Padding = new Thickness(12, 10) };
        Avalonia.Automation.AutomationProperties.SetName(field, title);
        Grid.SetColumn(field, 1); row.Children.Add(field); body.Children.Add(row);
    }

    private void RenderClusterDetails(ClusterDefinition cluster)
    {
        var members = catalog!.Servers.Where(s => s.ClusterId == cluster.Id).ToArray();
        if (managementTab == 3 && clusterBackupScope == cluster.Id && clusterRestorePreviewHour is not null)
        { RenderClusterRestorePreview(cluster, members); return; }
        if (managementTab == 1 && Height < 760)
        {
            content.Children.Add(ActionButton("← Back to dashboard", () => { selectedCluster = null; Render(); }));
            content.Children.Add(Label(cluster.Name + " · " + cluster.Edition, 23, true));
        }
        else DetailHeader(cluster.Name, cluster.Edition, "", true, members.Length);
        DetailTabs(["Maps", "Cluster Settings", "Mods", "Backups", "Players", "Console"]);
        if (managementTab == 0)
        {
            content.Children.Add(SectionHeading("Your maps", members.Length + " maps", 0));
            AddServers(members); return;
        }
        if (managementTab == 3) { RenderClusterBackups(cluster, members); return; }
        if (managementTab is 2 or 4 or 5) { RenderClusterMapTool(cluster, members); return; }
        if (Height >= 760) content.Children.Add(Label(new[] { "Maps", "Cluster Settings", "Mods", "Backups", "Players", "Console" }[managementTab], 28, true));
        if (managementTab == 1)
        {
            RenderClusterDefaults(cluster);
        }
        else content.Children.Add(Surface(Label("This cluster view is still being connected.", 16)));
    }
}

