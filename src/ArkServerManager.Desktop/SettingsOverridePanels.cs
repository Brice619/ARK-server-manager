using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Styling;
using ArkServerManager.Core;
using System.Globalization;
using System.Text.Json;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private void ConfigureOverrideList(ListBox list)
    {
        ScrollViewer.SetHorizontalScrollBarVisibility(list,ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list,ScrollBarVisibility.Visible);
        var itemStyle=new Style(s=>s.OfType<ListBoxItem>());
        itemStyle.Setters.Add(new Setter(ListBoxItem.PaddingProperty,new Thickness(0)));
        itemStyle.Setters.Add(new Setter(ListBoxItem.HorizontalContentAlignmentProperty,HorizontalAlignment.Stretch));
        // Rows are containers for editable fields, not selectable choices. Their
        // presentation stays transparent on hover, selection and keyboard focus.
        itemStyle.Setters.Add(new Setter(ListBoxItem.TemplateProperty,new Avalonia.Controls.Templates.FuncControlTemplate<ListBoxItem>((item,scope)=>
        {
            var presenter=new Avalonia.Controls.Presenters.ContentPresenter {Name="PART_ContentPresenter",HorizontalContentAlignment=HorizontalAlignment.Stretch,Background=Brushes.Transparent};
            presenter.Bind(Avalonia.Controls.Presenters.ContentPresenter.ContentProperty,new Avalonia.Data.Binding("Content"){Source=item});
            presenter.Bind(Avalonia.Controls.Presenters.ContentPresenter.ContentTemplateProperty,new Avalonia.Data.Binding("ContentTemplate"){Source=item});
            scope.Register("PART_ContentPresenter",presenter);return presenter;
        })));
        list.Styles.Add(itemStyle);
        list.ItemsPanel=new Avalonia.Controls.Templates.FuncTemplate<Panel?>(()=>new VirtualizingStackPanel());
        list.TemplateApplied+=(_,_)=>{var viewer=list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();if(viewer is not null)ConfigureEditorScrollbar(viewer);};
    }
    private readonly Dictionary<Guid,Dictionary<(int File,string Section,string Key),List<string>>> repeatedSettingDrafts=[];
    private readonly Dictionary<Guid,Dictionary<string,string>> creatureClassBindings=[];
    private readonly Dictionary<Guid,ServerLaunchPreferencesState> loadedLaunchPreferences=[];
    private readonly Dictionary<Guid,ServerLaunchPreferences> launchPreferenceDrafts=[];
    private readonly Dictionary<Guid,ServerNetworkScope> networkScopeDrafts=[];
    private string settingSaveNotice="";

    private List<string> RepeatedValues(ServerDefinition server,string key,int file=1,string section=GameSection)
    {
        if(repeatedSettingDrafts.TryGetValue(server.Id,out var draft)&&draft.TryGetValue((file,section,key),out var values))return values.ToList();
        return new IniDocument(advancedLoadedFiles.GetValueOrDefault((server.Id,file))?.Text??"").GetValues(section,key).ToList();
    }
    private void SetRepeated(ServerDefinition server,string key,List<string> values,int file=1,string section=GameSection)
    {
        if(!repeatedSettingDrafts.TryGetValue(server.Id,out var draft))repeatedSettingDrafts[server.Id]=draft=[];
        draft[(file,section,key)]=values;
    }
    private Border SettingsPanel(string title,Control body)
    {
        var stack=new StackPanel {Spacing=8};stack.Children.Add(Label(title,24,true));stack.Children.Add(new Border {Height=1,Background=Brush.Parse("#30617B")});stack.Children.Add(body);return OverviewFrame(stack,new(24,10));
    }
    private static void PutTuple(List<string> entries,string identityKey,string identity,Action<IniTuple> change)
    {
        var index=entries.FindIndex(text=>{try{return new IniTuple(text).Get(identityKey)==identity;}catch(ArgumentException){return false;}});
        var item=index>=0?new IniTuple(entries[index]):new IniTuple(identityKey=="DinoNameTag"?$"({identityKey}={identity})":$"({identityKey}=\"{identity}\")");change(item);
        if(index>=0)entries[index]=item.ToString();else entries.Add(item.ToString());
    }

    private Action<string>? creatureSearchChanged;
    private Control CreatureOverridesPanel(ServerDefinition server)
    {
        settingsBindingReview.AddRange(new[]{new SettingsBindingReview("Creature spawn weight",1,GameSection,"DinoSpawnWeightMultipliers","tuple"),new SettingsBindingReview("Creature taming",1,GameSection,"PreventDinoTameClassNames","repeated-string")});
        if(!creatureClassBindings.TryGetValue(server.Id,out var bindings))
        {
            bindings=[];if(managerRoot is not null)try{var path=Path.Combine(managerRoot,"app-settings",$"creature-classes-{server.Id:N}.json");if(File.Exists(path))bindings=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(path))??[];}catch(Exception e)when(e is IOException or JsonException){ }
            creatureClassBindings[server.Id]=bindings;
        }
        var items=DocumentedSettings.CreatureCatalog.ToList();
        foreach(var raw in RepeatedValues(server,"DinoSpawnWeightMultipliers"))
        {
            try{var tag=new IniTuple(raw).Get("DinoNameTag");if(tag is null||items.Any(c=>c.NameTag==tag))continue;var className=bindings.GetValueOrDefault(tag,"");items.Add(new(tag,className,tag,true,className.Length>0));}catch(ArgumentException){ }
        }
        foreach(var raw in RepeatedValues(server,"PreventDinoTameClassNames"))
        {
            var className=raw.Trim('"');if(!items.Any(c=>c.ClassName==className))items.Add(new(className,className,bindings.FirstOrDefault(b=>b.Value==className).Key,false,true));
        }
        foreach(var binding in bindings)if(binding.Value.Length>0&&!items.Any(c=>c.ClassName==binding.Value))items.Add(new(binding.Key+" (custom)",binding.Value,binding.Key,true,true));
        items=items.OrderBy(c=>c.Name,StringComparer.OrdinalIgnoreCase).ToList();
        const string columns="*,24,220,40,180";
        var body=new Grid {RowDefinitions=new("44,12,28,8,*"),Height=Math.Max(290,Math.Min(633,Bounds.Height>0?Bounds.Height-390:633))-268};
        var toolbar=new Grid {ColumnDefinitions=new("*,14,194")};var search=EditorText("","Search creature...");Avalonia.Automation.AutomationProperties.SetName(search,"Search creature overrides");toolbar.Children.Add(SearchField(search));var add=EditorButton("＋  Add Override",()=>_ = AddCreatureOverrideAsync(server));Grid.SetColumn(add,2);toolbar.Children.Add(add);body.Children.Add(toolbar);
        var header=new Grid {ColumnDefinitions=new(columns),Margin=new(13,0,37,0)};
        foreach(var (name,column) in new[]{("Creature",0),("Spawn weight",2),("Taming allowed",4)}){var label=Label(name,18);if(column>0)label.HorizontalAlignment=HorizontalAlignment.Center;Grid.SetColumn(label,column);header.Children.Add(label);}Grid.SetRow(header,2);body.Children.Add(header);
        var list=new ListBox {Background=Brushes.Transparent,BorderThickness=new(0),Padding=new(0)};Avalonia.Automation.AutomationProperties.SetName(list,"Creature overrides catalog");ConfigureOverrideList(list);
        var rows=new Dictionary<string,Control>();var weights=new Dictionary<string,List<NumericUpDown>>();var synchronizing=false;
        list.ItemTemplate=new Avalonia.Controls.Templates.FuncDataTemplate<DocumentedSettings.Creature>((creature,_)=>
        {
            if(creature is null)return new Border();var identity=creature.ClassName.Length>0?creature.ClassName:creature.NameTag!;if(rows.TryGetValue(identity,out var cached))return cached;
            var tag=creature.NameTag;var row=new Grid {ColumnDefinitions=new(columns),Height=58};var label=Label(creature.Name,18);label.VerticalAlignment=VerticalAlignment.Center;label.TextTrimming=TextTrimming.CharacterEllipsis;ToolTip.SetTip(label,creature.ClassName);row.Children.Add(label);
            if(creature.Spawnable&&!string.IsNullOrWhiteSpace(tag))
            {
                var original=RepeatedValues(server,"DinoSpawnWeightMultipliers").FirstOrDefault(raw=>{try{return new IniTuple(raw).Get("DinoNameTag")==tag;}catch(ArgumentException){return false;}});
                var weight=SettingsInteger(original is null?"1.0":new IniTuple(original).Get("SpawnWeightMultiplier")??"1.0");weight.Increment=0.1m;weight.FormatString="0.0##";Avalonia.Automation.AutomationProperties.SetName(weight,creature.Name+" spawn weight");var input=SettingsNumberControl(weight);input.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(input,2);row.Children.Add(input);ToolTip.SetTip(input,"Changes spawn weight for name tag "+tag+", including variants sharing that tag.");
                if(!weights.TryGetValue(tag,out var shared))weights[tag]=shared=[];shared.Add(weight);
                weight.ValueChanged+=(_,_)=>{if(synchronizing||weight.Value is not {} value)return;var values=RepeatedValues(server,"DinoSpawnWeightMultipliers");PutTuple(values,"DinoNameTag",tag,t=>t.Set("SpawnWeightMultiplier",value.ToString(CultureInfo.InvariantCulture)));SetRepeated(server,"DinoSpawnWeightMultipliers",values);synchronizing=true;foreach(var other in shared)other.Value=value;synchronizing=false;};
            }
            else{var unavailable=Label("—",19);unavailable.HorizontalAlignment=HorizontalAlignment.Center;unavailable.VerticalAlignment=VerticalAlignment.Center;ToolTip.SetTip(unavailable,"No documented spawn-weight name tag for this creature or it does not use normal spawning.");Grid.SetColumn(unavailable,2);row.Children.Add(unavailable);}
            var allowed=new CheckBox {IsChecked=creature.Tameable&&!RepeatedValues(server,"PreventDinoTameClassNames").Any(c=>c.Trim('"')==creature.ClassName),IsEnabled=creature.Tameable&&creature.ClassName.Length>0,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};Avalonia.Automation.AutomationProperties.SetName(allowed,creature.Name+" taming allowed");ToolTip.SetTip(allowed,creature.Tameable?creature.ClassName:"This creature is not normally tameable; this setting cannot make it tameable.");Grid.SetColumn(allowed,4);row.Children.Add(allowed);
            allowed.IsCheckedChanged+=(_,_)=>{if(!allowed.IsEnabled)return;var values=RepeatedValues(server,"PreventDinoTameClassNames");values.RemoveAll(v=>v.Trim('"')==creature.ClassName);if(allowed.IsChecked!=true)values.Add($"\"{creature.ClassName}\"");SetRepeated(server,"PreventDinoTameClassNames",values);};
            var frame=OverviewFrame(row,new(12,0));frame.Margin=new(0,0,24,8);rows[identity]=frame;return frame;
        });
        void Filter(){var query=search.Text??"";list.ItemsSource=items.Where(c=>(query.Length==0||(c.Name+" "+c.ClassName+" "+c.NameTag).Contains(query,StringComparison.OrdinalIgnoreCase))&&(settingsSearch.Length==0||(c.Name+" "+c.ClassName+" "+c.NameTag).Contains(settingsSearch,StringComparison.OrdinalIgnoreCase))).ToArray();}
        search.TextChanged+=(_,_)=>Filter();creatureSearchChanged=_=>Filter();Filter();Grid.SetRow(list,4);body.Children.Add(list);return SettingsPanel("Creature overrides",body);
    }
    private async Task AddCreatureOverrideAsync(ServerDefinition server)
    {
        var dialog=new Window {Title="Creature override",Width=560,Height=410,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Brush.Parse("#0B1A26"),FontFamily=FontFamily};
        var body=new StackPanel {Margin=new(24),Spacing=14};var tag=EditorText("","Creature name tag, e.g. Rex");var className=EditorText("","Creature class, e.g. Rex_Character_BP_C");var weight=EditorText("1.0","Spawn weight");body.Children.Add(tag);body.Children.Add(className);body.Children.Add(weight);var notice=Label("",16);body.Children.Add(notice);
        body.Children.Add(EditorButton("Add Override",()=>{if(tag.Text is not {Length:>0} name||className.Text is not {Length:>0} creature||name.Any(c=>!char.IsLetterOrDigit(c)&&c!='_')||creature.Any(c=>!char.IsLetterOrDigit(c)&&c!='_')||!decimal.TryParse(weight.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out var value)||value<0){notice.Text="Enter a creature name tag, class and nonnegative spawn weight.";return;}dialog.Close(true);},true));body.Children.Add(EditorButton("Cancel",()=>dialog.Close(false)));dialog.Content=body;
        if(!await dialog.ShowDialog<bool>(this))return;
        var entries=RepeatedValues(server,"DinoSpawnWeightMultipliers");PutTuple(entries,"DinoNameTag",tag.Text!,t=>t.Set("SpawnWeightMultiplier",weight.Text!));SetRepeated(server,"DinoSpawnWeightMultipliers",entries);
        creatureClassBindings[server.Id][tag.Text!]=className.Text!;
        if(managerRoot is not null)await new AtomicJsonStore<Dictionary<string,string>>(Path.Combine(managerRoot,"app-settings",$"creature-classes-{server.Id:N}.json"),true).SaveAsync(creatureClassBindings[server.Id],lifetime.Token);
        Render();
    }

    private Action<string>? engramSearchChanged;
    private Control EngramOverridesPanel(ServerDefinition server)
    {
        settingsBindingReview.AddRange(new[]{new SettingsBindingReview("Auto-unlock all engrams",1,GameSection,"bAutoUnlockAllEngrams","bool"),new SettingsBindingReview("Engram overrides",1,GameSection,"OverrideNamedEngramEntries","tuple")});
        var entries=RepeatedValues(server,"OverrideNamedEngramEntries");var items=DocumentedSettings.EngramCatalog.ToList();
        foreach(var raw in entries)try{var t=new IniTuple(raw);var name=t.Get("EngramClassName");if(name is not null&&!items.Any(i=>i.ClassName==name))items.Add(new(name,name,1,0));}catch(ArgumentException){ }
        const string columns="*,120,20,120,24,160,24,80,24,148";
        var body=new Grid {RowDefinitions=new("38,12,28,8,*"),Height=Math.Max(290,Math.Min(633,Bounds.Height>0?Bounds.Height-390:633))-240};
        var autoRow=new StackPanel {Orientation=Orientation.Horizontal,Spacing=20,VerticalAlignment=VerticalAlignment.Center};
        var draft=screenSettingDrafts[server.Id];
        autoRow.Children.Add(Label("Auto-unlock all engrams",18));
        var auto=SettingsToggle(bool.TryParse(SettingValue(server,new("",GameSection,"bAutoUnlockAllEngrams","False","bool",1),draft,advancedLoadedFiles),out var enabled)&&enabled,on=>draft[(1,GameSection,"bAutoUnlockAllEngrams")]=on?"True":"False");
        Avalonia.Automation.AutomationProperties.SetName(auto,"Auto-unlock all engrams");ToolTip.SetTip(auto,"Automatically unlocks engrams; individual engram overrides are ignored while enabled.");autoRow.Children.Add(auto);body.Children.Add(autoRow);
        var header=new Grid {ColumnDefinitions=new(columns),Margin=new(13,0,37,0)};
        foreach(var (caption,column) in new[]{("Engram",0),("Level",1),("Points",3),("Remove prereqs",5),("Hidden",7)})
        {var label=Label(caption,17);label.VerticalAlignment=VerticalAlignment.Center;if(column>0)label.HorizontalAlignment=HorizontalAlignment.Center;Grid.SetColumn(label,column);header.Children.Add(label);}
        Grid.SetRow(header,2);body.Children.Add(header);
        var list=new ListBox {Background=Brushes.Transparent,BorderThickness=new(0),Padding=new(0),SelectionMode=SelectionMode.Single};
        ConfigureOverrideList(list);
        var rowCache=new Dictionary<string,Control>();
        list.ItemTemplate=new Avalonia.Controls.Templates.FuncDataTemplate<DocumentedSettings.Engram>((item,_)=>
        {
            if(item is null)return new Border();
            if(rowCache.TryGetValue(item.ClassName,out var cached))return cached;
            var (name,engram,levelDefault,pointsDefault)=item;
            var original=entries.FirstOrDefault(raw=>{try{return new IniTuple(raw).Get("EngramClassName")==engram;}catch(ArgumentException){return false;}});var tuple=original is null?null:new IniTuple(original);
            var row=new Grid {ColumnDefinitions=new(columns),Height=64};var title=Label(name,19);title.VerticalAlignment=VerticalAlignment.Center;title.TextTrimming=TextTrimming.CharacterEllipsis;ToolTip.SetTip(title,name);row.Children.Add(title);
            var level=SettingsInteger(tuple?.Get("EngramLevelRequirement")??levelDefault.ToString());var points=SettingsInteger(tuple?.Get("EngramPointsCost")??pointsDefault.ToString());
            var prereq=new CheckBox {IsChecked=tuple?.Get("RemoveEngramPreReq")?.Equals("True",StringComparison.OrdinalIgnoreCase)==true,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};var hidden=new CheckBox {IsChecked=tuple?.Get("EngramHidden")?.Equals("True",StringComparison.OrdinalIgnoreCase)==true,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
            Avalonia.Automation.AutomationProperties.SetName(level,name+" level");Avalonia.Automation.AutomationProperties.SetName(points,name+" points");
            ToolTip.SetTip(prereq,"Remove prerequisites for this engram override.");var levelControl=SettingsNumberControl(level);var pointsControl=SettingsNumberControl(points);levelControl.VerticalAlignment=VerticalAlignment.Center;pointsControl.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(levelControl,1);row.Children.Add(levelControl);Grid.SetColumn(pointsControl,3);row.Children.Add(pointsControl);Grid.SetColumn(prereq,5);row.Children.Add(prereq);Grid.SetColumn(hidden,7);row.Children.Add(hidden);
            var apply=EditorButton("Override",()=>{if(level.Value is not {} l||points.Value is not {} p){ToolTip.SetTip(row,"Enter a level and points value.");return;}var values=RepeatedValues(server,"OverrideNamedEngramEntries");PutTuple(values,"EngramClassName",engram,t=>{t.Set("EngramLevelRequirement",l.ToString(CultureInfo.InvariantCulture));t.Set("EngramPointsCost",p.ToString(CultureInfo.InvariantCulture));t.Set("RemoveEngramPreReq",prereq.IsChecked==true?"True":"False");t.Set("EngramHidden",hidden.IsChecked==true?"True":"False");});SetRepeated(server,"OverrideNamedEngramEntries",values);ToolTip.SetTip(row,"Override is in the draft. Save Changes applies it.");},width:148);apply.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(apply,9);row.Children.Add(apply);
            var frame=OverviewFrame(row,new(12,0));frame.Margin=new(0,0,24,8);rowCache[engram]=frame;return frame;
        });
        list.TemplateApplied+=(_,_)=>{var viewer=list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();if(viewer is not null)ConfigureEditorScrollbar(viewer);};
        engramSearchChanged=query=>{list.ItemsSource=items.Where(i=>query.Length==0||i.Name.Contains(query,StringComparison.OrdinalIgnoreCase)||i.ClassName.Contains(query,StringComparison.OrdinalIgnoreCase)).ToArray();};engramSearchChanged(settingsSearch);
        Grid.SetRow(list,4);body.Children.Add(list);return SettingsPanel("Engram Overrides",body);
    }
    private Control NetworkSettingsPanel(ServerDefinition server)
    {
        var body=new StackPanel {Spacing=18};var row=new Grid {ColumnDefinitions=new("206,*,12,52")};row.Children.Add(Label("LAN address",19));var address=ConnectionAddress(server).Split(':')[0];var field=EditorText(address);field.IsReadOnly=true;Grid.SetColumn(field,1);row.Children.Add(field);var copy=EditorButton("Copy",()=>_ = Clipboard?.SetTextAsync(address),width:52);copy.Content=VectorIcon("M8,2 L22,2 L22,18 L18,18 M2,7 L17,7 L17,23 L2,23 Z",Brush.Parse("#B5DEFF"),24);Grid.SetColumn(copy,3);row.Children.Add(copy);body.Children.Add(row);
        var internet=new CheckBox {IsChecked=networkScopeDrafts.GetValueOrDefault(server.Id,loadedLaunchPreferences.GetValueOrDefault(server.Id)?.Scope??server.NetworkScope)==ServerNetworkScope.Internet};
        internet.IsCheckedChanged+=(_,_)=>networkScopeDrafts[server.Id]=internet.IsChecked==true?ServerNetworkScope.Internet:ServerNetworkScope.LocalNetwork;var scopeRow=new Grid {ColumnDefinitions=new("206,*")};scopeRow.Children.Add(Label("Internet connections",19));Grid.SetColumn(internet,1);scopeRow.Children.Add(internet);body.Children.Add(scopeRow);body.Children.Add(Label("Off: local network only. Save Changes applies the Windows Firewall rule. Internet play also needs router forwarding.",15));
        var result=Label("",17);var check=EditorButton("Check Connection",()=>_ = CheckSettingsConnectionAsync(server,address,result));check.HorizontalAlignment=HorizontalAlignment.Stretch;body.Children.Add(check);body.Children.Add(result);return SettingsPanel("Connection",body);
    }
    private async Task CheckSettingsConnectionAsync(ServerDefinition server,string address,TextBlock result)
    {
        if(IsReviewServer(server.Id)||designPreview){result.Text="The dummy has no running network listener.";return;}
        result.Text="Checking RCON connection…";
        try{using var socket=new System.Net.Sockets.TcpClient();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));await socket.ConnectAsync(address,server.Settings.RconPort,timeout.Token);result.Text="RCON port is reachable. This does not verify player connectivity.";}
        catch(Exception e)when(e is System.Net.Sockets.SocketException or OperationCanceledException){result.Text="RCON port is not reachable. Check server status and network settings.";}
    }
    private ToggleButton SettingsToggle(bool value,Action<bool> change)
    {
        var toggle=new ToggleButton {IsChecked=value,Width=64,Height=38,HorizontalAlignment=HorizontalAlignment.Left};
        toggle.Template=new Avalonia.Controls.Templates.FuncControlTemplate<ToggleButton>((t,_)=>{
            var knob=new Border {Width=28,Height=28,CornerRadius=new(14),Background=Brushes.White,Margin=new(5),VerticalAlignment=VerticalAlignment.Center};var track=new Border {CornerRadius=new(19),Child=knob};
            void Paint(){track.Background=Brush.Parse(t.IsChecked==true?"#009BFF":"#25455D");knob.HorizontalAlignment=t.IsChecked==true?HorizontalAlignment.Right:HorizontalAlignment.Left;}Paint();t.IsCheckedChanged+=(_,_)=>Paint();return track;
        });toggle.IsCheckedChanged+=(_,_)=>change(toggle.IsChecked==true);return toggle;
    }
    private static NumericUpDown SettingsInteger(string value)=>new(){Value=decimal.TryParse(value,out var parsed)?parsed:0,Minimum=0,Maximum=1000000,Increment=1,FormatString="0",Height=44,MinHeight=0,Padding=new(30,0,30,0),TextAlignment=TextAlignment.Center,VerticalContentAlignment=VerticalAlignment.Center,FontSize=20,Background=Brush.Parse("#071A27"),BorderBrush=Brush.Parse("#32708F")};
    private static Control SettingsNumberControl(NumericUpDown number)
    {
        number.ShowButtonSpinner=false;var fieldStyle=new Style(s=>s.OfType<TextBox>());fieldStyle.Setters.Add(new Setter(TextBox.MinHeightProperty,0d));fieldStyle.Setters.Add(new Setter(TextBox.VerticalContentAlignmentProperty,VerticalAlignment.Center));fieldStyle.Setters.Add(new Setter(TextBox.PaddingProperty,new Thickness(30,0,30,0)));fieldStyle.Setters.Add(new Setter(ScrollViewer.VerticalScrollBarVisibilityProperty,ScrollBarVisibility.Disabled));fieldStyle.Setters.Add(new Setter(ScrollViewer.HorizontalScrollBarVisibilityProperty,ScrollBarVisibility.Disabled));number.Styles.Add(fieldStyle);var wrapper=new Grid {ColumnDefinitions=new("*,30"),Height=44};wrapper.Children.Add(number);Grid.SetColumnSpan(number,2);var arrows=new Grid {RowDefinitions=new("*,*"),Margin=new(0,2,4,2)};
        foreach(var direction in new[]{1,-1}){var step=direction;var button=new Button {Content=VectorIcon(direction==1?"M3,15 L12,6 L21,15":"M3,6 L12,15 L21,6",Brush.Parse("#A8D8FF"),13),Background=Brushes.Transparent,BorderThickness=new(0),Padding=new(0),HorizontalContentAlignment=HorizontalAlignment.Center};button.Click+=(_,_)=>number.Value=Math.Clamp((number.Value??0)+step*number.Increment,number.Minimum,number.Maximum);Grid.SetRow(button,direction==1?0:1);arrows.Children.Add(button);}Grid.SetColumn(arrows,1);wrapper.Children.Add(arrows);return wrapper;
    }
    private Control AdvancedSettingsPanel(ServerDefinition server)
    {
        var stack=new StackPanel {Spacing=12};var body=new StackPanel {Spacing=14};
        var preferences=launchPreferenceDrafts.GetValueOrDefault(server.Id,loadedLaunchPreferences.GetValueOrDefault(server.Id)?.Preferences??server.LaunchPreferences);
        var argsRow=new Grid {ColumnDefinitions=new("244,*")};var caption=Label("Extra launch arguments",19);caption.VerticalAlignment=VerticalAlignment.Center;argsRow.Children.Add(caption);
        var arguments=EditorText(preferences.ExtraArguments);arguments.MaxLength=4096;Grid.SetColumn(arguments,1);argsRow.Children.Add(arguments);body.Children.Add(argsRow);
        Avalonia.Automation.AutomationProperties.SetName(arguments,"Extra launch arguments");
        arguments.TextChanged+=(_,_)=>launchPreferenceDrafts[server.Id]=launchPreferenceDrafts.GetValueOrDefault(server.Id,preferences) with {ExtraArguments=arguments.Text??""};
        var switches=new Grid {ColumnDefinitions=new("244,*")};
        var logCaption=Label("Server logging",19);Grid.SetColumn(logCaption,0);switches.Children.Add(logCaption);var logging=SettingsToggle(preferences.ServerLogging,on=>launchPreferenceDrafts[server.Id]=launchPreferenceDrafts.GetValueOrDefault(server.Id,preferences) with {ServerLogging=on});Grid.SetColumn(logging,1);switches.Children.Add(logging);body.Children.Add(switches);
        Avalonia.Automation.AutomationProperties.SetName(logging,"Server logging");
        stack.Children.Add(SettingsPanel("Launch options",body));
        var config=new StackPanel {Spacing=10};var files=new Grid {ColumnDefinitions=new("*,14,*")};for(var i=0;i<2;i++){var index=i;var button=EditorButton(i==0?"Open Game.ini":"Open GameUserSettings.ini",()=>{advancedEditorServer=server.Id;advancedEditorFile=index==0?1:0;Render();});button.HorizontalAlignment=HorizontalAlignment.Stretch;Grid.SetColumn(button,i==0?0:2);files.Children.Add(button);}config.Children.Add(files);
        config.Children.Add(Label("Key/value overrides",20,true));
        var table=new StackPanel {Spacing=6};config.Children.Add(table);
        var header=new Grid {ColumnDefinitions=new("*,*,*,*")};var labels=new[]{"File","Section","Key","Value"};for(var i=0;i<4;i++){var label=Label(labels[i],17);Grid.SetColumn(label,i);header.Children.Add(label);}table.Children.Add(header);
        var draftRows=new StackPanel {Spacing=8};table.Children.Add(draftRows);
        void Refresh(){draftRows.Children.Clear();foreach(var entry in screenSettingDrafts[server.Id]){var row=new Grid {ColumnDefinitions=new("*,*,*,*")};var values=new[]{entry.Key.File==1?"Game.ini":"GameUserSettings.ini",entry.Key.Section,entry.Key.Key,entry.Value};for(var i=0;i<4;i++){var label=Label(values[i],17);label.TextTrimming=TextTrimming.CharacterEllipsis;ToolTip.SetTip(label,values[i]);Grid.SetColumn(label,i);row.Children.Add(label);}draftRows.Children.Add(row);}if(draftRows.Children.Count==0)draftRows.Children.Add(Label("No overrides",17));}Refresh();
        config.Children.Add(EditorButton("＋  Add Override",()=>_ = AddAsync(),width:214));stack.Children.Add(SettingsPanel("Configuration",config));return stack;
        async Task AddAsync()
        {
            var dialog=new Window {Title="Configuration override",Width=580,Height=480,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Brush.Parse("#0B1A26"),FontFamily=FontFamily};var form=new StackPanel {Margin=new(24),Spacing=12};
            var file=EditorText("GameUserSettings.ini");var section=EditorText("ServerSettings");var key=EditorText("");var value=EditorText("");foreach(var pair in new[]{("File",file),("Section",section),("Key",key),("Value",value)}){var row=new Grid {ColumnDefinitions=new("100,*")};row.Children.Add(Label(pair.Item1,18));Grid.SetColumn(pair.Item2,1);row.Children.Add(pair.Item2);form.Children.Add(row);}var notice=Label("",17);form.Children.Add(notice);
            form.Children.Add(EditorButton("Add Override",()=>{try{if(file.Text is not ("Game.ini" or "GameUserSettings.ini"))throw new ArgumentException("Choose Game.ini or GameUserSettings.ini.");SettingsDraft.Apply("",[new(section.Text??"",key.Text??"",[value.Text??""])]);dialog.Close(true);}catch(ArgumentException e){notice.Text=e.Message;}},true));form.Children.Add(EditorButton("Cancel",()=>dialog.Close(false)));dialog.Content=form;
            if(await dialog.ShowDialog<bool>(this)){screenSettingDrafts[server.Id][(file.Text=="Game.ini"?1:0,section.Text!,key.Text!)]=value.Text??"";Refresh();}
        }
    }
}
