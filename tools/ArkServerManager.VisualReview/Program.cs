using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.VisualTree;
using ArkServerManager.Desktop;

if(args is ["idle-manager-for-update",var dataRoot])
{
    var result=await new ArkServerManager.Core.LocalManagerClient(dataRoot).CallAsync<ArkServerManager.Core.IdleManagerShutdownResult>("app-update-idle-shutdown",null,TimeSpan.FromSeconds(10));Console.WriteLine(result.Message);return result.Accepted?0:3;
}
if (args is not [var screen, var destination, var widthText, var heightText]
    || !int.TryParse(widthText, out var width) || !int.TryParse(heightText, out var height)
    || width < (screen == "cluster-joining-confirmation" ? 500 : screen is "existing-server-registration" or "startup-selection" or "creation-mod-details" or "saved-configuration" or "configuration-resolution" or "configuration-resolution-missing" || screen.StartsWith("cluster-shared-settings", StringComparison.Ordinal) ? 600 : 900)
    || width > 2560 || height < (screen == "cluster-joining-confirmation" ? 400 : 640) || height > 1600)
{
    Console.Error.WriteLine("Choose a review screen, PNG path, width and height."); return 2;
}
AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions
    { UseHeadlessDrawing = false }).SetupWithoutStarting();
if(screen is "network-access-save-check" or "startup-dispatch-check")
{
    var check=screen=="startup-dispatch-check"?MainWindow.CheckStartupDispatchForReviewAsync():MainWindow.CheckNetworkAccessSaveForReviewAsync();var until=DateTime.UtcNow.AddSeconds(20);
    while(!check.IsCompleted){Dispatcher.UIThread.RunJobs();if(DateTime.UtcNow>until)throw new TimeoutException("Network settings action timed out.");Thread.Sleep(10);}
    check.GetAwaiter().GetResult();Console.WriteLine(screen=="startup-dispatch-check"?"Start/restart dispatch without preparing or depending on the backup helper; no server launched.":"Network settings apply local/internet firewall rules, persist verified choices, and retain failed drafts.");return 0;
}
Window window = screen.StartsWith("cluster-shared-settings", StringComparison.Ordinal) ? MainWindow.CreateSharedSettingsDesignReview()
    : screen == "existing-server-registration" ? MainWindow.CreateExistingRegistrationDesignReview()
    : screen == "cluster-joining-confirmation" ? MainWindow.CreateJoiningConfirmationDesignReview()
    : screen == "mod-configuration-files" ? MainWindow.CreateModConfigurationFilesDesignReview()
    : screen == "startup-selection" ? MainWindow.CreateServerStartupDesignReview()
    : screen == "creation-mod-details" ? MainWindow.CreateCreationModDetailsDesignReview()
    : screen == "saved-configuration" ? MainWindow.CreateSavedConfigurationDesignReview()
    : screen is "configuration-resolution" or "configuration-resolution-missing" ? MainWindow.CreateConfigurationResolutionDesignReview(screen == "configuration-resolution-missing")
    : screen == "installer-components" ? new InstallerPreviewWindow(componentsDesignPreview: true)
    : screen == "app-launch-update" ? new LaunchUpdateWindow(null, null, designReview: true) : MainWindow.CreateDesignReview(screen);
window.Width = width; window.Height = height; window.Show();
Dispatcher.UIThread.RunJobs();
if(screen=="approved-management-actions")
{
    var reviewed=(MainWindow)window;
    void Pump(Func<bool> ready){var until=DateTime.UtcNow.AddSeconds(12);while(!ready()){Dispatcher.UIThread.RunJobs();window.UpdateLayout();if(DateTime.UtcNow>until)throw new TimeoutException("Management page action did not complete.");Thread.Sleep(10);}Dispatcher.UIThread.RunJobs();window.UpdateLayout();}
    void Click(Window target,string name){target.GetVisualDescendants().OfType<Button>().Single(b=>Avalonia.Automation.AutomationProperties.GetName(b)==name).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();target.UpdateLayout();}
    Window Modal(string title){Pump(()=>window.OwnedWindows.Any(w=>w.Title==title));return window.OwnedWindows.Single(w=>w.Title==title);}
    bool HasText(string text)=>window.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text==text);
    Pump(()=>reviewed.ManagementPageReadyForReview);var serverDirectory=reviewed.ManagementFixtureDirectoryForReview;
    Click(window,"Create Backup");var create=Modal("Create Backup");create.GetVisualDescendants().OfType<TextBox>().Single().Text="Page action backup";Click(create,"Create Backup");Pump(()=>reviewed.ManagementPageReadyForReview&&HasText("Page action backup"));
    var index=System.Text.Json.JsonSerializer.Deserialize<ArkServerManager.Core.ManagedBackupInventory>(File.ReadAllText(Path.Combine(serverDirectory,"backups","managed","index.json")))!;if(index.Entries.Count!=1)throw new InvalidOperationException("Create Backup did not persist its archive.");
    var search=window.GetVisualDescendants().OfType<TextBox>().Single();search.Text="missing";Dispatcher.UIThread.RunJobs();if(!HasText("No matching backups"))throw new InvalidOperationException("Backup search failed.");search.Text="";Dispatcher.UIThread.RunJobs();Click(window,"Backup");
    Click(window,"Schedule");var schedule=Modal("Backup Schedule");schedule.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked=true;Click(schedule,"Save Changes");Pump(()=>reviewed.ManagementPageReadyForReview&&HasText("Backup schedule saved."));
    var world=Path.Combine(serverDirectory,"ShooterGame","Saved","SavedArks","review-world.txt");File.WriteAllText(world,"changed after backup");Click(window,"Restore");Click(Modal("Restore backup?"),"Restore");Pump(()=>reviewed.ManagementPageReadyForReview&&File.ReadAllText(world)!="changed after backup");
    Click(window,"Delete backup Page action backup");Click(Modal("Delete backup?"),"Delete");Pump(()=>reviewed.ManagementPageReadyForReview&&HasText("No backups yet"));
    Click(window,"Players");Pump(()=>reviewed.ManagementPlayerReadyForReview);Click(window,"Select player SurvivorTwo");if(!window.GetVisualDescendants().OfType<ComboBox>().Any(c=>Equals(c.SelectedItem,"Admin")))throw new InvalidOperationException("Player selection did not update details.");
    Click(window,"Select player Brice");Click(window,"Ban");Pump(()=>reviewed.ManagementPlayerReadyForReview&&HasText("Player action completed."));Click(window,"Banned");if(!HasText("Brice")||window.GetVisualDescendants().OfType<Button>().Any(b=>Avalonia.Automation.AutomationProperties.GetName(b)=="Select player SurvivorTwo"))throw new InvalidOperationException("Banned filter failed.");
    Click(window,"Allowlist");Pump(()=>reviewed.ManagementPlayerReadyForReview&&HasText("Allowlist saved. Applies on next start."));if(!File.ReadAllText(Path.Combine(serverDirectory,"ShooterGame","Binaries",OperatingSystem.IsWindows()?"Win64":"Linux","PlayersExclusiveJoinList.txt")).Contains("76561198000004821"))throw new InvalidOperationException("Player allowlist button did not save.");
    Click(window,"Console");var header=window.GetVisualDescendants().OfType<TextBlock>().Single(t=>t.Text=="The Island");if(header.GetVisualAncestors().OfType<Control>().Any(c=>c.Opacity<.99))throw new InvalidOperationException("Persistent header faded.");
    var logs=window.GetVisualDescendants().OfType<TextBox>().Single(t=>Avalonia.Automation.AutomationProperties.GetName(t)=="Search logs");logs.Text="no-such-log";Dispatcher.UIThread.RunJobs();if(!HasText("No log lines to display."))throw new InvalidOperationException("Log search failed.");logs.Text="";Dispatcher.UIThread.RunJobs();
    window.GetVisualDescendants().OfType<ComboBox>().Single().SelectedItem="Error";Dispatcher.UIThread.RunJobs();if(!HasText("No log lines to display."))throw new InvalidOperationException("Log level filter failed.");window.GetVisualDescendants().OfType<ComboBox>().Single().SelectedItem="All levels";Dispatcher.UIThread.RunJobs();
    Click(window,"Clear View");if(!HasText("No log lines to display."))throw new InvalidOperationException("Clear View failed.");Click(window,"RCON");if(!HasText("No RCON responses yet."))throw new InvalidOperationException("RCON tab failed.");if(window.GetVisualDescendants().OfType<Button>().Single(b=>Avalonia.Automation.AutomationProperties.GetName(b)=="Send").IsEnabled)throw new InvalidOperationException("Stopped dummy accepted live command.");
    Console.WriteLine("Verified page actions: real dummy archive create/restore/delete, schedule persistence, search/sort, player selection/ban/filter/allowlist files, console search/levels/clear/RCON, stopped command state, and persistent header.");
}
if(screen=="approved-cluster-backups-actions")((MainWindow)window).VerifyClusterBackupsActionsForReview();
if(screen=="approved-cluster-transfers-actions")((MainWindow)window).VerifyClusterTransfersActionsForReview();
if(screen=="approved-cluster-activity-actions")((MainWindow)window).VerifyClusterActivityActionsForReview();
if(screen=="approved-settings-bindings")
{
    var bindings=((MainWindow)window).CaptureSettingsBindingsForReview();
    var expected=System.Text.Json.JsonSerializer.Deserialize<List<MainWindow.SettingsBindingReview>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"documented-settings-bindings.json")))!;
    foreach(var actual in bindings.Where(b=>b.Kind is not ("readonly" or "launch-bool")))
        if(!expected.Any(e=>e.File==actual.File&&e.Section==actual.Section&&e.Key==actual.Key))throw new InvalidOperationException($"Undocumented settings binding: {actual.Label}: {actual.File}/{actual.Section}/{actual.Key}");
    foreach(var documented in expected)
        if(!bindings.Any(b=>b.File==documented.File&&b.Section==documented.Section&&b.Key==documented.Key))throw new InvalidOperationException("Documented control is missing: "+documented.Key);
    File.WriteAllText(Path.ChangeExtension(destination,".json"),System.Text.Json.JsonSerializer.Serialize(bindings,new System.Text.Json.JsonSerializerOptions {WriteIndented=true}));
    Console.WriteLine($"Checked {bindings.Count} production field descriptors against the documented file, section and key inventory.");Dispatcher.UIThread.RunJobs();
    var reviewed=(MainWindow)window;var changed=0;
    void SelectCategory(string name){window.GetVisualDescendants().OfType<Button>().Where(b=>Avalonia.Automation.AutomationProperties.GetName(b)==name).OrderBy(b=>b.Width==228?0:1).First().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();}
    foreach(var category in new[]{"General","Rates","Players","Creatures","Breeding","Structures","World","Network"})
    {
        SelectCategory(category);var wanted=new List<(MainWindow.SettingsBindingReview Binding,string Value)>();
        foreach(var binding in reviewed.CurrentSettingsBindingsForReview().Where(b=>b.Kind is not ("readonly" or "launch-bool")))
        {
            var row=window.GetVisualDescendants().OfType<Grid>().SingleOrDefault(c=>c.Tag is string tag&&tag=="setting:"+binding.Label+" "+binding.Key);if(row is null)continue;
            string value;
            if(binding.Kind is "number" or "integer" or "stat")
            {
                var numeric=row.GetVisualDescendants().OfType<NumericUpDown>().Single();var next=binding.Key=="DifficultyOffset"?0.75m:binding.Key.Contains("Port")?27877m:binding.Kind=="integer"||binding.Key=="MaxPlayers"?12m:2.5m;numeric.Value=next;value=next.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else if(binding.Kind is "bool" or "bool-inverted" or "max-difficulty")
            {
                var toggle=row.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Single();toggle.IsChecked=false;toggle.IsChecked=true;value=binding.Kind=="max-difficulty"?"5.0":binding.Kind=="bool-inverted"?"False":"True";
            }
            else{var field=row.GetVisualDescendants().OfType<TextBox>().Single();field.Text="audit-fixture";value="audit-fixture";}
            Dispatcher.UIThread.RunJobs();wanted.Add((binding,value));
        }
        SelectCategory("Save Changes");
        foreach(var (binding,value) in wanted){if(reviewed.SavedSettingForReview(binding.File,binding.Section,binding.Key)!=value)throw new InvalidOperationException("Control did not save the documented value: "+binding.Key+" wanted "+value+" actual "+reviewed.SavedSettingForReview(binding.File,binding.Section,binding.Key)+" notices "+string.Join(" | ",window.GetVisualDescendants().OfType<TextBlock>().Select(t=>t.Text).Where(t=>t is not null)));changed++;}
    }
    const string gameSection="/Script/ShooterGame.ShooterGameMode";
    SelectCategory("Engrams");foreach(var item in ArkServerManager.Core.DocumentedSettings.VanillaEngrams){window.GetVisualDescendants().OfType<TextBox>().Single(t=>Avalonia.Automation.AutomationProperties.GetName(t)=="Search settings").Text=item.ClassName;Dispatcher.UIThread.RunJobs();window.UpdateLayout();window.GetVisualDescendants().OfType<Button>().Single(b=>Avalonia.Automation.AutomationProperties.GetName(b)=="Override").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));}window.GetVisualDescendants().OfType<TextBox>().Single(t=>Avalonia.Automation.AutomationProperties.GetName(t)=="Search settings").Text="";SelectCategory("Save Changes");
    var savedEngrams=reviewed.SavedSettingValuesForReview(1,gameSection,"OverrideNamedEngramEntries");foreach(var engram in ArkServerManager.Core.DocumentedSettings.VanillaEngrams)if(!savedEngrams.Any(raw=>new ArkServerManager.Core.IniTuple(raw).Get("EngramClassName")==engram.ClassName))throw new InvalidOperationException("Engram control saved the wrong class: "+engram.Name);
    window.GetVisualDescendants().OfType<TextBox>().Single(t=>Avalonia.Automation.AutomationProperties.GetName(t)=="Search settings").Text="";Dispatcher.UIThread.RunJobs();
    reviewed.SeedCreatureOverrideForReview();SelectCategory("Creatures");window.UpdateLayout();
    var creaturePane=window.GetVisualDescendants().OfType<ScrollViewer>().Single(v=>v.Content is Grid g&&g.Margin.Left==15);
    creaturePane.Offset=new(0,creaturePane.Extent.Height-creaturePane.Viewport.Height);Dispatcher.UIThread.RunJobs();window.UpdateLayout();
    window.GetVisualDescendants().OfType<TextBox>().Single(t=>Avalonia.Automation.AutomationProperties.GetName(t)=="Search creature overrides").Text="Rex_Character_BP_C";
    Dispatcher.UIThread.RunJobs();window.UpdateLayout();
    var spawnRow=window.GetVisualDescendants().OfType<Grid>().Single(g=>g.Children.OfType<TextBlock>().Any(t=>t.Text=="Rex"));
    spawnRow.GetVisualDescendants().OfType<NumericUpDown>().Single().Value=2.5m;spawnRow.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked=false;Dispatcher.UIThread.RunJobs();SelectCategory("Save Changes");
    var savedCreature=reviewed.SavedSettingForReview(1,gameSection,"DinoSpawnWeightMultipliers")!;var savedTuple=new ArkServerManager.Core.IniTuple(savedCreature);if(savedTuple.Get("SpawnWeightMultiplier")!="2.5"||savedTuple.Get("SpawnLimitPercentage")!="0.2"||reviewed.SavedSettingForReview(1,gameSection,"PreventDinoTameClassNames")!="\"Rex_Character_BP_C\"")throw new InvalidOperationException("Creature override did not retain its limits and save taming.");
    SelectCategory("Structures");var destroy=window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Single(b=>Avalonia.Automation.AutomationProperties.GetName(b)=="Auto-destroy decayed structures");destroy.IsChecked=true;Dispatcher.UIThread.RunJobs();SelectCategory("Save Changes");if(!reviewed.SavedLaunchForReview().AutoDestroyStructures||reviewed.SavedSettingForReview(0,"ServerSettings","AutoDestroyOldStructuresMultiplier")!="1.0")throw new InvalidOperationException("Auto-destroy did not save its required flag and timer.");
    SelectCategory("Advanced");window.GetVisualDescendants().OfType<TextBox>().Single(t=>Avalonia.Automation.AutomationProperties.GetName(t)=="Extra launch arguments").Text="-culture=en";window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Single(t=>Avalonia.Automation.AutomationProperties.GetName(t)=="Server logging").IsChecked=true;Dispatcher.UIThread.RunJobs();SelectCategory("Save Changes");var argsReview=new List<string>{"TheIsland?listen"};reviewed.SavedLaunchForReview().Apply(argsReview);if(!argsReview.Contains("-servergamelog")||!argsReview.Contains("-culture=en")||!argsReview.Contains("-AutoDestroyStructures")||argsReview.Contains("-USEALLAVAILABLECORES"))throw new InvalidOperationException("Launch controls did not save the documented arguments.");
    SelectCategory("General");Console.WriteLine($"Exercised and verified {changed} scalar controls, four engram overrides, creature spawn/taming, and launch switches through Save Changes.");
}
if(screen=="approved-creature-actions")
{
    var reviewed=(MainWindow)window;reviewed.SeedCreatureOverrideForReview();
    void Click(string name){window.GetVisualDescendants().OfType<Button>().Single(b=>Avalonia.Automation.AutomationProperties.GetName(b)==name).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();window.UpdateLayout();}
    Click("Creatures");
    var creaturePane=window.GetVisualDescendants().OfType<ScrollViewer>().Single(v=>v.Content is Grid g&&g.Margin.Left==15);
    creaturePane.Offset=new(0,creaturePane.Extent.Height-creaturePane.Viewport.Height);Dispatcher.UIThread.RunJobs();window.UpdateLayout();
    var list=window.GetVisualDescendants().OfType<ListBox>().Single();if(list.ItemCount<519||list.GetRealizedContainers().Count()>20)throw new InvalidOperationException("Creature catalog is incomplete or not virtualized.");
    var search=window.GetVisualDescendants().OfType<TextBox>().Single(t=>Avalonia.Automation.AutomationProperties.GetName(t)=="Search creature overrides");
    foreach(var (query,name,value) in new[]{("Rex_Character_BP_C","Rex",2.5m),("Rhynio_Character_BP_C","Rhyniognatha",3.5m)})
    {
        search.Text=query;Dispatcher.UIThread.RunJobs();window.UpdateLayout();
        window.GetVisualDescendants().OfType<NumericUpDown>().Single(n=>Avalonia.Automation.AutomationProperties.GetName(n)==name+" spawn weight").Value=value;
        window.GetVisualDescendants().OfType<CheckBox>().Single(c=>Avalonia.Automation.AutomationProperties.GetName(c)==name+" taming allowed").IsChecked=false;Dispatcher.UIThread.RunJobs();
    }
    Click("Save Changes");var saved=reviewed.SavedSettingValuesForReview(1,"/Script/ShooterGame.ShooterGameMode","DinoSpawnWeightMultipliers");
    if(saved.Count!=2)throw new InvalidOperationException("Unchanged catalog entries must not create overrides.");
    var rex=new ArkServerManager.Core.IniTuple(saved.Single(r=>new ArkServerManager.Core.IniTuple(r).Get("DinoNameTag")=="Rex"));if(rex.Get("SpawnWeightMultiplier")!="2.5"||rex.Get("SpawnLimitPercentage")!="0.2")throw new InvalidOperationException("Existing creature spawn limits were lost.");
    var rhynio=new ArkServerManager.Core.IniTuple(saved.Single(r=>new ArkServerManager.Core.IniTuple(r).Get("DinoNameTag")=="Rhynio"));if(rhynio.Get("SpawnWeightMultiplier")!="3.5")throw new InvalidOperationException("New creature override did not save.");
    var blocked=reviewed.SavedSettingValuesForReview(1,"/Script/ShooterGame.ShooterGameMode","PreventDinoTameClassNames");if(!blocked.Contains("\"Rex_Character_BP_C\"")||!blocked.Contains("\"Rhynio_Character_BP_C\""))throw new InvalidOperationException("Creature taming class mapping is wrong.");
    search=window.GetVisualDescendants().OfType<TextBox>().Single(t=>Avalonia.Automation.AutomationProperties.GetName(t)=="Search creature overrides");search.Text="";Dispatcher.UIThread.RunJobs();window.UpdateLayout();
    var outer=window.GetVisualDescendants().OfType<ScrollViewer>().Single(v=>v.Content is Grid g&&g.Margin.Left==15);outer.Offset=new(0,outer.Extent.Height-outer.Viewport.Height);Dispatcher.UIThread.RunJobs();window.UpdateLayout();
    list=window.GetVisualDescendants().OfType<ListBox>().Single();list.SelectedIndex=0;Dispatcher.UIThread.RunJobs();
    if(list.GetRealizedContainers().Any(c=>c.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First().Background!=Avalonia.Media.Brushes.Transparent))throw new InvalidOperationException("Override rows gained a selection background.");
    Console.WriteLine("Verified 519 creature rows, virtualization, search, new and existing spawn/taming saves, preserved limits, and transparent selection.");
}
if(screen=="approved-engram-actions")
{
    window.UpdateLayout();Dispatcher.UIThread.RunJobs();window.UpdateLayout();
    var list=window.GetVisualDescendants().OfType<ListBox>().Single();
    if(list.ItemCount<580)throw new InvalidOperationException("Engram catalog is incomplete.");
    if(list.GetRealizedContainers().Count()>20)throw new InvalidOperationException("Engram catalog is not virtualized.");
    var search=window.GetVisualDescendants().OfType<TextBox>().Single(t=>Avalonia.Automation.AutomationProperties.GetName(t)=="Search settings");
    search.Text="EngramEntry_Saddle_Rhynio_C";Dispatcher.UIThread.RunJobs();window.UpdateLayout();
    if(list.ItemCount!=1)throw new InvalidOperationException("Full-catalog search failed.");
    var number=window.GetVisualDescendants().OfType<NumericUpDown>().Single(n=>Avalonia.Automation.AutomationProperties.GetName(n)=="Rhyniognatha Saddle level");number.Value=91;
    window.GetVisualDescendants().OfType<Button>().Single(b=>Avalonia.Automation.AutomationProperties.GetName(b)=="Override").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    window.GetVisualDescendants().OfType<Button>().Single(b=>Avalonia.Automation.AutomationProperties.GetName(b)=="Save Changes").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();window.UpdateLayout();
    var raw=((MainWindow)window).SavedSettingValuesForReview(1,"/Script/ShooterGame.ShooterGameMode","OverrideNamedEngramEntries").Single();
    var tuple=new ArkServerManager.Core.IniTuple(raw);if(tuple.Get("EngramClassName")!="EngramEntry_Saddle_Rhynio_C"||tuple.Get("EngramLevelRequirement")!="91")throw new InvalidOperationException("Late catalog item did not save.");
    search=window.GetVisualDescendants().OfType<TextBox>().Single(t=>Avalonia.Automation.AutomationProperties.GetName(t)=="Search settings");search.Text="";Dispatcher.UIThread.RunJobs();window.UpdateLayout();
    list=window.GetVisualDescendants().OfType<ListBox>().Single();list.ScrollIntoView(list.ItemsSource!.Cast<object>().Last());Dispatcher.UIThread.RunJobs();window.UpdateLayout();
    Console.WriteLine("Verified searchable full catalog, virtualized scrolling and Rhyniognatha override through Save Changes.");
}
if(screen=="dashboard-fit-actions")
{
    var page=window.GetVisualDescendants().OfType<ScrollViewer>().Single(s=>s.HorizontalScrollBarVisibility==Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled&&s.Content is StackPanel);
    if(page.VerticalScrollBarVisibility!=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled||page.Extent.Height>page.Viewport.Height+1)throw new InvalidOperationException($"Dashboard exceeds its page: {page.Extent.Height} > {page.Viewport.Height}.");
    Console.WriteLine("Both overflowing dashboard rows fit vertically without page scrolling.");
}
if (screen == "approved-editor-actions")
{
    Button Named(string name) => window.GetVisualDescendants().OfType<Button>().Single(b=>Avalonia.Automation.AutomationProperties.GetName(b)==name);
    void Click(string name) { Named(name).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs(); }
    TextBox Field(string name) => window.GetVisualDescendants().OfType<TextBox>().Single(b=>Avalonia.Automation.AutomationProperties.GetName(b)==name);
    Field("Server name").Text="Saved dummy name";Dispatcher.UIThread.RunJobs();Click("Save Changes");
    if(Field("Server name").Text!="Saved dummy name")throw new InvalidOperationException("Dummy settings save lost the draft.");
    Field("Server name").Text="Unsaved dummy name";Dispatcher.UIThread.RunJobs();Click("Revert");
    if(Field("Server name").Text!="Saved dummy name")throw new InvalidOperationException("Revert did not restore the saved dummy settings.");
    Field("Search settings").Text="Player limit";Dispatcher.UIThread.RunJobs();
    if(Field("Server name").IsEffectivelyVisible)throw new InvalidOperationException("Settings search did not filter unrelated fields.");
    Field("Search settings").Text="";
    Click("Mods");
    var header=window.GetVisualDescendants().OfType<TextBlock>().Single(t=>t.Text=="The Island");
    if(header.GetVisualAncestors().OfType<Control>().Any(c=>c.Opacity<0.99))throw new InvalidOperationException("Tab switching faded the persistent header.");
    if(window.GetVisualDescendants().OfType<Button>().Where(b=>new[]{"Overview","Settings","Mods","Backups","Players","Console"}.Contains(Avalonia.Automation.AutomationProperties.GetName(b))).Any(b=>b.FontWeight!=Avalonia.Media.FontWeight.Normal))throw new InvalidOperationException("A selected tab was bolded.");
    Click("Add by ID");Field("Mod ID").Text="99999";Dispatcher.UIThread.RunJobs();Click("Add to selected mods");Click("Save Changes");Click("Settings");Click("Mods");
    if(!window.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="Mod ID: 99999"))throw new InvalidOperationException("Saved dummy mod selection was lost on tab navigation.");
    Click("Remove mod 99999");Click("Revert");
    if(!window.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text=="Mod ID: 99999"))throw new InvalidOperationException("Mod revert failed to restore the saved dummy list.");
    Console.WriteLine("Settings draft/save/revert/search and dummy mod add/save/navigation/remove/revert checked without server writes.");
}
if (screen == "approved-solo-actions")
{
    void ClickNamed(string name) { window.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == name).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs(); }
    foreach (var (tile, expected) in new[] { ("Open server settings", "Settings"), ("Open server backups", "Backups"), ("Open server activity", "Recent Activity") })
    {
        ClickNamed(tile);
        if (expected == "Settings" ? !window.GetVisualDescendants().OfType<TextBox>().Any(t => Avalonia.Automation.AutomationProperties.GetName(t) == "Server name") : !window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == expected)) throw new InvalidOperationException("Overview tile did not reach " + expected);
        ClickNamed("Overview");
        if (!window.GetVisualDescendants().OfType<Button>().Any(b => Avalonia.Automation.AutomationProperties.GetName(b) == tile)) throw new InvalidOperationException("Overview return lost tile " + tile);
    }
    ClickNamed("Start Server");
    if (!window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Running")) throw new InvalidOperationException("UI-only dummy start did not update its status.");
    ClickNamed("Stop");
    if (!window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Stopped")) throw new InvalidOperationException("UI-only dummy stop did not update its status.");
    Console.WriteLine("Three overview panel routes and UI-only dummy actions checked; no server process or files created.");
}
if (screen == "approved-cluster-actions")
{
    var rail = window.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.HorizontalScrollBarVisibility != Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
    var right = window.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Scroll maps right");
    if (!right.IsEnabled) throw new InvalidOperationException("Overflowing cluster rail has no enabled right arrow.");
    right.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    var observations = new List<double>();
    var probe = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(12) };
    probe.Tick += (_, _) => observations.Add(rail.Offset.X); probe.Start();
    using (var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(400))) Dispatcher.UIThread.MainLoop(deadline.Token);
    probe.Stop(); Dispatcher.UIThread.RunJobs();
    for (var i = 1; i < observations.Count; i++) if (observations[i] + 0.1 < observations[i - 1] || observations[i] > rail.Extent.Width - rail.Viewport.Width + 0.1) throw new InvalidOperationException("Cluster scrolling reversed or overshot.");
    if (rail.Offset.X <= 0) throw new InvalidOperationException("Cluster arrow did not scroll.");
    if (window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Partial")) throw new InvalidOperationException("Redundant cluster Partial badge remains.");
    if(window.GetVisualDescendants().OfType<Button>().Any(b=>Avalonia.Automation.AutomationProperties.GetName(b) is "Open cluster transfers" or "Open cluster backups"))throw new InvalidOperationException("Removed cluster shortcuts remain.");
    var left=window.GetVisualDescendants().OfType<Button>().Single(b=>Avalonia.Automation.AutomationProperties.GetName(b)=="Scroll maps left");
    var leftPosition=left.TranslatePoint(new Point(),window)!.Value;var rightPosition=right.TranslatePoint(new Point(),window)!.Value;
    if(Math.Abs(leftPosition.Y-rightPosition.Y)>.1||Math.Abs(rightPosition.X-leftPosition.X-left.Bounds.Width-12)>.1||left.Bounds.Size!=right.Bounds.Size)throw new InvalidOperationException("Cluster arrow buttons are not evenly spaced and aligned.");
    foreach(var button in new[]{left,right}){var icon=(Control)button.Content!;var center=icon.TranslatePoint(new Point(icon.Bounds.Width/2,icon.Bounds.Height/2),button)!.Value;if(Math.Abs(center.X-button.Bounds.Width/2)>.5||Math.Abs(center.Y-button.Bounds.Height/2)>.5)throw new InvalidOperationException("A cluster chevron is not centered.");}
    foreach (var (tile, expected) in new[] { ("Transfers", "Uploads"), ("Backups", "Backup Schedule") })
    {
        window.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == tile).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        if (!window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains(expected, StringComparison.OrdinalIgnoreCase) == true)) throw new InvalidOperationException("Cluster tile did not reach its destination.");
        window.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Overview").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
    }
    Console.WriteLine("Cluster tabs, shortcut removal, centered arrows with a 12-pixel gap, bounded smooth scrolling and redundant badge removal checked.");
}
if (screen == "window-controls")
{
    MenuItem Caption(string name) => window.GetVisualDescendants().OfType<Button>().Single(button => Avalonia.Automation.AutomationProperties.GetName(button) == "ARK Server Manager menu").ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, name));
    var maximize = Caption("Maximize or restore window");
    maximize.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent)); Dispatcher.UIThread.RunJobs();
    if (window.WindowState != WindowState.Maximized) throw new InvalidOperationException("Maximize action did not change window state.");
    maximize.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent)); Dispatcher.UIThread.RunJobs();
    if (window.WindowState != WindowState.Normal) throw new InvalidOperationException("Restore action did not change window state.");
    Caption("Minimize window").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent)); Dispatcher.UIThread.RunJobs();
    if (window.WindowState != WindowState.Minimized) throw new InvalidOperationException("Minimize action did not change window state.");
    window.WindowState = WindowState.Normal; Dispatcher.UIThread.RunJobs();
}
if (screen is "creation-mods-invalid" or "creation-mods-review")
{
    var editor = window.GetVisualDescendants().OfType<TextBox>().Single(box => box.PlaceholderText == "Mod IDs");
    editor.Text = screen == "creation-mods-invalid" ? "not-a-mod-id" : "2182894352, 731604991";
    Dispatcher.UIThread.RunJobs();
    var next = window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Continue  ›"));
    next.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    Dispatcher.UIThread.RunJobs();
    var text = window.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToArray();
    var expected = screen == "creation-mods-invalid" ? "Enter distinct positive numeric mod IDs in load order." : "2 mods";
    if (!text.Contains(expected)) throw new InvalidOperationException("Creation mod review did not reach the expected production state.");
}
if (screen == "cluster-shared-settings-advanced")
{
    window.GetVisualDescendants().OfType<Expander>().Single().IsExpanded = true;
    Dispatcher.UIThread.RunJobs();
}
if (screen == "network-connections")
{
    window.GetVisualDescendants().OfType<Expander>().Single(item => Equals(item.Header, "Local network connections")).IsExpanded = true;
    Dispatcher.UIThread.RunJobs();
    var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().First();
    scroll.Offset = new Vector(0, scroll.Extent.Height); Dispatcher.UIThread.RunJobs();
}
if (screen is "asa-mod-details" or "preset-edit" or "server-welcome" or "shared-cluster-history" or "coordinated-history" or "coordinated-point" or "restore-recovery" or "shared-restore-recovery" or "cluster-restore-recovery" or "coordinated-unavailable")
{
    var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().First();
    scroll.Offset = new Vector(0, scroll.Extent.Height);
    Dispatcher.UIThread.RunJobs();
}
if (window is MainWindow main) main.CompleteVisualReviewFrame();
AvaloniaHeadlessPlatform.ForceRenderTimerTick();
using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No offscreen frame was rendered.");
var path = Path.GetFullPath(destination); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
using (var output = File.Create(path)) frame.Save(output, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
if (screen == "mod-configuration-files")
{
    foreach (var file in new[] { "GameUserSettings.ini", "Game.ini" })
    {
        window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Edit " + file))
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        var expectedSection = file == "Game.ini" ? "[/Script/ShooterGame.ShooterGameMode]" : "[ServerSettings]";
        if (!window.GetVisualDescendants().OfType<TextBox>().Any(editor => editor.Text?.Contains(expectedSection, StringComparison.Ordinal) == true))
            throw new InvalidOperationException("Mod configuration did not open the selected file editor.");
        window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "← Back to mod configuration"))
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        if (!window.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text == "Mod ID: 928708"))
            throw new InvalidOperationException("Returning from configuration lost the mod context.");
    }
    Console.WriteLine("Both mod configuration file routes and return navigation checked offscreen.");
}
if (screen == "window-controls")
{
    window.GetVisualDescendants().OfType<Button>().Single(button => Avalonia.Automation.AutomationProperties.GetName(button) == "Close window")
        .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
    var confirmation = window.OwnedWindows.Single(dialog => dialog.Title == "Close ARK Server Manager?");
    confirmation.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Close preview"))
        .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
    if (window.IsVisible) throw new InvalidOperationException("Close action did not close the preview window.");
    Console.WriteLine("Production minimize, maximize, restore and close actions checked offscreen.");
}
Console.WriteLine($"Rendered {screen} at {width} x {height}: {path}");
return 0;
