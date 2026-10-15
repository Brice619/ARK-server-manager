using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Avalonia.Styling;
using ArkServerManager.Core;
using System.Globalization;
using System.Text.Json;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private sealed record SettingField(string Label, string Section, string Key, string Default, string Kind = "text", int File = 0);
    public sealed record SettingsBindingReview(string Label,int File,string Section,string Key,string Kind);
    private readonly List<SettingsBindingReview> settingsBindingReview=[];
    private readonly Dictionary<Guid, Dictionary<(int File, string Section, string Key), string>> screenSettingDrafts = [];
    private readonly HashSet<Guid> screenSettingsLoading = [];
    private string settingsSearch = "";
    private static readonly string[] SettingCategories = ["General", "Rates", "Players", "Creatures", "Breeding", "Structures", "World", "Engrams", "Network", "Advanced"];
    private readonly Dictionary<(ArkEdition, string), Bitmap> selectedModArtwork = [];
    private readonly HashSet<(ArkEdition, string)> selectedModArtworkLoading = [];
    private readonly Dictionary<(ArkEdition, string), Action> selectedModRowRefresh = [];
    private List<string>? reviewSavedMods;
    private List<string>? reviewArtIds;
    private readonly Dictionary<(int File, string Section, string Key), string> reviewSavedSettings = [];

    private Button EditorButton(string text, Action? click, bool gold = false, double width = double.NaN)
    {
        var button = OverviewAction(text, gold ? "#FFDA43" : "#B5DEFF", click, width);
        button.BorderBrush = Brush.Parse(gold ? "#FFDA43" : "#287997");
        button.Height = 48; button.FontSize = 19; button.Padding = new(10, 6);
        if (!text.Contains("  "))
        {
            var glyph = text switch { "Save Changes" => "M2,2 L19,2 L23,6 L23,23 L2,23 Z M7,2 L7,10 L17,10 L17,2 M7,23 L7,15 L18,15 L18,23", "Configure" => GearIcon, "Advanced INI" => "M3,1 L15,1 L22,8 L22,23 L3,23 Z M15,1 L15,8 L22,8 M7,12 L18,12 M7,17 L18,17", "Add by ID" => "M9,15 L15,9 M10,5 L14,1 C22,-3 29,5 23,11 L19,15 M14,19 L10,23 C2,29 -5,21 1,15 L5,11", _ => "" };
            if (glyph.Length > 0) { var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, VerticalAlignment = VerticalAlignment.Center }; row.Children.Add(VectorIcon(glyph, Brush.Parse(gold ? "#FFDA43" : "#B5DEFF"), 24)); var title = Label(text, 19); title.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(title); row.Spacing = 12; row.HorizontalAlignment = HorizontalAlignment.Center; button.Content = row; }
        }
        if (button.Content is StackPanel line && line.Children.LastOrDefault() is TextBlock label) label.FontSize = 19;
        return button;
    }

    private static TextBox EditorText(string value, string placeholder = "")
    {
        var box = new TextBox { Text = value, PlaceholderText = placeholder, Height = 44, FontSize = 20, Padding = new(12, 8), Background = Brush.Parse("#071A27"), BorderBrush = Brush.Parse("#32708F"), BorderThickness = new(1), CornerRadius = new(5) };
        box.Resources["TextControlBorderBrushFocused"]=Brush.Parse("#22C8FF");
        box.Resources["TextControlBorderBrushPointerOver"]=Brush.Parse("#539BC1");
        box.Resources["TextControlPlaceholderForeground"]=Brush.Parse("#A9CAE8");
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Disabled); ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Disabled); return box;
    }

    private static Control SearchField(TextBox text)
    {
        text.Padding=new(50,8,12,8);var row=new Grid();row.Children.Add(text);
        var icon=VectorIcon("M10,1 A9,9 0 1 0 10,19 A9,9 0 1 0 10,1 M17,17 L24,24",Brush.Parse("#A9CAE8"),24);
        icon.HorizontalAlignment=HorizontalAlignment.Left;icon.Margin=new(16,0,0,0);icon.IsHitTestVisible=false;row.Children.Add(icon);return row;
    }

    private string SettingValue(ServerDefinition server, SettingField field, Dictionary<(int, string, string), string> draft, Dictionary<(Guid Server, int File), ServerConfigurationFile> loaded)
    {
        if (draft.TryGetValue((field.File, field.Section, field.Key), out var value)) return value;
        if (IsReviewServer(server.Id) && reviewSavedSettings.TryGetValue((field.File, field.Section, field.Key), out value)) return value;
        return loaded.TryGetValue((server.Id, field.File), out var file) && file.Text is { } text
            ? new IniDocument(text).GetValues(field.Section, field.Key).LastOrDefault() ?? field.Default : field.Default;
    }

    private void ApprovedServerSettings(ServerDefinition server)
    {
        settingsBindingReview.Clear();
        if (!screenSettingDrafts.TryGetValue(server.Id, out var draft)) screenSettingDrafts[server.Id] = draft = [];
        var dummy = IsReviewServer(server.Id) || designPreview;
        if (!advancedLoadedFiles.ContainsKey((server.Id, 0)) && screenSettingsLoading.Add(server.Id)) Avalonia.Threading.Dispatcher.UIThread.Post(()=>_ = LoadScreenSettingsAsync(server));
        var ready = dummy || advancedLoadedFiles.GetValueOrDefault((server.Id, 0))?.SourceSHA256 is not null;
        var height = Math.Max(290, Math.Min(633, Bounds.Height > 0 ? Bounds.Height - 390 : 633));
        var shell = new Grid { RowDefinitions = new("50,10,*,8,70"), Height = height - 36 };
        var searchRow = new Grid { ColumnDefinitions = new("*,12,190") };
        var search = EditorText(settingsSearch, "Search settings..."); search.Height = 50;
        Avalonia.Automation.AutomationProperties.SetName(search, "Search settings"); searchRow.Children.Add(SearchField(search));
        if(settingsCategory==7)
        {
            search.PlaceholderText="Search engrams...";
        }
        var advanced = EditorButton("Advanced INI", () => { advancedEditorServer = server.Id; advancedEditorFile = 0; Render(); }, width: 190); Grid.SetColumn(advanced, 2); searchRow.Children.Add(advanced); shell.Children.Add(searchRow);
        var work = new Grid { ColumnDefinitions = new("238,*") }; Grid.SetRow(work, 2); shell.Children.Add(work);
        var nav = new StackPanel { Spacing = 0, Margin = new(0, 4, 0, 0) };
        for (var i = 0; i < SettingCategories.Length; i++)
        {
            var index = i; var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20, VerticalAlignment = VerticalAlignment.Center };
            var glyph = i switch { 0 => GearIcon, 1 => "M2,2 L2,22 L23,22 M5,17 L11,11 L15,14 L23,4", 2 => PlayersGlyph, 3 => "M4,17 A6,5 0 0 0 20,17 L17,12 L7,12 Z M3,3 L5,7 M10,1 L10,5 M17,2 L17,6 M23,5 L21,9", 4 => "M12,1 C1,8 1,22 12,23 C23,22 23,8 12,1 Z", 5 => BoxIcon, 6 => "M12,1 A11,11 0 1 0 12,23 A11,11 0 1 0 12,1 M1,12 L23,12 M12,1 C5,7 5,17 12,23 C19,17 19,7 12,1", 7 => "M1,3 L11,5 L23,3 L23,22 L11,24 L1,22 Z M11,5 L11,24", 8 => "M1,7 Q12,-3 23,7 M5,12 Q12,5 19,12 M9,17 Q12,14 15,17 M12,21 L12,22", _ => "M3,23 L14,12 M14,12 C9,3 18,-1 23,2 L18,7 L21,10 C25,15 17,18 14,12" };
            line.Children.Add(VectorIcon(glyph, Brush.Parse("#A9DAFF"), 26)); line.Children.Add(Label(SettingCategories[i], 19));
            var button = new Button { Content = line, Width = 228, Height = 48, Padding = new(20, 4), Background = settingsCategory == i ? Brush.Parse("#08334B") : Brushes.Transparent, BorderBrush = Brush.Parse("#20C7FF"), BorderThickness = settingsCategory == i ? new(1) : new(0), CornerRadius = new(5), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            button.Resources["ButtonBackgroundPointerOver"] = Brush.Parse("#10364C");
            Avalonia.Automation.AutomationProperties.SetName(button, SettingCategories[i]); button.Click += (_, _) => { settingsCategory = index; Render(); }; nav.Children.Add(button);
        }
        var navScroll = new ScrollViewer { Content = nav, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch, Height = height - 96, VerticalAlignment = VerticalAlignment.Top, Margin = new(-4, 0, 14, -80) }; work.Children.Add(navScroll);
        var groups = new Grid { ColumnDefinitions = new("1.1*,14,*"), RowDefinitions = new("Auto,14,Auto"), Margin = new(15, 6, 22, 10) };
        TextBox? peerPortBox=null;
        Border Group(string title, IEnumerable<SettingField> fields)
        {
            fields=fields.ToArray();settingsBindingReview.AddRange(fields.Select(f=>new SettingsBindingReview(f.Label,f.File,f.Section,f.Key,f.Kind)));
            var shortGeneral=settingsCategory==0&&Bounds.Height>0&&Bounds.Height<1024;var compact=fields.Any(f=>f.Kind=="stat");
            var body = new StackPanel { Spacing = compact?3:shortGeneral?8:settingsCategory is 0 or 8?12:settingsCategory==2?3:8 }; body.Children.Add(Label(title, 24, true)); body.Children.Add(new Border { Height = 1, Background = Brush.Parse("#30617B"), Margin = new(0, compact?0:shortGeneral?-5:settingsCategory==2?-10:-9, 0, compact?0:-4) });
            if(compact)body.Children.Add(Label("Stat                                                                Per-level multiplier",17));



            foreach (var field in fields)
            {
                var row = new Grid { ColumnDefinitions = new(field.Kind=="stat"?"1.46*,*":settingsCategory is 0 or 8?"*,314":settingsCategory==1?"*,258":"*,244"), Height = field.Kind=="stat"?32:shortGeneral?43:settingsCategory==2?35:settingsCategory is 0 or 8?45:42 }; var caption = Label(field.Label, field.Kind=="stat"?18:19); caption.VerticalAlignment = VerticalAlignment.Center; caption.TextWrapping=TextWrapping.Wrap; row.Children.Add(caption);
                var value = SettingValue(server, field, draft, advancedLoadedFiles); Control input;
                void Changed(string next) { if(field.Kind!="readonly")draft[(field.File, field.Section, field.Key)] = next;if(field.Key=="Port"&&peerPortBox is not null&&int.TryParse(next,out var port))peerPortBox.Text=(port+1).ToString(CultureInfo.InvariantCulture); }
                if(field.Kind=="launch-bool")
                {
                    var preferences=launchPreferenceDrafts.GetValueOrDefault(server.Id,loadedLaunchPreferences.GetValueOrDefault(server.Id)?.Preferences??server.LaunchPreferences);
                    input=SettingsToggle(preferences.AutoDestroyStructures,on=>
                    {
                        launchPreferenceDrafts[server.Id]=launchPreferenceDrafts.GetValueOrDefault(server.Id,preferences) with {AutoDestroyStructures=on};
                        if(on){var period=SettingValue(server,UserNumber("","AutoDestroyOldStructuresMultiplier","0.0"),draft,advancedLoadedFiles);if(!decimal.TryParse(period,NumberStyles.Float,CultureInfo.InvariantCulture,out var multiplier)||multiplier<=0)draft[(0,"ServerSettings","AutoDestroyOldStructuresMultiplier")]="1.0";}
                    });
                }
                else if (field.Kind is "bool" or "max-difficulty" or "bool-inverted")
                {
                    var toggle = new ToggleButton { IsChecked = field.Kind == "max-difficulty" ? decimal.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out var difficulty) && difficulty == 5 : bool.TryParse(value, out var flag) && (field.Kind == "bool-inverted" ? !flag : flag), Width = 64, Height = 38, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = ready };
                    toggle.Template = new FuncControlTemplate<ToggleButton>((t, _) =>
                    {
                        var knob = new Border { Width = 28, Height = 28, CornerRadius = new(14), Background = Brushes.White, Margin = new(5), VerticalAlignment = VerticalAlignment.Center }; var track = new Border { CornerRadius = new(19), Child = knob };
                        void Paint() { track.Background = Brush.Parse(t.IsChecked == true ? "#009BFF" : "#25455D"); knob.HorizontalAlignment = t.IsChecked == true ? HorizontalAlignment.Right : HorizontalAlignment.Left; }
                        Paint(); t.IsCheckedChanged += (_, _) => Paint(); return track;
                    });
                    toggle.IsCheckedChanged += (_, _) => Changed(field.Kind == "max-difficulty" ? toggle.IsChecked == true ? "5.0" : "_default" : (toggle.IsChecked == true) != (field.Kind == "bool-inverted") ? "True" : "False"); input = toggle;
                }
                else if (field.Kind is "number" or "stat" or "integer")
                {
                    var number = new NumericUpDown { Value = decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 1, Minimum = field.Key == "MaxPlayers" ? 1 : 0, Maximum = 1000000, Increment = field.Kind is "stat" or "number" && field.Key != "MaxPlayers" && field.Key != "MaxTamedDinos" && field.Key != "TheMaxStructuresInRange" ? 0.1m : 1, FormatString = field.Kind == "integer" ? "0" : field.Kind == "stat" || field.Key.EndsWith("Multiplier") || field.Key == "DifficultyOffset" ? "0.0#" : field.Key == "AutoSavePeriodMinutes" ? "0 'min'" : "0.##", Height = 44, FontSize = 20, Background = Brush.Parse("#071A27"), BorderBrush = Brush.Parse("#32708F"), IsEnabled = ready };
                    number.Resources["TextControlBorderBrushFocused"]=Brush.Parse("#22C8FF");number.ShowButtonSpinner = false; ScrollViewer.SetVerticalScrollBarVisibility(number, ScrollBarVisibility.Disabled); number.ValueChanged += (_, _) => Changed(number.Value?.ToString(CultureInfo.InvariantCulture) ?? "0");
                    number.MinHeight=0;number.Padding=new(30,0,30,0);number.TextAlignment=TextAlignment.Center;number.VerticalContentAlignment=VerticalAlignment.Center;
                    var fieldStyle=new Avalonia.Styling.Style(s=>s.OfType<TextBox>());fieldStyle.Setters.Add(new Avalonia.Styling.Setter(ScrollViewer.VerticalScrollBarVisibilityProperty,ScrollBarVisibility.Disabled));fieldStyle.Setters.Add(new Avalonia.Styling.Setter(ScrollViewer.HorizontalScrollBarVisibilityProperty,ScrollBarVisibility.Disabled));fieldStyle.Setters.Add(new Avalonia.Styling.Setter(TextBox.MinHeightProperty,0d));fieldStyle.Setters.Add(new Avalonia.Styling.Setter(TextBox.VerticalContentAlignmentProperty,VerticalAlignment.Center));fieldStyle.Setters.Add(new Avalonia.Styling.Setter(TextBox.PaddingProperty,new Thickness(30,0,30,0)));number.Styles.Add(fieldStyle);
                    if(field.Kind=="stat"){number.Height=32;number.MinHeight=0;number.Padding=new(30,0,30,0);number.FontSize=18;}else if(settingsCategory==2)number.Height=34;
                    if(field.Kind=="stat")ToolTip.SetTip(number,"ARK treats zero as the default multiplier; use a small positive value to reduce the gain.");
                    var numeric = new Grid { ColumnDefinitions = new("*,30") }; numeric.Children.Add(number); Grid.SetColumnSpan(number, 2);
                    var arrows = new Grid { RowDefinitions = new("*,*"), Margin = new(0, 2, 4, 2) };
                    foreach (var direction in new[] { 1, -1 }) { var step = direction; var arrow = new Button { Content = VectorIcon(direction == 1 ? "M3,15 L12,6 L21,15" : "M3,6 L12,15 L21,6", Brush.Parse("#A8D8FF"), 13), Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(0), IsEnabled = ready, HorizontalContentAlignment = HorizontalAlignment.Center }; arrow.Click += (_, _) => number.Value = Math.Clamp((number.Value ?? 0) + step * number.Increment, number.Minimum, number.Maximum); Grid.SetRow(arrow, direction == 1 ? 0 : 1); arrows.Children.Add(arrow); }
                    Grid.SetColumn(arrows, 1); numeric.Children.Add(arrows); input = numeric;
                }
                else
                {
                    var box = EditorText(value); box.IsEnabled = ready; box.IsReadOnly = field.Kind == "readonly"; box.MaxLength = 256;
                    if(field.Key=="PeerPort"){box.TextAlignment=TextAlignment.Center;peerPortBox=box;var gamePort=SettingValue(server,new("","SessionSettings","Port",server.Settings.GamePort.ToString(),"integer"),draft,advancedLoadedFiles);if(int.TryParse(gamePort,out var port))box.Text=(port+1).ToString(CultureInfo.InvariantCulture);}
                    if (field.Kind == "password")
                    {
                        box.PasswordChar = '●'; var wrapper = new Grid { ColumnDefinitions = new("*,40") }; wrapper.Children.Add(box); Grid.SetColumnSpan(box, 2); var reveal = new Button { Content = VectorIcon("M1,12 Q12,-3 23,12 Q12,27 1,12 Z M8,12 A4,4 0 1 0 16,12 A4,4 0 1 0 8,12", Brush.Parse("#A8D8FF"), 26), Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(0) }; Avalonia.Automation.AutomationProperties.SetName(reveal, "Show " + field.Label.ToLowerInvariant()); reveal.Click += (_, _) => box.PasswordChar = box.PasswordChar == '●' ? '\0' : '●'; Grid.SetColumn(reveal, 1); wrapper.Children.Add(reveal); input = wrapper;
                    }
                    else input = box; box.TextChanged += (_, _) => Changed(box.Text ?? "");
                }
                Avalonia.Automation.AutomationProperties.SetName(input, field.Label); Grid.SetColumn(input, 1); row.Children.Add(input);
                if(settingsCategory==1){row.Children.Remove(input);var unit=new Grid {ColumnDefinitions=new("*,24")};Grid.SetColumn(input,0);unit.Children.Add(input);var suffix=Label("x",18);suffix.HorizontalAlignment=HorizontalAlignment.Right;suffix.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(suffix,1);unit.Children.Add(suffix);Grid.SetColumn(unit,1);row.Children.Add(unit);}
                row.Tag="setting:"+field.Label+" "+field.Key;row.IsVisible=settingsSearch.Length==0||field.Label.Contains(settingsSearch,StringComparison.OrdinalIgnoreCase)||field.Key.Contains(settingsSearch,StringComparison.OrdinalIgnoreCase);
                body.Children.Add(row);
            }
            return OverviewFrame(body, new(25, 14));
        }
        if (settingsCategory == 0)
        {
            groups.Children.Add(Group("Server", [new("Server name", "SessionSettings", "SessionName", dummy ? "The Island" : server.Settings.Name), new("Player limit", "/Script/Engine.GameSession", "MaxPlayers", server.Settings.MaxPlayers.ToString(), "number"), new("Server password", "ServerSettings", "ServerPassword", dummy ? "preview1" : "", "password"), new("Admin password", "ServerSettings", "ServerAdminPassword", dummy ? "preview1" : "", "password")]));
            var gameplay = Group("Gameplay", [new("PvE", "ServerSettings", "ServerPVE", dummy?"True":"False", "bool"), new("Difficulty", "ServerSettings", "DifficultyOffset", "1.0", "number"), new("Maximum difficulty", "ServerSettings", "OverrideOfficialDifficulty", dummy ? "5.0" : "0", "max-difficulty"), new("Third person", "ServerSettings", "AllowThirdPersonPlayer", "True", "bool"), new("Crosshair", "ServerSettings", "ServerCrosshair", "True", "bool")]); gameplay.VerticalAlignment = VerticalAlignment.Top; Grid.SetColumn(gameplay, 2); Grid.SetRowSpan(gameplay, 3); groups.Children.Add(gameplay);
            var saving = Group("Saving", [new("Autosave interval", "ServerSettings", "AutoSavePeriodMinutes", "15", "number")]); Grid.SetRow(saving, 2); groups.Children.Add(saving);
        }
        else
        {
            AddApprovedSettingsCategory(groups,Group,server);
        }
        // Settings groups remain expanded in a single vertical scrolling column.
        // The user's explicit stacking rule overrides the reference's paired cards.
        var stackedGroups=groups.Children.ToArray();groups.Children.Clear();groups.ColumnDefinitions=new("*");groups.RowDefinitions.Clear();
        for(var i=0;i<stackedGroups.Length;i++)
        {
            if(i>0)groups.RowDefinitions.Add(new RowDefinition(16,GridUnitType.Pixel));groups.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var card=stackedGroups[i];Grid.SetColumn(card,0);Grid.SetColumnSpan(card,1);Grid.SetRow(card,i*2);Grid.SetRowSpan(card,1);card.VerticalAlignment=VerticalAlignment.Top;groups.Children.Add(card);
        }
        var scroll = new ScrollViewer { Content = groups, VerticalScrollBarVisibility = settingsCategory==7?ScrollBarVisibility.Disabled:ScrollBarVisibility.Visible, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; if(settingsCategory!=7)ConfigureEditorScrollbar(scroll);
        settingsCategoryBody=groups;
        var pane = new Border { BorderBrush = Brush.Parse("#00DFFF"), BorderThickness = new(2), CornerRadius = new(10), Child = scroll }; Grid.SetColumn(pane, 1); work.Children.Add(pane);
        search.TextChanged += (_, _) => { settingsSearch = search.Text ?? ""; if(settingsCategory==7)engramSearchChanged?.Invoke(settingsSearch);else foreach(var row in groups.GetVisualDescendants().OfType<Control>().Where(c=>c.Tag is string tag&&tag.StartsWith("setting:",StringComparison.Ordinal)))row.IsVisible=settingsSearch.Length==0||((string)row.Tag!).Contains(settingsSearch,StringComparison.OrdinalIgnoreCase);if(settingsCategory==3)creatureSearchChanged?.Invoke(settingsSearch); };
        var footer = new Grid { ColumnDefinitions = new("238,*,164,16,234"), Margin = new(0, 10, 0, 0) }; var footerFrame = new Border { BorderBrush = Brush.Parse("#27536D"), BorderThickness = new(0), Child = footer }; Grid.SetRow(footerFrame, 4); shell.Children.Add(footerFrame);var footerLine=new Border {Height=1,Background=Brush.Parse("#27536D"),Margin=new(238,0,0,0),VerticalAlignment=VerticalAlignment.Top};Grid.SetRow(footerLine,4);shell.Children.Add(footerLine);
        var hint = Label(settingSaveNotice.Length>0?settingSaveNotice:ready ? "ⓘ  Changes apply on next start" : "ⓘ  Loading current settings…", 18); hint.TextWrapping=TextWrapping.NoWrap;hint.TextTrimming=TextTrimming.CharacterEllipsis;ToolTip.SetTip(hint,hint.Text); hint.Foreground = Brush.Parse("#A9DAFF"); hint.VerticalAlignment = VerticalAlignment.Center; hint.Margin = new(16, 0, 0, 0); Grid.SetColumn(hint, 1); footer.Children.Add(hint);
        var revert = EditorButton("⟳  Revert", () => { draft.Clear();repeatedSettingDrafts.Remove(server.Id);launchPreferenceDrafts.Remove(server.Id);networkScopeDrafts.Remove(server.Id);settingSaveNotice="";Render(); }, width: 164); Grid.SetColumn(revert, 2); footer.Children.Add(revert);
        var save = EditorButton("Save Changes", ready && !advancedSaveBusy && !screenSettingsSaveBusy ? () => _ = SaveScreenSettingsAsync(server) : null, true, 234); Grid.SetColumn(save, 4); footer.Children.Add(save);
        var frame = OverviewFrame(shell, new(18, 24, 18, 12)); frame.BorderBrush = Brush.Parse("#639EC9"); frame.BorderThickness=new(1,0,1,1);frame.CornerRadius = new(0,0,13,13); frame.Margin = new(-4, -23, -4, 0); content.Children.Add(frame);
        if (pageScroll is not null) pageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
    }

    private bool screenSettingsSaveBusy;
    private async Task LoadScreenSettingsAsync(ServerDefinition server)
    {
        try
        {
            for(var file=0;file<2;file++)
            {
                var name=file==0?"GameUserSettings.ini":"Game.ini";
                var loaded=IsReviewServer(server.Id)||designPreview
                    ? managerRoot is null?new ServerConfigurationFile(server.Id,name,"Windows",null,[]):SettingsDraft.ReadPreview(managerRoot,server.Id,name)
                    : await client!.CallAsync<ServerConfigurationFile>("server-configuration-read",new ServerConfigurationReadRequest(server.Id,name),TimeSpan.FromSeconds(8),lifetime.Token);
                if(loaded.ServerId!=server.Id||loaded.FileName!=name)throw new InvalidDataException();
                advancedLoadedFiles[(server.Id,file)]=loaded;
            }
            if(!IsReviewServer(server.Id)&&!designPreview)loadedLaunchPreferences[server.Id]=await client!.CallAsync<ServerLaunchPreferencesState>("server-launch-preferences-read",server.Id,TimeSpan.FromSeconds(8),lifetime.Token);
            else if(managerRoot is not null)loadedLaunchPreferences[server.Id]=await new AtomicJsonStore<ServerLaunchPreferencesState>(Path.Combine(managerRoot,"app-settings","solo-settings-dummy","launch.json"),true).LoadAsync(lifetime.Token)??new(server.Id,server.LaunchPreferences,server.NetworkScope);
        }
        catch(Exception error)when(error is IOException or TimeoutException or InvalidOperationException or JsonException or OperationCanceledException){settingSaveNotice="Settings could not be fully loaded. Reload before saving.";}
        finally{if(!lifetime.IsCancellationRequested&&(selectedServer==server.Id||selectedServer==ReviewServerId&&server.Id==ReviewServerId)&&managementTab==1)Render();}
    }

    private async Task SaveScreenSettingsAsync(ServerDefinition server)
    {
        if(screenSettingsSaveBusy||!screenSettingDrafts.TryGetValue(server.Id,out var draft))return;
        var repeated=repeatedSettingDrafts.GetValueOrDefault(server.Id,[]);
        var singleSnapshot=draft.ToDictionary(p=>p.Key,p=>p.Value);
        var repeatedSnapshot=repeated.ToDictionary(p=>p.Key,p=>p.Value.ToList());
        screenSettingsSaveBusy=true;settingSaveNotice="Saving settings…";
        try
        {
            if(launchPreferenceDrafts.TryGetValue(server.Id,out var launchDraft))launchDraft.Tokens();
            foreach(var file in singleSnapshot.Keys.Select(k=>k.File).Concat(repeatedSnapshot.Keys.Select(k=>k.File)).Distinct())
            {
                if(!advancedLoadedFiles.TryGetValue((server.Id,file),out var loaded))throw new InvalidOperationException("Load both configuration files before saving.");
                var changes=singleSnapshot.Where(p=>p.Key.File==file).Select(p=>new SettingsValueEdit(p.Key.Section,p.Key.Key,p.Key.Key=="OverrideOfficialDifficulty"&&p.Value=="_default"?[]:[p.Value])).ToList();
                changes.AddRange(repeatedSnapshot.Where(p=>p.Key.File==file).Select(p=>new SettingsValueEdit(p.Key.Section,p.Key.Key,p.Value)));
                var text=SettingsDraft.Apply(loaded.Text??"",changes);
                if(IsReviewServer(server.Id)||designPreview)
                {
                    if(managerRoot is not null)await SettingsDraft.SavePreviewAsync(managerRoot,loaded,text,lifetime.Token);
                    advancedLoadedFiles[(server.Id,file)]=managerRoot is null?loaded with {Text=text}:SettingsDraft.ReadPreview(managerRoot,server.Id,loaded.FileName);
                    foreach(var pair in singleSnapshot.Where(p=>p.Key.File==file))reviewSavedSettings[pair.Key]=pair.Value;
                }
                else
                {
                    await SaveAdvancedConfigurationAsync(server,file,loaded.FileName,text);
                    if(!lastAdvancedSaveSucceeded){settingSaveNotice="Save was canceled or not confirmed. Remaining drafts are retained.";return;}
                }
                foreach(var pair in singleSnapshot.Where(p=>p.Key.File==file))if(draft.GetValueOrDefault(pair.Key)==pair.Value)draft.Remove(pair.Key);
                foreach(var pair in repeatedSnapshot.Where(p=>p.Key.File==file))if(repeated.TryGetValue(pair.Key,out var current)&&current.SequenceEqual(pair.Value))repeated.Remove(pair.Key);
            }
            if(launchPreferenceDrafts.ContainsKey(server.Id)||networkScopeDrafts.ContainsKey(server.Id))
            {
                var desired=launchPreferenceDrafts.GetValueOrDefault(server.Id,loadedLaunchPreferences.GetValueOrDefault(server.Id)?.Preferences??server.LaunchPreferences);
                var scope=networkScopeDrafts.GetValueOrDefault(server.Id,loadedLaunchPreferences.GetValueOrDefault(server.Id)?.Scope??server.NetworkScope);
                if(!IsReviewServer(server.Id)&&!designPreview)
                {
                    var expected=loadedLaunchPreferences.GetValueOrDefault(server.Id)??await client!.CallAsync<ServerLaunchPreferencesState>("server-launch-preferences-read",server.Id,TimeSpan.FromSeconds(8),lifetime.Token);
                    if(networkScopeDrafts.ContainsKey(server.Id))
                    {
                        if(!OperatingSystem.IsWindows())throw new InvalidOperationException("Automatic network access changes require Windows Firewall setup.");
                        settingSaveNotice="Applying the network choice. Windows may request administrator approval…";
                        Render();
                        if(!await SetupFirewallAsync(server,scope))
                        {settingSaveNotice=networkAddressNotice+" Network choice was not saved; check the pending firewall change before retrying.";return;}
                    }
                    loadedLaunchPreferences[server.Id]=await client!.CallAsync<ServerLaunchPreferencesState>("server-launch-preferences-save",new ServerLaunchPreferencesEdit(expected,desired,scope),TimeSpan.FromSeconds(8),lifetime.Token);
                }
                else
                {
                    loadedLaunchPreferences[server.Id]=new(server.Id,desired,scope);
                    if(managerRoot is not null)await new AtomicJsonStore<ServerLaunchPreferencesState>(Path.Combine(managerRoot,"app-settings","solo-settings-dummy","launch.json"),true).SaveAsync(loadedLaunchPreferences[server.Id],lifetime.Token);
                }
                launchPreferenceDrafts.Remove(server.Id);networkScopeDrafts.Remove(server.Id);
            }
            settingSaveNotice="Changes saved. Configuration applies on the next start; network rules apply immediately.";
            if(!IsReviewServer(server.Id)&&!designPreview)await LoadScreenSettingsAsync(server);
        }
        catch(Exception error)when(error is IOException or TimeoutException or ArgumentException or InvalidOperationException or JsonException or OperationCanceledException){settingSaveNotice=error is ArgumentException?error.Message:"Save did not complete: "+error.Message+" Unsaved settings are retained.";}
        finally{screenSettingsSaveBusy=false;if(!lifetime.IsCancellationRequested)Render();}
    }
    private void ConfigureEditorScrollbar(ScrollViewer viewer, bool arrows = true)
    {
        viewer.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            var nearest=(e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<ScrollViewer>().FirstOrDefault(v=>v.VerticalScrollBarVisibility!=ScrollBarVisibility.Disabled&&v.Extent.Height>v.Viewport.Height);
            if(nearest is not null&&nearest!=viewer)return;
            var maximum = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
            if (maximum <= 0 || e.Delta.Y == 0) return;
            var target = railAnimations.TryGetValue(viewer, out var running) ? running.Target : viewer.Offset.Y;
            e.Handled = true; SmoothScrollTo(viewer, Math.Clamp(target - e.Delta.Y * 100, 0, maximum), true);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        viewer.PointerPressed += (_, _) => { if (railAnimations.Remove(viewer, out var running)) running.Timer.Stop(); };
        viewer.DetachedFromVisualTree += (_, _) => { if (railAnimations.Remove(viewer, out var running)) running.Timer.Stop(); };
        void StyleBar(ScrollBar? bar)
        {
            if (bar is not null)
            {
                bar.Width = 16; bar.AllowAutoHide = false; bar.Template = new FuncControlTemplate<ScrollBar>((s, scope) =>
                {
                    var thumb = new Thumb { MinHeight = 30, Template = new FuncControlTemplate<Thumb>((_, _) => new Border { Background = Brush.Parse("#648BBC"), CornerRadius = new(7) }) }; var track = new Track { Name = "PART_Track", Orientation = Orientation.Vertical, IsDirectionReversed = true, Thumb = thumb };
                    track.Bind(Track.MinimumProperty, new Avalonia.Data.Binding("Minimum") { Source = s }); track.Bind(Track.MaximumProperty, new Avalonia.Data.Binding("Maximum") { Source = s }); track.Bind(Track.ValueProperty, new Avalonia.Data.Binding("Value") { Source = s, Mode = Avalonia.Data.BindingMode.TwoWay }); track.Bind(Track.ViewportSizeProperty, new Avalonia.Data.Binding("ViewportSize") { Source = s }); scope.Register("PART_Track", track);
                    var layout = new Grid { RowDefinitions = new(arrows ? "24,*,24" : "0,*,0") }; var trackFrame = new Border { Background = Brush.Parse("#0F344A"), CornerRadius = new(7), Child = track }; Grid.SetRow(trackFrame, 1); layout.Children.Add(trackFrame);
                    foreach (var direction in arrows ? new[] { -1, 1 } : Array.Empty<int>())
                    {
                        var step = direction; var icon = (Avalonia.Controls.Shapes.Path)VectorIcon(direction < 0 ? "M1,20 L12,2 L23,20 Z" : "M1,2 L12,20 L23,2 Z", Brush.Parse("#B5DEFF"), 15); icon.Fill = Brush.Parse("#B5DEFF");
                        var arrow = new Button { Content = icon, Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(0), HorizontalContentAlignment = HorizontalAlignment.Center }; arrow.Click += (_, _) => SmoothScrollTo(viewer, viewer.Offset.Y + step * 100, true); Grid.SetRow(arrow, direction < 0 ? 0 : 2); layout.Children.Add(arrow);
                    }
                    return layout;
                });
            }
        }
        viewer.TemplateApplied += (_, e) => StyleBar(e.NameScope.Find<ScrollBar>("PART_VerticalScrollBar"));
        StyleBar(viewer.GetVisualDescendants().OfType<ScrollBar>().FirstOrDefault(b=>b.Name=="PART_VerticalScrollBar"));
    }

    private void ApprovedSelectedMods(ServerDefinition server)
    {
        selectedModRowRefresh.Clear();
        var dummy = IsReviewServer(server.Id) || designPreview;
        if (dummy) reviewArtIds ??= server.Mods.ToList();
        var ordered = modOrderDrafts.GetValueOrDefault(server.Id, queuedModOrders.GetValueOrDefault(server.Id, server.Mods));
        var shell = new Grid { RowDefinitions = new("44,16,44,14,*,16,58"), Height = Math.Max(290, Math.Min(630, Bounds.Height > 0 ? Bounds.Height - 390 : 630)) - 28 };
        var toolbar = new Grid { ColumnDefinitions = new("*,206,18,230") };
        var count = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center }; count.Children.Add(Label("Selected Mods", 25, true)); count.Children.Add(new Border { Child = Label(ordered.Count.ToString(), 20), Padding = new(14, 2), Background = Brush.Parse("#0D293B"), BorderBrush = Brush.Parse("#286788"), BorderThickness = new(1), CornerRadius = new(4) }); toolbar.Children.Add(count);
        var check = EditorButton("⟳  Check Updates", () => { foreach (var id in ordered) { modDetailsAttempts.Remove((server.Edition, id)); if (!dummy) _ = LoadRecordedModDetailsAsync(server.Edition, id); } if (dummy) ToolTip.SetTip(count, "Dummy mod list; no workshop requests are made."); }, width: 206); Grid.SetColumn(check, 1); toolbar.Children.Add(check);
        var browse = EditorButton("＋  Browse Workshop", () => { modBrowser = true; Render(); }, true, 230); Grid.SetColumn(browse, 3); toolbar.Children.Add(browse); shell.Children.Add(toolbar);
        var searchRow = new Grid { ColumnDefinitions = new("*,14,154") }; var search = EditorText(modSearch, "Search selected mods"); Avalonia.Automation.AutomationProperties.SetName(search, "Search selected mods"); searchRow.Children.Add(SearchField(search));
        var add = EditorButton("Add by ID", () => { modBrowser = true; Render(); }, width: 154); add.Height = 44; Grid.SetColumn(add, 2); searchRow.Children.Add(add); Grid.SetRow(searchRow, 2); shell.Children.Add(searchRow);
        var rows = new StackPanel { Spacing = 8 };
        var names = new[] { "Structures Plus (S+)", "Dino Storage v2", "Awesome SpyGlass!", "Awesome Teleporters!", "Upgrade Station" };
        var rowControls = new List<(Control Row, string Name, string Id)>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var index = i; var id = ordered[i]; var key = (server.Edition, id); var metadata = modDetails.GetValueOrDefault(key);
            if (!dummy && metadata is null && !modDetailsLoading.Contains(key) && DateTimeOffset.UtcNow - modDetailsAttempts.GetValueOrDefault(key) > TimeSpan.FromMinutes(2)) _ = LoadRecordedModDetailsAsync(server.Edition, id);
            var artIndex = dummy ? reviewArtIds!.IndexOf(id) : -1;
            var name = dummy && artIndex >= 0 && artIndex < names.Length ? names[artIndex] : metadata?.Name ?? "Mod ID: " + id;
            var row = new Grid { ColumnDefinitions = new("38,44,96,*,370,152,28,40"), Height = 73 };
            var grip = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center }; for (var n = 0; n < 3; n++) { var dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 }; for (var d = 0; d < 2; d++) dots.Children.Add(new Border { Width = 4, Height = 4, CornerRadius = new(2), Background = Brush.Parse("#95BADD") }); grip.Children.Add(dots); }
            var drag = new Button { Content = grip, Padding = new(0), Background = Brushes.Transparent, BorderThickness = new(0), HorizontalContentAlignment = HorizontalAlignment.Left }; row.Children.Add(drag);
            var order = Label((i + 1).ToString(), 19); order.Margin = new(12, 0, 0, 0); order.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(order, 1); row.Children.Add(order);
            Control art = dummy && artIndex >= 0 && artIndex < 5 ? ReferenceArtwork("approved-mods", new(166, 508 + artIndex * 82, 71, 65)) : selectedModArtwork.TryGetValue(key, out var bitmap) ? new Image { Source = bitmap, Stretch = Stretch.UniformToFill } : VectorIcon(BoxIcon, Muted, 30);
            if (!dummy && metadata?.PreviewImage is { } picture && !selectedModArtwork.ContainsKey(key) && selectedModArtworkLoading.Add(key)) _ = LoadSelectedModArtworkAsync(key, picture);
            var artwork = new Border { Child = art, Width = 72, Height = 65, CornerRadius = new(4), BorderBrush = Brush.Parse("#2894B6"), BorderThickness = new(1), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(artwork, 2); row.Children.Add(artwork);
            var info = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Margin = new(26, 0, 0, 0) }; var nameLabel = Label(name, 20, true); info.Children.Add(nameLabel); var provider = Label(server.Edition == ArkEdition.ASE ? "◉  Steam Workshop" : "◉  CurseForge", 17); provider.Foreground = Brush.Parse("#B4D7F3"); info.Children.Add(provider); Grid.SetColumn(info, 3); row.Children.Add(info);
            var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Center }; status.Children.Add(VectorIcon(ClockGlyph, Brush.Parse("#A9CAE8"), 24)); var state = Label(modDetailsLoading.Contains(key) ? "Checking…" : modDetailsAttempts.ContainsKey(key) ? metadata is not null ? "Details checked" : "Check unavailable" : "Not checked", 18); state.Foreground = Brush.Parse("#B4D7F3"); status.Children.Add(state); Grid.SetColumn(status, 4); row.Children.Add(status);
            if (metadata is not null) ToolTip.SetTip(status, "Workshop metadata updated " + metadata.UpdatedAt.LocalDateTime.ToString("g") + ". Installed mod version has not been compared.");
            var configure = EditorButton("Configure", () => { configuredMod = id; modConfigurationText = false; Render(); }, width: 152); configure.Height = 44; Grid.SetColumn(configure, 5); row.Children.Add(configure);
            var remove = new Button { Content = VectorIcon("M5,7 L6,23 L18,23 L19,7 M2,6 L22,6 M8,6 L8,2 L16,2 L16,6 M10,10 L10,20 M14,10 L14,20", Brush.Parse("#FF625C"), 26), Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(4) }; Avalonia.Automation.AutomationProperties.SetName(remove, "Remove mod " + id); remove.Click += (_, _) => { modOrderDrafts[server.Id] = ordered.Where(value => value != id).ToList(); replacingModSelections.Add(server.Id); Render(); }; Grid.SetColumn(remove, 7); row.Children.Add(remove);
            void Move(int offset) { if (index + offset < 0 || index + offset >= ordered.Count) return; var draft = ordered.ToList(); (draft[index], draft[index + offset]) = (draft[index + offset], draft[index]); modOrderDrafts[server.Id] = draft; Render(); }
            var up = new MenuItem { Header = "Move earlier", IsEnabled = index > 0 }; up.Click += (_, _) => Move(-1); var down = new MenuItem { Header = "Move later", IsEnabled = index < ordered.Count - 1 }; down.Click += (_, _) => Move(1); drag.ContextMenu = new ContextMenu { ItemsSource = new[] { up, down } }; drag.Click += (_, _) => drag.ContextMenu.Open(drag); Avalonia.Automation.AutomationProperties.SetName(drag, "Move mod " + id);
            double? startY = null;
            drag.AddHandler(PointerPressedEvent,(_,e)=>
            {
                if(!e.GetCurrentPoint(drag).Properties.IsLeftButtonPressed)return;
                startY=e.GetPosition(rows).Y;e.Pointer.Capture(drag);e.Handled=true;
            },Avalonia.Interactivity.RoutingStrategies.Tunnel);
            drag.AddHandler(PointerReleasedEvent,(_,e)=>
            {
                if(startY is not double start)return;
                var offset=(int)Math.Round((e.GetPosition(rows).Y-start)/83);startY=null;e.Pointer.Capture(null);e.Handled=true;
                if(offset!=0)Move(Math.Clamp(offset,-index,ordered.Count-index-1));else drag.ContextMenu.Open(drag);
            },Avalonia.Interactivity.RoutingStrategies.Tunnel);
            var frame = OverviewFrame(row, new(12, 0)); frame.Height = 75; rows.Children.Add(frame); rowControls.Add((frame, name, id)); frame.IsVisible = modSearch.Length == 0 || name.Contains(modSearch, StringComparison.OrdinalIgnoreCase) || id.Contains(modSearch, StringComparison.OrdinalIgnoreCase);
            if (!dummy) selectedModRowRefresh[key] = () =>
            {
                var current = modDetails.GetValueOrDefault(key); nameLabel.Text = current?.Name ?? "Mod ID: " + id;
                state.Text = modDetailsLoading.Contains(key) ? "Checking…" : modDetailsAttempts.ContainsKey(key) ? current is not null ? "Details checked" : "Check unavailable" : "Not checked";
                if (selectedModArtwork.TryGetValue(key, out var readyArt)) artwork.Child = new Image { Source = readyArt, Stretch = Stretch.UniformToFill };
                else if (current?.PreviewImage is { } image && !selectedModArtwork.ContainsKey(key) && selectedModArtworkLoading.Add(key)) _ = LoadSelectedModArtworkAsync(key, image);
                frame.IsVisible = modSearch.Length == 0 || nameLabel.Text.Contains(modSearch, StringComparison.OrdinalIgnoreCase) || id.Contains(modSearch, StringComparison.OrdinalIgnoreCase);
            };
        }
        if (ordered.Count == 0) rows.Children.Add(Label("No mods selected", 22));
        search.TextChanged += (_, _) => { modSearch = search.Text ?? ""; foreach (var item in rowControls) item.Row.IsVisible = modSearch.Length == 0 || (modDetails.GetValueOrDefault((server.Edition, item.Id))?.Name ?? item.Name).Contains(modSearch, StringComparison.OrdinalIgnoreCase) || item.Id.Contains(modSearch, StringComparison.OrdinalIgnoreCase); };
        var rail = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; ConfigureEditorScrollbar(rail,false); Grid.SetRow(rail, 4); shell.Children.Add(rail);
        var footer = new Grid { ColumnDefinitions = new("*,148,18,214"), Margin = new(0, 10, -9, 0) }; var orderHint = Label("⠿  Load order", 17); orderHint.VerticalAlignment = VerticalAlignment.Center; footer.Children.Add(orderHint);
        var revert = EditorButton("⟳  Revert", modOrderDrafts.ContainsKey(server.Id) ? () => { modOrderDrafts.Remove(server.Id); replacingModSelections.Remove(server.Id); Render(); } : null, width: 148); Grid.SetColumn(revert, 1); footer.Children.Add(revert);
        var save = EditorButton("Save Changes", () => { if (dummy) { reviewSavedMods = ordered.ToList(); modOrderDrafts.Remove(server.Id); replacingModSelections.Remove(server.Id); Render(); } else { modOrderConfirmation = true; Render(); } }, true, 214); Grid.SetColumn(save, 3); footer.Children.Add(save); Grid.SetRow(footer, 6); shell.Children.Add(footer);
        content.Children.Add(OverviewFrame(shell, new(14))); if (pageScroll is not null) pageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
    }

    private async Task LoadSelectedModArtworkAsync((ArkEdition Edition, string Id) key, Uri picture)
    {
        var bitmap = await ReadModArtworkAsync(picture, lifetime.Token); if (bitmap is not null) selectedModArtwork[key] = bitmap;
        if (!lifetime.IsCancellationRequested && IsModsViewVisible && !modBrowser && configuredMod is null && selectedModRowRefresh.TryGetValue(key, out var update)) update();
    }

    private void RenderReviewModDestination(ServerDefinition server, bool browser)
    {
        var body = new StackPanel { Spacing = 18 }; body.Children.Add(EditorButton("Back to selected mods", () => { configuredMod = null; modBrowser = false; Render(); }));
        body.Children.Add(Label(browser ? "Add by ID" : "Mod configuration", 28, true));
        if (browser)
        {
            var id = EditorText(lookupId, "Enter mod ID"); Avalonia.Automation.AutomationProperties.SetName(id, "Mod ID"); id.TextChanged += (_, _) => lookupId = id.Text ?? ""; body.Children.Add(id);
            var notice = Label("Preview changes stay in the dummy list.", 18); body.Children.Add(notice);
            body.Children.Add(EditorButton("Add to selected mods", () => { if (!ulong.TryParse(lookupId.Trim(), out var parsed) || parsed == 0) { notice.Text = "Enter a positive numeric mod ID."; return; } var list = modOrderDrafts.GetValueOrDefault(server.Id, server.Mods).ToList(); if (list.Contains(parsed.ToString())) { notice.Text = "This ID is already selected."; return; } list.Add(parsed.ToString()); modOrderDrafts[server.Id] = list; modBrowser = false; Render(); }, true));
        }
        else body.Children.Add(Label("The UI dummy has no mod configuration files.", 18));
        content.Children.Add(OverviewFrame(body));
    }
}
