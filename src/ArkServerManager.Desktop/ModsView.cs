using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private Guid? modPageServer;
    private bool modBrowser;
    private string? configuredMod;
    private string modSearch = "", lookupId = "", lookupMessage = "";
    private ModMetadata? lookedUpMod;
    private Bitmap? modPreview;
    private readonly Dictionary<(ArkEdition Edition, string Id), ModMetadata> modDetails = [];
    private readonly Dictionary<(ArkEdition Edition, string Id), DateTimeOffset> modDetailsAttempts = [];
    private readonly HashSet<(ArkEdition Edition, string Id)> modDetailsLoading = [];
    private readonly SemaphoreSlim modDetailsGate = new(1, 1);
    private bool lookupBusy;
    private readonly Dictionary<Guid, List<string>> modOrderDrafts = new();
    private bool modOrderConfirmation;
    private readonly HashSet<Guid> replacingModSelections = [];

    private void RenderServerMods(ServerDefinition server)
    {
        if (modPageServer != server.Id)
        {
            modPageServer = server.Id; modBrowser = false; modSearch = lookupId = lookupMessage = "";
            lookedUpMod = null; configuredMod = null; modOrderConfirmation = false;
            modPreview?.Dispose(); modPreview = null;
            if (!designPreview && client is not null) _ = LoadQueuedModOrderAsync(server.Id);
        }
        if (modOrderConfirmation) { RenderModOrderConfirmation(server); return; }
        if (configuredMod is { } modId) { RenderModConfiguration(server, modId); return; }
        if (modBrowser) { RenderModBrowser(server); return; }
        content.Children.Add(Label("Mods", 32, true));
        var subtitle = Label(designPreview ? "Design preview · Illustrative mod IDs for reviewing this screen."
            : "Manage the mods recorded for this server.", 17); subtitle.Foreground = Muted;
        content.Children.Add(subtitle);
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 20, 18, 12) };
        toolbar.Children.Add(Label("Load order", 24, true));
        var add = ActionButton("+  Add mods", () => { modBrowser = true; Render(); }, true);
        Grid.SetColumn(add, 1); toolbar.Children.Add(add); content.Children.Add(toolbar);
        var search = new TextBox { Text = modSearch, PlaceholderText = "Find a mod name or ID", MaxWidth = 520,
            HorizontalAlignment = HorizontalAlignment.Left, FontSize = 16, Padding = new Thickness(12, 10) };
        Avalonia.Automation.AutomationProperties.SetName(search, "Find recorded mods");
        var rows = new StackPanel { Spacing = 12 };
        void Populate()
        {
            rows.Children.Clear();
            var queued = queuedModOrders.GetValueOrDefault(server.Id);
            var ordered = modOrderDrafts.GetValueOrDefault(server.Id, queued ?? server.Mods);
            if (!replacingModSelections.Contains(server.Id) && (!ordered.ToHashSet().SetEquals(server.Mods) || ordered.Count != server.Mods.Count))
            { modOrderDrafts.Remove(server.Id); ordered = server.Mods; }
            for (var i = 0; i < ordered.Count; i++)
            {
                var index = i;
                var id = ordered[i];
                var key = (server.Edition, id);
                var metadata = modDetails.GetValueOrDefault(key);
                if (!designPreview && metadata is null && !modDetailsLoading.Contains(key)
                    && DateTimeOffset.UtcNow - modDetailsAttempts.GetValueOrDefault(key) > TimeSpan.FromMinutes(2))
                    _ = LoadRecordedModDetailsAsync(server.Edition, id);
                if (!id.Contains(modSearch, StringComparison.OrdinalIgnoreCase)
                    && !(metadata?.Name.Contains(modSearch, StringComparison.OrdinalIgnoreCase) ?? false)) continue;
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("44,96,*,Auto"), MinHeight = 78 };
                var order = Label((i + 1).ToString(), 16); order.Foreground = Muted; order.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(order);
                var art = new Border { Width = 76, Height = 68, CornerRadius = new CornerRadius(5),
                    Background = Brush.Parse("#101C26"), Child = VectorIcon(BoxIcon, Muted, 30) };
                Grid.SetColumn(art, 1); row.Children.Add(art);
                var info = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
                info.Children.Add(Label(metadata?.Name ?? "Mod ID: " + id, 19, true));
                var detail = Label(metadata is not null ? "ID: " + id : modDetailsLoading.Contains(key) ? "Finding mod details…"
                    : "Details unavailable · ID remains selected", 14); detail.Foreground = Muted;
                info.Children.Add(detail); Grid.SetColumn(info, 2); row.Children.Add(info);
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
                var configure = ActionButton("Configure", () => { configuredMod = id; modConfigurationText = false; Render(); });
                Avalonia.Automation.AutomationProperties.SetName(configure, "Configure mod " + id);
                actions.Children.Add(configure);
                void Move(int offset)
                {
                    var draft = ordered.ToList();
                    (draft[index], draft[index + offset]) = (draft[index + offset], draft[index]);
                    if (draft.SequenceEqual(server.Mods)) modOrderDrafts.Remove(server.Id);
                    else modOrderDrafts[server.Id] = draft;
                    Render();
                }
                var details = new MenuItem { Header = "View details" };
                details.Click += (_, _) =>
                {
                    lookupId = id; modBrowser = true; lookedUpMod = metadata; lookupMessage = "";
                    modPreview?.Dispose(); modPreview = null; Render();
                    if (metadata is null) _ = LookupModAsync(server);
                    else if (metadata.PreviewImage is { } picture) _ = LoadModPreviewAsync(metadata, picture);
                };
                var up = new MenuItem { Header = "Move earlier", IsEnabled = index > 0 };
                up.Click += (_, _) => Move(-1);
                var down = new MenuItem { Header = "Move later", IsEnabled = index < ordered.Count - 1 };
                down.Click += (_, _) => Move(1);
                var menu = new ContextMenu { ItemsSource = new object[] { details, new Separator(), up, down } };
                if ((server.Edition == ArkEdition.ASE || OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) && !designPreview && client is not null)
                {
                    var remove = new MenuItem { Header = "Remove mod" };
                    remove.Click += (_, _) =>
                    {
                        modOrderDrafts[server.Id] = ordered.Where(value => value != id).ToList();
                        replacingModSelections.Add(server.Id); modOrderConfirmation = true; Render();
                    };
                    menu.Items.Add(new Separator()); menu.Items.Add(remove);
                }
                var more = ActionButton("•••  ▾");
                more.IsEnabled = true;
                more.ContextMenu = menu;
                Avalonia.Automation.AutomationProperties.SetName(more, "More actions for mod " + id);
                more.Click += (_, _) => menu.Open(more);
                actions.Children.Add(more);
                Grid.SetColumn(actions, 3); row.Children.Add(actions);
                rows.Children.Add(Surface(row, new Thickness(18, 12)));
            }
            if (rows.Children.Count == 0)
            {
                var empty = new StackPanel { Spacing = 12 };
                empty.Children.Add(Label(server.Mods.Count == 0 ? "No mods recorded" : "No matching mod IDs", 24, true));
                empty.Children.Add(Label(server.Mods.Count == 0 ? "Choose Add mods to inspect a mod before adding it." : "Try another ID.", 16));
                rows.Children.Add(Surface(empty, new Thickness(30)));
            }
        }
        search.TextChanged += (_, _) => { modSearch = search.Text ?? ""; Populate(); };
        content.Children.Add(search); Populate(); content.Children.Add(rows);
        if (modOrderDrafts.ContainsKey(server.Id))
        {
            var draftActions = new WrapPanel();
            draftActions.Children.Add(ActionButton("Revert changes", () =>
            { modOrderDrafts.Remove(server.Id); if (!queuedModReplacements.Contains(server.Id)) replacingModSelections.Remove(server.Id); Render(); }));
            draftActions.Children.Add(ActionButton(replacingModSelections.Contains(server.Id) ? "Save changes" : "Save order", designPreview || client is not null ? () => { modOrderConfirmation = true; Render(); } : null, true));
            content.Children.Add(draftActions);
            content.Children.Add(Label("Unsaved mod changes · Server files and the recorded list are unchanged.", 14));
        }
        if (queuedModOrders.ContainsKey(server.Id))
        {
            content.Children.Add(Label("Saved changes · Applied or retried at the next startup or restart.", 15));
            content.Children.Add(ActionButton("Refresh saved changes", () => { _ = LoadQueuedModOrderAsync(server.Id); }));
        }
        if (queuedModOrderNotices.TryGetValue(server.Id, out var savedNotice)) content.Children.Add(Label(savedNotice, 15));
        var note = Label("New mods download during startup. Removed mod files are deleted when the change is applied.", 13);
        note.Foreground = Muted; note.Margin = new Thickness(0, 20); content.Children.Add(note);
    }

    private void RenderModOrderConfirmation(ServerDefinition server)
    {
        if (!designPreview || reviewLiveModOrder) { RenderLiveModOrderConfirmation(server); return; }
        content.Children.Add(Label("Save mod load order?", 30, true));
        content.Children.Add(Label(server.Settings.Name + " · " + server.Edition, 17));
        var review = new StackPanel { Spacing = 12 };
        var order = modOrderDrafts.GetValueOrDefault(server.Id, server.Mods);
        for (var i = 0; i < order.Count; i++) review.Children.Add(Label($"{i + 1}.  {order[i]}", 17));
        content.Children.Add(Surface(review));
        content.Children.Add(Label("Design preview · Confirmation changes only the illustrative catalog in this session.", 15));
        var actions = new WrapPanel();
        actions.Children.Add(ActionButton("Cancel", () => { modOrderConfirmation = false; Render(); }));
        actions.Children.Add(ActionButton("Yes, save order", designPreview ? () =>
        {
            if (catalog is not null)
            {
                var index = catalog.Servers.FindIndex(s => s.Id == server.Id);
                if (index >= 0 && order.Count == server.Mods.Count && order.ToHashSet().SetEquals(server.Mods))
                    catalog.Servers[index] = server with { Mods = order.ToList() };
            }
            modOrderDrafts.Remove(server.Id); modOrderConfirmation = false; Render();
        } : null, true));
        content.Children.Add(actions);
    }

    private void RenderModBrowser(ServerDefinition server)
    {
        content.Children.Add(ActionButton("←  Back to mods", () => { modBrowser = false; Render(); }));
        content.Children.Add(Label("Find a mod", 32, true));
        var subtitle = Label(server.Edition == ArkEdition.ASE ? "Look up an ARK: Survival Evolved Steam Workshop ID."
            : "Add an ASA CurseForge project ID. ASA downloads it during startup.", 17); subtitle.Foreground = Muted;
        content.Children.Add(subtitle);
        var body = new StackPanel { Spacing = 22 };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MaxWidth = 840,
            HorizontalAlignment = HorizontalAlignment.Left };
        var id = new TextBox { Text = lookupId, PlaceholderText = "Enter mod ID", MinWidth = 460,
            FontSize = 17, Padding = new Thickness(12, 10) };
        Avalonia.Automation.AutomationProperties.SetName(id, "Mod ID");
        id.TextChanged += (_, _) => lookupId = id.Text ?? "";
        row.Children.Add(id);
        var lookup = ActionButton(server.Edition == ArkEdition.ASA ? "Add project ID" : lookupBusy ? "Looking up…" : "Look up",
            lookupBusy || designPreview || server.Edition == ArkEdition.ASA && client is null ? null : server.Edition == ArkEdition.ASA ? () =>
            {
                try
                {
                    var selected = modOrderDrafts.GetValueOrDefault(server.Id, queuedModOrders.GetValueOrDefault(server.Id) ?? server.Mods).ToList();
                    selected.Add(lookupId.Trim()); AsaModLaunchArguments.Build(selected);
                    modOrderDrafts[server.Id] = selected; replacingModSelections.Add(server.Id);
                    modBrowser = false; modOrderConfirmation = true; Render();
                }
                catch (ArgumentException) { lookupMessage = "Enter a positive project ID that is not already selected."; Render(); }
            } : () => { _ = LookupModAsync(server); }, true);
        lookup.Margin = new Thickness(14, 0, 0, 0); Grid.SetColumn(lookup, 1); row.Children.Add(lookup); body.Children.Add(row);
        if (server.Edition == ArkEdition.ASA)
            body.Children.Add(ActionButton(lookupBusy ? "Finding details…" : "Find details by ID",
                lookupBusy || designPreview ? null : () => { _ = LookupModAsync(server); }));
        if (lookupMessage.Length > 0)
        {
            var message = Label(lookupMessage, 16); message.Foreground = Amber; body.Children.Add(message);
        }
        if (lookedUpMod is { } mod)
        {
            if (modPreview is not null)
                body.Children.Add(new Image { Source = modPreview, Width = 128, Height = 128,
                    Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left });
            body.Children.Add(Label(mod.Name, 27, true));
            Fact(body, "Mod ID", mod.Id); Fact(body, "Game edition", mod.Edition.ToString());
            var description = Label(mod.Description.Length > 2000 ? mod.Description[..2000] + "…" : mod.Description, 16);
            body.Children.Add(description);
            body.Children.Add(new Expander { Header = "More details", Content = Label("Updated: " + mod.UpdatedAt.ToLocalTime().ToString("g")
                + "\n" + string.Join(", ", mod.Tags), 15) });
            body.Children.Add(ActionButton("+  Add to server", designPreview || client is null ? null : () =>
            {
                var selected = modOrderDrafts.GetValueOrDefault(server.Id, queuedModOrders.GetValueOrDefault(server.Id) ?? server.Mods).ToList();
                if (selected.Any(value => ulong.TryParse(value, out var existing) && existing == ulong.Parse(mod.Id)))
                { lookupMessage = "This mod is already selected."; Render(); return; }
                selected.Add(mod.Id); modOrderDrafts[server.Id] = selected;
                replacingModSelections.Add(server.Id); modBrowser = false; modOrderConfirmation = true; Render();
            }));
            body.Children.Add(Label("Review and confirm before saving. The mod downloads during server startup.", 14));
        }
        else body.Children.Add(Label(server.Edition == ArkEdition.ASA ? "Find public details by project ID, or add the ID directly. Details provided by CFWidget."
            : designPreview ? "Design preview · Online lookup is disabled." : "Lookup reads metadata only. Review the result before adding a mod.", 15));
        content.Children.Add(Surface(body, new Thickness(30)));
    }

    private async Task LookupModAsync(ServerDefinition server)
    {
        if (lookupBusy) return;
        if (designPreview) { lookupMessage = "Online lookup is disabled in the design preview."; Render(); return; }
        var requestedId = lookupId.Trim();
        modPreview?.Dispose(); modPreview = null;
        lookupBusy = true; lookedUpMod = null; lookupMessage = ""; Render();
        ModMetadata? result = null;
        string message = "";
        try
        {
            using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
            result = server.Edition == ArkEdition.ASA
                ? await new PublicAsaModMetadata(http).GetAsync(requestedId, lifetime.Token)
                : await new SteamWorkshopMetadata(http).GetAsync(requestedId, lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or ArgumentException
            or ModLookupException or System.Text.Json.JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
        {
            message = error is ModLookupException or ArgumentException ? error.Message : "Could not retrieve mod details. Check the ID and connection, then try again.";
        }
        finally { lookupBusy = false; }
        if (lifetime.IsCancellationRequested) return;
        if (modPageServer != server.Id || !modBrowser || lookupId.Trim() != requestedId)
        { if (IsModsViewVisible) Render(); return; }
        lookedUpMod = result; lookupMessage = message;
        if (result is not null) modDetails[(result.Edition, result.Id)] = result;
        if (IsModsViewVisible) Render();
        if (result?.PreviewImage is { } picture) _ = LoadModPreviewAsync(result, picture);
    }

    private async Task LoadRecordedModDetailsAsync(ArkEdition edition, string id)
    {
        var key = (edition, id);
        if (!modDetailsLoading.Add(key)) return;
        var acquired = false;
        try
        {
            await modDetailsGate.WaitAsync(lifetime.Token); acquired = true;
            modDetailsAttempts[key] = DateTimeOffset.UtcNow;
            using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
            var metadata = edition == ArkEdition.ASA
                ? await new PublicAsaModMetadata(http).GetAsync(id, lifetime.Token)
                : await new SteamWorkshopMetadata(http).GetAsync(id, lifetime.Token);
            modDetails[key] = metadata;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or ArgumentException
            or ModLookupException or System.Text.Json.JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
        { /* Metadata is optional; retain the selected ID and allow later retry. */ }
        finally
        {
            if (acquired)
            {
                try { await Task.Delay(600, lifetime.Token); } catch (OperationCanceledException) { }
                modDetailsGate.Release();
            }
            modDetailsLoading.Remove(key);
            if (!lifetime.IsCancellationRequested && IsModsViewVisible) Render();
        }
    }

    private async Task LoadModPreviewAsync(ModMetadata expected, Uri picture)
    {
        var bitmap = await ReadModArtworkAsync(picture, lifetime.Token);
        try
        {
            if (bitmap is null || lifetime.IsCancellationRequested || !ReferenceEquals(lookedUpMod, expected)) return;
            modPreview?.Dispose(); modPreview = bitmap; bitmap = null;
            if (IsModsViewVisible) Render();
        }
        finally { bitmap?.Dispose(); }
    }
}

