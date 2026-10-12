using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private int creationStep;
    private bool? draftCluster;
    private ArkEdition? draftEdition;
    private string? draftMap;
    private string draftName = "", draftClusterName = "", draftDescription = "";
    private decimal? draftCapacity;
    private decimal? draftWelcomeDuration;
    private string? draftMode;
    private string draftJoinPassword = "", draftAdminPassword = "";
    private int customizationCategory;
    private bool? draftInternet;
    private AseClientAccess draftAseClients = AseClientAccess.Steam;
    private AsaClientPlatforms draftAsaClients = AsaClientPlatforms.All;
    private bool draftCustomMap;
    private string draftMapModId = "";
    private string draftMapFilter = "";
    private bool creationExistingCluster;
    private string creationValidation = "";
    private Guid creationServerId = Guid.NewGuid();
    private bool creationCheckBusy;
    private ServerCreationCheckRequest? checkedCreationRequest;
    private ServerCreationCheck? creationCheck;
    private string creationCheckMessage = "";
    private ServerPreset? creationPreset;
    private string? creationModDraft;

    private void ResetCreationDraft()
    {
        createdArks.Clear(); creationClusterDefaults = null; creationClusterInternet = null; creationClusterId = Guid.NewGuid();
        creationStep = customizationCategory = 0;
        draftCluster = draftInternet = null; draftEdition = null; draftMap = null;
        draftName = draftClusterName = draftDescription = draftJoinPassword = draftAdminPassword = "";
        draftCapacity = draftWelcomeDuration = null; draftMode = null; creationValidation = "";
        creationServerId = Guid.NewGuid(); checkedCreationRequest = null; creationCheck = null; creationCheckMessage = "";
        creationPreset = null;
        creationModDraft = null;
        draftAseClients = AseClientAccess.Steam;
        draftAsaClients = AsaClientPlatforms.All;
        draftCustomMap = false; draftMapModId = "";
        draftMapFilter = "";
        creationExistingCluster = false;
    }

    private void RenderCreationWizard()
    {
        if (creationStep == 5) { RenderCreationCompletion(); return; }
        var titles = new[] { "Create Server / Cluster", "Choose your game", "Choose your world", "Make it yours", "Review your ARK" };
        var subtitles = new[] { "Choose how you want to host your ARKs.", "Choose the edition for this server or cluster.",
            "Choose a world for the new server.", "Configure your server settings before creation.", "Check your choices before creating." };
        content.Children.Add(Label(titles[creationStep], Height < 760 ? 28 : 36, true));
        var subtitle = Label(subtitles[creationStep], 18); subtitle.Foreground = Muted; content.Children.Add(subtitle);
        var steps = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*"), Margin = new Thickness(0, 24, 18, 28) };
        var stepNames = new[] { "Type", "Game", "Map", "Customize", "Review" };
        for (var i = 0; i < stepNames.Length; i++)
        {
            var block = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
            var number = Label(i < creationStep ? "✓" : (i + 1).ToString(), 17, true);
            number.Foreground = i == creationStep ? Brush.Parse("#181B1F") : Muted;
            number.HorizontalAlignment = HorizontalAlignment.Center; number.VerticalAlignment = VerticalAlignment.Center;
            block.Children.Add(new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(16),
                BorderBrush = i <= creationStep ? Amber : Muted, BorderThickness = new Thickness(1),
                Background = i == creationStep ? Amber : Brushes.Transparent, Child = number });
            var label = Label(stepNames[i], 16); label.Foreground = i == creationStep ? Amber : Muted;
            block.Children.Add(label); Grid.SetColumn(block, i); steps.Children.Add(block);
        }
        if (Height < 760) content.Children.Add(Label("Step " + (creationStep + 1) + " of 5 · " + stepNames[creationStep], 14));
        else content.Children.Add(steps);
        switch (creationStep)
        {
            case 0: CreationTypeChoices(); break;
            case 1: CreationEditionChoices(); break;
            case 2: CreationMapChoices(); break;
            case 3: CreationCustomization(); break;
            case 4: CreationReview(); break;
        }
        if (creationValidation.Length > 0)
        {
            var warning = Label(creationValidation, 16); warning.Foreground = Amber; content.Children.Add(warning);
        }
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var locked = creationPublicationJobs.TryGetValue(creationServerId, out var publication) && !publication.Finished;
        locked |= creationConfigurationJobs.TryGetValue(creationServerId, out var preparation)
            && (preparation.Busy || preparation.Accepted && !preparation.Finished);
        footer.Children.Add(ActionButton(creationStep == 0 || creationExistingCluster && creationStep == 2 ? "Cancel" : "←  Back", locked || (createdArks.Count > 0 && creationStep == 2) ? null : () =>
        {
            creationValidation = "";
            if (creationStep == 0 || creationExistingCluster && creationStep == 2) ResetPage(); else creationStep--;
            Render();
        }));
        var next = ActionButton(creationStep == 4 ? publication?.Busy == true ? "Creating…"
            : publication is { Accepted: true, Finished: false } ? "Reconnect to creation" : "Create server" : "Continue  ›",
            creationStep == 4 ? CanPublishCreation() && publication?.Busy != true ? BeginCreationPublication : null : AdvanceCreation, true);
        Grid.SetColumn(next, 2); footer.Children.Add(next); creationFooter.Children.Add(footer);
        creationFooter.IsVisible = true;
        var note = Label("Create installs no additional files and leaves the new server stopped.", 13);
        note.Foreground = Muted; creationFooter.Children.Add(note);
    }

    private void AdvanceCreation()
    {
        creationValidation = creationStep switch
        {
            0 when draftCluster is null => "Choose a standalone server or a cluster.",
            1 when draftEdition is null => "Choose ASA or ASE.",
            2 when draftMap is null => "Choose a world.",
            3 when string.IsNullOrWhiteSpace(draftName) => "Enter a server name.",
            3 when draftCluster == true && string.IsNullOrWhiteSpace(draftClusterName) => "Enter a cluster name.",
            3 when draftCapacity is null or < 1 => "Choose the maximum number of players.",
            3 when draftWelcomeDuration is decimal duration && (duration < 1 || duration > int.MaxValue || duration != decimal.Truncate(duration))
                => "Choose a positive whole number of seconds for the welcome message.",
            3 when draftMode is null => "Choose PvE or PvP.",
            3 when draftInternet is null => "Choose local network or internet in the Network category.",
            3 when draftEdition == ArkEdition.ASA && draftAsaClients == 0 => "Choose at least one ASA player platform in Gameplay.",
            _ => ""
        };
        if (creationStep == 3 && creationValidation.Length == 0)
        {
            try
            {
                var mods = CreationModIds();
                if (draftEdition == ArkEdition.ASE && draftAseClients != AseClientAccess.Steam && mods.Count > 0)
                    throw new ArgumentException("ASE Workshop mods require Steam clients. Choose Steam access or remove the mods.");
            }
            catch (ArgumentException error) { creationValidation = error.Message; customizationCategory = 4; }
        }
        if (creationStep == 3 && creationValidation.Length == 0)
        {
            try { ServerAccessConfiguration.ValidatePasswords(draftAdminPassword, draftJoinPassword); }
            catch (ArgumentException error) { creationValidation = error.Message; customizationCategory = 2; }
        }
        if (creationStep == 2 && creationValidation.Length == 0)
        {
            try
            {
                ServerMapIdentifier.ForLaunch(draftEdition!.Value, draftMap!);
                if (draftCustomMap) CreationModIds();
            }
            catch (ArgumentException error) { creationValidation = error.Message; }
        }
        if (creationValidation.Length == 0) creationStep++;
        Render();
    }

    private static Button ChoiceCard(string name, string description, Control artwork, bool selected, Action choose, bool titleFirst = false)
    {
        var body = new StackPanel { Spacing = 20 };
        var title = Label(name, 28, true); title.HorizontalAlignment = HorizontalAlignment.Center;
        if (titleFirst) body.Children.Add(title);
        artwork.HorizontalAlignment = HorizontalAlignment.Center; body.Children.Add(artwork);
        if (!titleFirst) body.Children.Add(title);
        var caption = Label(description, 17); caption.Foreground = Muted; caption.TextAlignment = TextAlignment.Center;
        body.Children.Add(caption);
        var mark = Label(selected ? "◉" : "○", 28); mark.Foreground = selected ? Amber : Muted;
        mark.HorizontalAlignment = HorizontalAlignment.Center; body.Children.Add(mark);
        var button = DesignCard(body, name, 374, choose);
        button.Width = 390; button.Padding = new Thickness(20); button.Margin = new Thickness(0, 0, 24, 16);
        button.BorderBrush = selected ? Amber : Brush.Parse("#2B3A46");
        Avalonia.Automation.AutomationProperties.SetName(button, "Choose " + name);
        return button;
    }

    private void CreationTypeChoices()
    {
        var cards = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        cards.Children.Add(ChoiceCard("Standalone server", "One ARK world with its own settings.",
            MapPicture("TheIsland", 348, 176), draftCluster == false, () => { draftCluster = false; creationValidation = ""; Render(); }));
        cards.Children.Add(ChoiceCard("Cluster", "Several ARK worlds with shared\nsettings and transfers.",
            new Border { Height = 176, Child = VectorIcon(ClusterIcon, Amber, 108) }, draftCluster == true,
            () => { draftCluster = true; creationValidation = ""; Render(); }));
        content.Children.Add(cards);
    }

    private void CreationEditionChoices()
    {
        var cards = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var edition in new[] { ArkEdition.ASA, ArkEdition.ASE })
        {
            var game = edition == ArkEdition.ASA ? "ARK: Survival Ascended" : "ARK: Survival Evolved";
            cards.Children.Add(ChoiceCard(edition.ToString(), game,
                MapPicture(edition == ArkEdition.ASA ? "TheIsland" : "Ragnarok", 348, 176), draftEdition == edition, () =>
                {
                    if (draftEdition != edition)
                    {
                        draftMap = null; creationPreset = null;
                        draftCustomMap = false; draftMapModId = "";
                        if (installationJobs.ContainsKey(creationServerId)) creationServerId = Guid.NewGuid();
                        checkedCreationRequest = null; creationCheck = null;
                    }
                    draftEdition = edition; creationValidation = ""; Render();
                }));
        }
        content.Children.Add(cards);
        content.Children.Add(Label("ASA and ASE use separate server files and cannot share a cluster.", 15));
    }

    private void CreationMapChoices()
    {
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 0, 0, 20) };
        tabs.Children.Add(ActionButton("Official maps", () => { draftCustomMap = false; draftMap = null; draftMapModId = ""; Render(); }, !draftCustomMap));
        tabs.Children.Add(ActionButton("Modded maps", () => { draftCustomMap = true; draftMap = null; Render(); }, draftCustomMap));
        content.Children.Add(tabs);
        if (draftCustomMap)
        {
            var custom = new StackPanel { Spacing = 18 };
            AddMapInput("Map launch name", draftMap ?? "", value => draftMap = string.IsNullOrWhiteSpace(value) ? null : value.Trim());
            AddMapInput(draftEdition == ArkEdition.ASA ? "CurseForge project ID" : "Steam Workshop mod ID", draftMapModId, value => draftMapModId = value.Trim());
            void AddMapInput(string title, string text, Action<string> changed)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("210,*") };
                var caption = Label(title, 16); caption.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(caption);
                var field = new TextBox { Text = text, FontSize = 16, Padding = new Thickness(12, 10), Background = Brush.Parse("#101C26") };
                Avalonia.Automation.AutomationProperties.SetName(field, title);
                field.TextChanged += (_, _) => changed(field.Text ?? ""); Grid.SetColumn(field, 1); row.Children.Add(field); custom.Children.Add(row);
            }
            custom.Children.Add(Label("Use the exact map launch name supplied by its author. The map’s mod ID will be included in downloads when the server starts. Some premium maps require player ownership.", 16));
            content.Children.Add(Surface(custom)); return;
        }
        var search = new TextBox { Text = draftMapFilter, PlaceholderText = "Find a world…", MaxLength = 128,
            Margin = new Thickness(0, 0, 0, 18) };
        Avalonia.Automation.AutomationProperties.SetName(search, "Find an official world");
        content.Children.Add(search);
        var cards = new WrapPanel();
        void PopulateMaps()
        {
            cards.Children.Clear();
            foreach (var choice in ServerMapCatalog.ForEdition(draftEdition!.Value).Where(choice => choice.Name.Contains(draftMapFilter.Trim(), StringComparison.OrdinalIgnoreCase)
                || choice.Identifier.Contains(draftMapFilter.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                var map = choice.Identifier;
                cards.Children.Add(ChoiceCard(choice.Name, draftEdition + " world",
                    MapPicture(map, 348, 176), draftMap == map, () => { draftMap = map; creationValidation = ""; Render(); }, titleFirst: true));
            }
            if (cards.Children.Count == 0) cards.Children.Add(Label("No worlds match this search.", 17));
        }
        search.TextChanged += (_, _) => { draftMapFilter = search.Text ?? ""; PopulateMaps(); };
        PopulateMaps();
        content.Children.Add(cards);
        var note = Label("Map availability depends on the installed server build. Some worlds require players to own the matching DLC.", 15);
        note.Foreground = Muted; content.Children.Add(note);
    }

    private List<string> CreationModIds()
    {
        var mods = creationModDraft is null ? creationPreset is null ? new List<string>() : new List<string>(creationPreset.Mods)
            : creationModDraft.Split([',', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        if (draftCustomMap)
        {
            if (!ulong.TryParse(draftMapModId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) || id == 0)
                throw new ArgumentException("Enter the custom map’s positive numeric mod ID.");
            if (!mods.Any(mod => ulong.TryParse(mod, out var existing) && existing == id)) mods.Add(id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        try { AsaModLaunchArguments.Build(mods); }
        catch (ArgumentException) { throw new ArgumentException("Enter distinct positive numeric mod IDs in load order."); }
        return mods;
    }

    private void CreationCustomization()
    {
        var body = new StackPanel { Spacing = 22 }; body.Children.Add(Label("Server Settings", 27, true));
        var categories = new[] { "General", "Gameplay", "Access", "Network", "Mods" };
        var tabs = new WrapPanel();
        for (var i = 0; i < categories.Length; i++)
        {
            var index = i;
            var tab = ActionButton(categories[i], () => { customizationCategory = index; Render(); }, customizationCategory == i);
            tab.Margin = new Thickness(0, 0, 10, 10); tabs.Children.Add(tab);
        }
        body.Children.Add(tabs);
        var fields = new WrapPanel();
        switch (customizationCategory)
        {
            case 0:
                fields.Children.Add(DraftTextField("Server name", draftName, value => draftName = value, "Name visible in the server list"));
                var capacity = new NumericUpDown { Value = draftCapacity, Minimum = 1, Maximum = int.MaxValue, Increment = 1,
                    FormatString = "0", FontSize = 16, Padding = new Thickness(10), PlaceholderText = "Choose capacity" };
                capacity.ValueChanged += (_, _) => draftCapacity = capacity.Value;
                fields.Children.Add(DraftField("Max players", capacity, "Maximum number of players"));
                if (draftCluster == true) fields.Children.Add(createdArks.Count > 0 || creationExistingCluster
                    ? DraftField("Cluster", Label(draftClusterName, 17))
                    : DraftTextField("Cluster name", draftClusterName, value => draftClusterName = value));
                var mode = new ComboBox { ItemsSource = new[] { "PvE", "PvP" }, SelectedItem = draftMode,
                    PlaceholderText = "Choose PvE or PvP", HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 16 };
                mode.SelectionChanged += (_, _) => draftMode = mode.SelectedItem as string;
                fields.Children.Add(DraftField("PvE / PvP", mode));
                fields.Children.Add(DraftTextField("Welcome message (optional)", draftDescription, value => draftDescription = value));
                var duration = new NumericUpDown { Value = draftWelcomeDuration, Minimum = 1, Maximum = int.MaxValue,
                    Increment = 1, FormatString = "0", FontSize = 16, Padding = new Thickness(10), PlaceholderText = "ARK default" };
                duration.ValueChanged += (_, _) => draftWelcomeDuration = duration.Value;
                fields.Children.Add(DraftField("Welcome message duration (seconds)", duration, "Leave empty to use ARK’s default."));
                break;
            case 1:
                fields.Children.Add(CreationPresetPicker());
                if (draftEdition == ArkEdition.ASE)
                {
                    var clients = new ComboBox { ItemsSource = new[] { "Steam", "Steam and Epic Games", "Epic Games" }, SelectedIndex = (int)draftAseClients,
                        HorizontalAlignment = HorizontalAlignment.Stretch };
                    clients.SelectionChanged += (_, _) => { if (clients.SelectedIndex >= 0) draftAseClients = (AseClientAccess)clients.SelectedIndex; };
                    fields.Children.Add(DraftField("Player clients", clients, "ASE Workshop mods require Steam. These options apply to this PC dedicated-server package."));
                }
                else
                {
                    foreach (var (platform, title) in new[] { (AsaClientPlatforms.PC, "PC clients"), (AsaClientPlatforms.WindowsStore, "Windows Store"),
                        (AsaClientPlatforms.XboxSeries, "Xbox Series X/S"), (AsaClientPlatforms.PlayStation5, "PlayStation 5") })
                    {
                        var choice = new CheckBox { Content = title, IsChecked = draftAsaClients.HasFlag(platform) };
                        choice.IsCheckedChanged += (_, _) => draftAsaClients = choice.IsChecked == true ? draftAsaClients | platform : draftAsaClients & ~platform;
                        fields.Children.Add(choice);
                    }
                    fields.Children.Add(Label("Selected mods must support the chosen player platforms. Matching game builds and a reachable server are required.", 15));
                }
                break;
            case 2:
                fields.Children.Add(DraftTextField("Join password (optional)", draftJoinPassword, value => draftJoinPassword = value, secret: true));
                fields.Children.Add(CreationAdministratorPassword());
                break;
            case 3:
                fields.Children.Add(ActionButton("Local network", () => { draftInternet = false; Render(); }, draftInternet == false));
                var internet = ActionButton("Internet", () => { draftInternet = true; Render(); }, draftInternet == true);
                internet.Margin = new Thickness(14, 0, 0, 0); fields.Children.Add(internet);
                break;
            case 4:
                var modFields = new StackPanel { Spacing = 16, Width = Math.Clamp(Bounds.Width - 175, 600, 1050) };
                modFields.Children.Add(Label(draftEdition == ArkEdition.ASA ? "CurseForge mod IDs" : "Steam Workshop mod IDs", 23, true));
                modFields.Children.Add(Label("Enter IDs separated by commas or new lines, in load order. Leave empty for no mods.", 16));
                var modIds = new TextBox { Text = creationModDraft ?? string.Join(", ", creationPreset?.Mods ?? []),
                    AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 105, MaxLength = 8192,
                    PlaceholderText = "Mod IDs" };
                modIds.TextChanged += (_, _) => creationModDraft = modIds.Text ?? "";
                modFields.Children.Add(modIds);
                modFields.Children.Add(ActionButton("Find a mod by ID…", designPreview ? null : () => _ = ReviewCreationModAsync()));
                if (draftCustomMap) modFields.Children.Add(Label("The custom map’s ID is included automatically: " + draftMapModId, 15));
                modFields.Children.Add(Label("Downloads are prepared before the server starts. Mod details and options remain available on its Mods page after creation.", 16));
                fields.Children.Add(modFields);
                break;
        }
        body.Children.Add(fields);
        if (customizationCategory == 3)
        {
            body.Children.Add(Label(draftInternet == true ? "Internet hosting may require router configuration. Firewall changes will ask first."
                : "Players on the same local network can connect.", 16));
            body.Children.Add(new Expander { Header = "Ports (automatically assigned)", Content = Label("Ports are assigned and checked before creation. You can edit them in the server’s Network settings after creation.", 16) });
        }
        content.Children.Add(Surface(body, new Thickness(30)));
    }

    private static Control DraftTextField(string title, string value, Action<string> changed, string? help = null, bool secret = false)
    {
        var field = new TextBox { Text = value, FontSize = 16, PasswordChar = secret ? '●' : '\0',
            Padding = new Thickness(12, 10), Background = Brush.Parse("#101C26") };
        field.TextChanged += (_, _) => changed(field.Text ?? "");
        Avalonia.Automation.AutomationProperties.SetName(field, title);
        return DraftField(title, field, help);
    }

    private Control CreationAdministratorPassword()
    {
        var body = new StackPanel { Spacing = 10 };
        var password = new TextBox { Text = draftAdminPassword, PasswordChar = '●', FontSize = 16,
            Padding = new Thickness(12, 10), Background = Brush.Parse("#101C26") };
        Avalonia.Automation.AutomationProperties.SetName(password, "Administrator password");
        password.TextChanged += (_, _) => draftAdminPassword = password.Text ?? "";
        body.Children.Add(password);
        var options = new WrapPanel();
        options.Children.Add(ActionButton("Generate password", () =>
        {
            password.Text = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24))
                .Replace('+', '-').Replace('/', '_');
        }));
        var reveal = new CheckBox { Content = "Show password", Margin = new Thickness(16, 0, 0, 0) };
        reveal.IsCheckedChanged += (_, _) => password.PasswordChar = reveal.IsChecked == true ? '\0' : '●';
        options.Children.Add(reveal); body.Children.Add(options);
        return DraftField("Administrator password", body, "Enter your own password or generate one. You can edit either.");
    }

    private static Control DraftField(string title, Control control, string? help = null)
    {
        var field = new StackPanel { Width = 520, Spacing = 10, Margin = new Thickness(0, 0, 28, 22) };
        field.Children.Add(Label(title, 16, true)); field.Children.Add(control);
        if (help is not null) { var caption = Label(help, 14); caption.Foreground = Muted; field.Children.Add(caption); }
        return field;
    }

    private void CreationReview()
    {
        var body = new StackPanel { Spacing = 14 }; body.Children.Add(Label("Your choices", 27, true));
        var fields = new WrapPanel();
        fields.Children.Add(MapPicture(draftMap!, 280, 190));
        var choices = new StackPanel { Width = 430, Margin = new Thickness(24, 0, 0, 0), Spacing = 10 };
        choices.Children.Add(Label(draftName, 23, true));
        choices.Children.Add(Label($"{draftEdition!.Value} · {MapName(draftMap!)}", 17));
        choices.Children.Add(Label(draftCluster == true ? $"Cluster: {draftClusterName}" : "Standalone server", 17));
        choices.Children.Add(Label($"{draftMode} · {draftCapacity!.Value:0} players", 17));
        choices.Children.Add(Label(draftInternet == true ? "Internet" : "Local network", 17));
        if (creationPreset is not null) choices.Children.Add(Label("Preset: " + creationPreset.Name, 17));
        var selectedMods = CreationModIds();
        choices.Children.Add(Label(selectedMods.Count + " mods", 17));
        fields.Children.Add(choices);
        body.Children.Add(fields);
        if (selectedMods.Count > 0) body.Children.Add(new Expander { Header = "Selected mod IDs in load order", Content = Label(string.Join(", ", selectedMods), 16) });
        var settings = new StackPanel { Spacing = 8 };
        Fact(settings, "Join password", draftJoinPassword.Length == 0 ? "None" : "Set");
        Fact(settings, "Administrator password", draftAdminPassword.Length == 0 ? "Not set" : "Set");
        if (draftDescription.Length > 0) Fact(settings, "Welcome message", draftDescription);
        Fact(settings, "Welcome message duration", draftWelcomeDuration is decimal seconds ? $"{seconds:0} seconds" : "ARK default");
        body.Children.Add(new Expander { Header = "Access and welcome message", Content = settings });
        body.Children.Add(Label("Install server files, prepare configuration, then create your server.", 16));
        if (creationPublicationJobs.TryGetValue(creationServerId, out var publication)) body.Children.Add(Label(publication.Message, 15));
        var request = CreationCheckRequest();
        body.Children.Add(ActionButton(creationCheckBusy ? "Checking setup…" : "Check server location and ports",
            client is null || creationCheckBusy ? null : () => _ = CheckCreationAsync(request)));
        if (creationCheckMessage.Length > 0) body.Children.Add(Label(creationCheckMessage, 15));
        if (creationCheck is { } result && request == checkedCreationRequest)
        {
            var details = new StackPanel { Spacing = 10 };
            Fact(details, "Server folder", result.Directory);
            Fact(details, "Available ports", $"Game {result.GamePort} · Query {result.QueryPort} · Administration {result.RconPort}");
            foreach (var finding in result.Findings) details.Children.Add(Label(finding, 15));
            body.Children.Add(new Expander { Header = "Setup check details", Content = details });
            if (!installationJobs.ContainsKey(creationServerId) && !(result.Platform == ServerPlatform.Linux && draftEdition == ArkEdition.ASA))
                body.Children.Add(ActionButton("Install server files…", () => _ = ReviewInstallationAsync(
                    new(creationServerId, draftEdition!.Value, result.Platform, false, false), draftName)));
            AddCreationConfiguration(body, result);
        }
        if (installationJobs.TryGetValue(creationServerId, out var installation)) body.Children.Add(InstallationJobCard(installation));
        content.Children.Add(Surface(body));
    }

    private ServerCreationCheckRequest CreationCheckRequest() => new(creationServerId, draftEdition!.Value, ServerMapIdentifier.ForLaunch(draftEdition.Value, draftMap!),
        draftName, (int)draftCapacity!.Value, draftInternet!.Value, draftCluster == true ? draftClusterName : null);

    private async Task CheckCreationAsync(ServerCreationCheckRequest request)
    {
        if (client is null || creationCheckBusy || lifetime.IsCancellationRequested) return;
        creationCheckBusy = true; creationCheckMessage = "Checking the new server location and available ports…"; Render();
        try
        {
            var result = await client.CallAsync<ServerCreationCheck>("server-creation-check", request,
                TimeSpan.FromSeconds(8), lifetime.Token);
            if (result.ServerId != request.ServerId || result.Findings is null || !Path.IsPathFullyQualified(result.Directory))
                throw new InvalidDataException("Invalid setup check.");
            new ServerSettings { Name = request.Name, MaxPlayers = request.MaxPlayers,
                GamePort = result.GamePort, QueryPort = result.QueryPort, RconPort = result.RconPort }.Validate();
            if (creationServerId == request.ServerId)
            { checkedCreationRequest = request; creationCheck = result; creationCheckMessage = "Location and port check completed. No files were installed."; }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or InvalidOperationException
            or ArgumentException or System.Text.Json.JsonException)
        { if (creationServerId == request.ServerId) { creationCheck = null; creationCheckMessage = "Setup could not be checked. Review your choices and try again."; } }
        finally { creationCheckBusy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }
}

