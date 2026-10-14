using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private Guid? clusterActivityScope;
    private string clusterActivitySearch="";
    private string clusterActivityMap="All maps";
    private string clusterActivityEvent="All events";
    private bool clusterActivityFollow=true;
    private bool clusterActivityNewest=true;
    private bool clusterActivityLoading;
    private string clusterActivityNotice="";
    private DispatcherTimer? clusterActivityTimer;
    private Action? refreshClusterActivityRows;
    private string ClusterEventName(ActivityEntry entry)
    {
        var category=entry.Category.ToLowerInvariant();var stage=entry.Stage?.ToLowerInvariant()??"";var message=entry.Message.ToLowerInvariant();
        if(category.Contains("transfer")||stage.Contains("transfer"))return "Transfers";
        if(category.Contains("backup")||stage.Contains("backup"))return entry.ServerId==clusterActivityScope?"Cluster Backup":"Backup";
        if(category.Contains("mod")||stage.Contains("mod"))return "Mods";
        if(category.Contains("setting")||stage.Contains("setting"))return "Settings";
        if(category=="server"&&(message=="server started"||stage=="started"||stage=="start-complete"))return "Started";
        if(category=="server"&&(message=="server stopped"||stage=="stopped"||stage=="stop-complete"))return "Stopped";
        return entry.Category.Length>0?char.ToUpperInvariant(entry.Category[0])+entry.Category[1..]:"Activity";
    }
    private void ApprovedClusterActivity(ClusterDefinition cluster,ServerDefinition[] members)
    {
        if(clusterActivityScope!=cluster.Id){clusterActivityScope=cluster.Id;clusterActivitySearch="";clusterActivityMap="All maps";clusterActivityEvent="All events";clusterActivityNewest=true;}
        var height=Math.Clamp(Bounds.Height>0?Bounds.Height-436:588,310,588);
        var shell=new Grid{Height=height-40,RowDefinitions=new("53,17,*,20,1,20,57")};
        var toolbar=new Grid{ColumnDefinitions=new("458,48,56,250,32,68,250,*,168")};
        if(Bounds.Width>0&&Bounds.Width<1450)toolbar.ColumnDefinitions=new("2*,18,50,1*,18,60,1*,18,145");
        var search=EditorText(clusterActivitySearch,"Search activity...");search.Height=53;Avalonia.Automation.AutomationProperties.SetName(search,"Search cluster activity");toolbar.Children.Add(SearchField(search));
        var mapLabel=Label("Map:",19);mapLabel.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(mapLabel,2);toolbar.Children.Add(mapLabel);
        var mapItems=new[]{"All maps","Cluster"}.Concat(members.Select(s=>MapName(s.Map)).Distinct()).ToArray();
        var map=ClusterActivityCombo(mapItems,mapItems.Contains(clusterActivityMap)?clusterActivityMap:"All maps");Grid.SetColumn(map,3);toolbar.Children.Add(map);
        var eventLabel=Label("Event:",19);eventLabel.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(eventLabel,5);toolbar.Children.Add(eventLabel);
        ActivityEntry[] Scoped()=> (history?.Entries??[]).Where(e=>e.ServerId==cluster.Id||e.ServerId is Guid id&&members.Any(s=>s.Id==id)).ToArray();
        var eventItems=new[]{"All events","Started","Stopped","Cluster Backup","Settings","Mods","Transfers"}.Concat(Scoped().Select(ClusterEventName)).Distinct().ToArray();
        var events=ClusterActivityCombo(eventItems,clusterActivityEvent);Grid.SetColumn(events,6);toolbar.Children.Add(events);
        string MapLabel(ActivityEntry entry)=>entry.ServerId==cluster.Id?"Cluster":members.FirstOrDefault(s=>s.Id==entry.ServerId) is {} member?MapName(member.Map):"Cluster";
        ActivityEntry[] Visible()=>Scoped().Where(e=>(clusterActivityMap=="All maps"||MapLabel(e)==clusterActivityMap)&&(clusterActivityEvent=="All events"||ClusterEventName(e)==clusterActivityEvent)&&(e.Message.Contains(clusterActivitySearch,StringComparison.OrdinalIgnoreCase)||MapLabel(e).Contains(clusterActivitySearch,StringComparison.OrdinalIgnoreCase)||ClusterEventName(e).Contains(clusterActivitySearch,StringComparison.OrdinalIgnoreCase))).OrderBy(e=>clusterActivityNewest?-e.Time.UtcTicks:e.Time.UtcTicks).ToArray();
        var export=ManagementButton("Export","M12,1 L12,16 M6,10 L12,16 L18,10 M2,16 L2,23 L22,23 L22,16",()=>_ = ExportClusterActivityAsync(Visible(),MapLabel),168,height:53);Grid.SetColumn(export,8);toolbar.Children.Add(export);shell.Children.Add(toolbar);
        var table=new Grid{RowDefinitions=new("49,*")};
        var columns="282,283,318,*";if(Bounds.Width>0&&Bounds.Width<1450)columns="1*,1*,1.12*,1.75*";
        var header=new Grid{ColumnDefinitions=new(columns),Margin=new(0,0,42,0),Height=49,Background=Brush.Parse("#08354D")};
        var time=EditorButton(clusterActivityNewest?"Time ▾":"Time ▴",()=>{clusterActivityNewest=!clusterActivityNewest;refreshClusterActivityRows?.Invoke();},width:double.NaN);time.Background=Brushes.Transparent;time.BorderThickness=new(0);time.HorizontalContentAlignment=HorizontalAlignment.Left;time.Padding=new(22,0);time.Height=49;header.Children.Add(time);
        foreach(var (title,index) in new[]{("Map",1),("Event",2),("Details",3)}){var label=Label(title,19);label.Margin=new(22,0);label.VerticalAlignment=VerticalAlignment.Center;var cellBorder=new Border{Child=label,BorderBrush=Brush.Parse("#17485D"),BorderThickness=new(1,0,0,0)};Grid.SetColumn(cellBorder,index);header.Children.Add(cellBorder);}table.Children.Add(header);
        var rows=new StackPanel();var scroll=new ScrollViewer{Content=rows,VerticalScrollBarVisibility=ScrollBarVisibility.Visible,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};ConfigureEditorScrollbar(scroll);Grid.SetRow(scroll,1);table.Children.Add(scroll);
        void Populate(){var offset=scroll.Offset;rows.Children.Clear();var entries=Visible();foreach(var (entry,index) in entries.Select((e,i)=>(e,i))){var row=new Grid{ColumnDefinitions=new(columns),Height=54,Margin=new(0,0,20,0),Background=Brush.Parse(index%2==0?"#06283C":"#083047")};var name=ClusterEventName(entry);var color=name switch{"Started"=>"#80FFA8","Stopped"=>"#FF5353","Settings"=>"#FFD337",_=>"#00DFFF"};var values=new[]{entry.Time.LocalDateTime.ToString("MMM d, yyyy  h:mm tt"),MapLabel(entry),name,entry.Message};for(var c=0;c<4;c++){Control cell;if(c==2){var line=new StackPanel{Orientation=Orientation.Horizontal,Spacing=18,Margin=new(20,0),VerticalAlignment=VerticalAlignment.Center};var glyph=name switch{"Started"=>"M3,2 L21,12 L3,22 Z","Stopped"=>"M3,3 L21,3 L21,21 L3,21 Z","Cluster Backup" or "Backup"=>DatabaseGlyph,"Transfers"=>"M2,6 L22,6 M16,1 L22,6 L16,11 M22,18 L2,18 M8,13 L2,18 L8,23","Settings"=>GearIcon,"Mods"=>"M2,5 L9,5 C7,1 15,1 13,5 L20,5 L20,12 C24,10 24,18 20,16 L20,23 L13,23 C15,19 7,19 9,23 L2,23 L2,16 C6,18 6,10 2,12 Z",_=>ClockGlyph};var icon=VectorIcon(glyph,Brush.Parse(color),28);if(name is "Started" or "Stopped" or "Settings" or "Mods")((Avalonia.Controls.Shapes.Path)icon).Fill=Brush.Parse(color);line.Children.Add(icon);var text=Label(name,19);text.Foreground=Brush.Parse(color);line.Children.Add(text);cell=line;}else{var label=Label(values[c],19);label.Margin=new(22,0);label.VerticalAlignment=VerticalAlignment.Center;label.TextWrapping=TextWrapping.NoWrap;label.TextTrimming=TextTrimming.CharacterEllipsis;ToolTip.SetTip(label,values[c]);cell=label;}var cellBorder=new Border{Child=cell,BorderBrush=Brush.Parse("#103F55"),BorderThickness=new(c==0?0:1,0,0,0)};Grid.SetColumn(cellBorder,c);row.Children.Add(cellBorder);}rows.Children.Add(new Border{Child=row,BorderBrush=Brush.Parse("#17485D"),BorderThickness=new(0,0,0,1)});}if(entries.Length==0){var empty=Label(clusterActivityNotice.Length>0?clusterActivityNotice:"No activity matches these filters.",18);empty.Margin=new(22,24);rows.Children.Add(empty);}ToolTip.SetTip(table,clusterActivityNotice.Length>0?clusterActivityNotice:history is {OlderEntriesOmitted:true}?"Showing recent history; older entries remain in the activity journal.":history is {UnreadableEntries:>0}?"Some journal entries could not be read.":null);Dispatcher.UIThread.Post(()=>scroll.Offset=clusterActivityFollow&&clusterActivityNewest?new(0,0):offset);}
        refreshClusterActivityRows=Populate;search.TextChanged+=(_,_)=>{clusterActivitySearch=search.Text??"";Populate();};map.SelectionChanged+=(_,_)=>{clusterActivityMap=map.SelectedItem as string??"All maps";Populate();};events.SelectionChanged+=(_,_)=>{clusterActivityEvent=events.SelectedItem as string??"All events";Populate();};Populate();
        var tableFrame=OverviewFrame(table,new(0));Grid.SetRow(tableFrame,2);shell.Children.Add(tableFrame);var divider=new Border{Height=1,Background=Brush.Parse("#18B8DE")};Grid.SetRow(divider,4);shell.Children.Add(divider);
        var footer=new Grid{ColumnDefinitions=new("*,220")};var follow=new StackPanel{Orientation=Orientation.Horizontal,Spacing=28,VerticalAlignment=VerticalAlignment.Center};var toggle=SettingsToggle(clusterActivityFollow,on=>{clusterActivityFollow=on;if(on)_ = RefreshClusterActivityAsync();});toggle.Width=76;toggle.Height=36;follow.Children.Add(toggle);var followText=new StackPanel{Spacing=2};followText.Children.Add(Label("Follow activity",21,true));var explanation=Label("Automatically show new events",16);explanation.Foreground=Brush.Parse("#A5C9E5");followText.Children.Add(explanation);follow.Children.Add(followText);footer.Children.Add(follow);
        var clear=ManagementButton("Clear Filters","M1,3 L23,3 L15,12 L15,21 L9,24 L9,12 Z",()=>{clusterActivitySearch="";clusterActivityMap="All maps";clusterActivityEvent="All events";search.Text="";map.SelectedItem="All maps";events.SelectedItem="All events";Populate();},220,height:57);Grid.SetColumn(clear,1);footer.Children.Add(clear);Grid.SetRow(footer,6);shell.Children.Add(footer);var frame=OverviewFrame(shell,new(18,20));frame.Margin=new(-18,2,-18,0);content.Children.Add(frame);
        if(pageScroll is not null)pageScroll.VerticalScrollBarVisibility=ScrollBarVisibility.Disabled;
        if(!designPreview&&client is not null){if(clusterActivityTimer is null){clusterActivityTimer=new(){Interval=TimeSpan.FromSeconds(3)};clusterActivityTimer.Tick+=(_,_)=>{if(lifetime.IsCancellationRequested){clusterActivityTimer.Stop();return;}if(selectedCluster is not null&&selectedServer is null&&managementTab==6&&clusterActivityFollow)_ = RefreshClusterActivityAsync();};clusterActivityTimer.Start();}if(history is null)_ = RefreshClusterActivityAsync();}
    }
    private ComboBox ClusterActivityCombo(string[] items,string selected)=>new(){ItemsSource=items,SelectedItem=selected,Height=53,HorizontalAlignment=HorizontalAlignment.Stretch,VerticalContentAlignment=VerticalAlignment.Center,Padding=new(15,7),FontSize=19,Background=Brush.Parse("#032236"),BorderBrush=Brush.Parse("#21BDEA"),BorderThickness=new(1),CornerRadius=new(5)};
    private async Task RefreshClusterActivityAsync()
    {
        if(client is null||clusterActivityLoading||lifetime.IsCancellationRequested)return;clusterActivityLoading=true;
        try{var latest=await client.CallAsync<ActivityHistory>("activity",null,TimeSpan.FromSeconds(8),lifetime.Token);if(latest.Entries is null||latest.UnreadableEntries<0)throw new InvalidDataException("Invalid activity history.");history=latest;clusterActivityNotice="";refreshClusterActivityRows?.Invoke();}
        catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
        catch(Exception error)when(error is IOException or TimeoutException or InvalidOperationException or System.Text.Json.JsonException){clusterActivityNotice="Activity unavailable; showing previously loaded events.";refreshClusterActivityRows?.Invoke();}
        finally{clusterActivityLoading=false;}
    }
    private async Task ExportClusterActivityAsync(ActivityEntry[] entries,Func<ActivityEntry,string> map)
    {
        if(!StorageProvider.CanSave)return;using var file=await StorageProvider.SaveFilePickerAsync(new(){Title="Export Cluster Activity",SuggestedFileName="cluster-activity.csv",DefaultExtension="csv"});if(file is null)return;
        static string Csv(string value)=>"\""+value.Replace("\"","\"\"")+"\"";
        await using var stream=await file.OpenWriteAsync();stream.SetLength(0);await using var writer=new StreamWriter(stream);await writer.WriteLineAsync("Time,Map,Event,Details");foreach(var entry in entries)await writer.WriteLineAsync(string.Join(",",Csv(entry.Time.ToString("O")),Csv(map(entry)),Csv(ClusterEventName(entry)),Csv(entry.Message)));
    }
}
