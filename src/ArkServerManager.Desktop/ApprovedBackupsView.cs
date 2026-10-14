using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private sealed class ApprovedBackupState{public ManagedBackupInventory? Inventory;public List<BackupHistoryEntry> Legacy=[];public Dictionary<long,long> LegacySizes=[];public bool Loading,Busy;public string Notice="";public Action? Refresh;}
    private readonly Dictionary<Guid,ApprovedBackupState> approvedBackupStates=[];
    private string backupSearch="",backupSort="Created";private bool backupSortDescending=true;
    private void ApprovedBackups(ServerDefinition server)
    {
        if(!approvedBackupStates.TryGetValue(server.Id,out var state)){approvedBackupStates[server.Id]=state=new();Avalonia.Threading.Dispatcher.UIThread.Post(()=>_ = LoadApprovedBackupsAsync(server,state));}
        var shell=new Grid{RowDefinitions=new("52,24,*,28,1,20,86"),Height=ManagementBodyHeight-42};
        var toolbar=new Grid{ColumnDefinitions=new("532,*,254,14,204")};var search=EditorText(backupSearch,"Search backups...");search.Height=52;Avalonia.Automation.AutomationProperties.SetName(search,"Search backups");toolbar.Children.Add(SearchField(search));
        var create=ManagementButton(state.Busy?"Working…":"Create Backup","M12,1 L12,23 M1,12 L23,12",state.Busy?null:()=>_ = CreateApprovedBackupAsync(server,state),254,true,height:52);Grid.SetColumn(create,2);toolbar.Children.Add(create);
        var schedule=ManagementButton("Schedule",ClockGlyph,state.Busy?null:()=>_ = ScheduleApprovedBackupsAsync(server,state),204,height:52);Grid.SetColumn(schedule,4);toolbar.Children.Add(schedule);shell.Children.Add(toolbar);
        var columns=Bounds.Width>=1450?"435,274,175,245,*":"2.05*,1.3*,0.8*,1.05*,250";var table=new Grid{RowDefinitions=new("62,*")};
        var heading=new Grid{ColumnDefinitions=new(columns),Margin=new(26,0,16,0)};
        foreach(var (title,column) in new[]{("Backup",0),("Created",1),("Size",2),("Type",3),("Actions",4)}){var button=EditorButton(title,title=="Actions"?null:()=>{backupSortDescending=backupSort==title?!backupSortDescending:false;backupSort=title;state.Refresh?.Invoke();});button.Height=60;button.Padding=new(0);var headingContent=new StackPanel{Orientation=Orientation.Horizontal,Spacing=14,VerticalAlignment=VerticalAlignment.Center};headingContent.Children.Add(Label(title,18));if(title!="Actions")headingContent.Children.Add(VectorIcon("M2,5 L6,1 L10,5 M2,10 L6,14 L10,10",Brush.Parse("#6086AD"),14));button.Content=headingContent;button.Background=Brushes.Transparent;button.BorderThickness=new(0);button.HorizontalContentAlignment=HorizontalAlignment.Left;button.IsEnabled=true;Grid.SetColumn(button,column);heading.Children.Add(button);}table.Children.Add(heading);
        var rows=new StackPanel{Spacing=0};var list=new ScrollViewer{Content=rows,VerticalScrollBarVisibility=ScrollBarVisibility.Visible,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};ConfigureEditorScrollbar(list);Grid.SetRow(list,1);table.Children.Add(list);
        var tableFrame=OverviewFrame(table,new(0));Grid.SetRow(tableFrame,2);shell.Children.Add(tableFrame);
        void Populate()
        {
            rows.Children.Clear();var entries=state.Inventory?.Entries??[];var ordered=backupSort switch{"Backup"=>entries.OrderBy(e=>e.Name).ToList(),"Size"=>entries.OrderBy(e=>e.Bytes).ToList(),"Type"=>entries.OrderBy(e=>e.Kind).ToList(),_=>entries.OrderBy(e=>e.CreatedAt).ToList()};if(backupSortDescending)ordered.Reverse();
            foreach(var entry in ordered.Where(e=>e.Name.Contains(backupSearch,StringComparison.OrdinalIgnoreCase)))
            {
                var row=new Grid{ColumnDefinitions=new(columns),Height=82,Margin=new(26,0,16,0)};
                var name=new StackPanel{Orientation=Orientation.Horizontal,Spacing=30,VerticalAlignment=VerticalAlignment.Center};name.Children.Add(VectorIcon("M3,1 L15,1 L22,8 L22,23 L3,23 Z M15,1 L15,8 L22,8",Brush.Parse("#B5D8FF"),30));var title=Label(entry.Name,19);title.VerticalAlignment=VerticalAlignment.Center;title.TextTrimming=TextTrimming.CharacterEllipsis;name.Children.Add(title);row.Children.Add(name);
                var date=Label(entry.CreatedAt.LocalDateTime.ToString("MMM d, yyyy, h:mm tt"),19);date.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(date,1);row.Children.Add(date);var size=Label(FormatBackupBytes(entry.Bytes),19);size.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(size,2);row.Children.Add(size);
                var type=Label(entry.Kind,16);type.Foreground=Brush.Parse(entry.Kind=="Scheduled"?"#76F2DA":"#D1E5FF");var badge=new Border{Child=type,Padding=new(20,7),CornerRadius=new(20),Background=Brush.Parse(entry.Kind=="Scheduled"?"#123936":"#1B344B"),BorderBrush=Brush.Parse("#2D647A"),BorderThickness=new(1),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(badge,3);row.Children.Add(badge);
                var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=24,VerticalAlignment=VerticalAlignment.Center};actions.Children.Add(ManagementButton("Restore",RefreshGlyph,state.Busy?null:()=>_ = RestoreApprovedBackupAsync(server,state,entry),142,height:46));var delete=ManagementButton("",TrashGlyph,state.Busy?null:()=>_ = DeleteApprovedBackupAsync(server,state,entry),48,color:"#FF5454",height:46);delete.Background=Brushes.Transparent;delete.BorderThickness=new(0);Avalonia.Automation.AutomationProperties.SetName(delete,"Delete backup "+entry.Name);actions.Children.Add(delete);Grid.SetColumn(actions,4);row.Children.Add(actions);rows.Children.Add(new Border{Child=row,BorderBrush=Brush.Parse("#27536D"),BorderThickness=new(0,1,0,0)});
            }
            foreach(var point in state.Legacy.Where(p=>backupSearch.Length==0||"Automatic backup".Contains(backupSearch,StringComparison.OrdinalIgnoreCase)).OrderByDescending(p=>p.CreatedAt))
            {
                var row=new Grid{ColumnDefinitions=new(columns),Height=82,Margin=new(26,0,16,0)};row.Children.Add(Label("Automatic backup",19));var date=Label(point.CreatedAt.LocalDateTime.ToString("MMM d, yyyy, h:mm tt"),19);Grid.SetColumn(date,1);row.Children.Add(date);var size=Label(state.LegacySizes.TryGetValue(point.RuntimeHour,out var bytes)?FormatBackupBytes(bytes):"Unavailable",19);Grid.SetColumn(size,2);row.Children.Add(size);var type=Label("Scheduled",16);Grid.SetColumn(type,3);row.Children.Add(type);var restore=ManagementButton("Restore",RefreshGlyph,()=>{backupViewServer=server.Id;selectedRestorePoint=point;Render();},142);var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=24};actions.Children.Add(restore);var delete=ManagementButton("",TrashGlyph,state.Busy?null:()=>_ = DeleteLegacyBackupAsync(server,state,point),48,color:"#FF5454",height:46);delete.Background=Brushes.Transparent;delete.BorderThickness=new(0);Avalonia.Automation.AutomationProperties.SetName(delete,"Delete automatic backup "+point.RuntimeHour);actions.Children.Add(delete);Grid.SetColumn(actions,4);row.Children.Add(actions);foreach(var child in row.Children)child.VerticalAlignment=VerticalAlignment.Center;rows.Children.Add(new Border{Child=row,BorderBrush=Brush.Parse("#27536D"),BorderThickness=new(0,1,0,0)});
            }
            if(rows.Children.Count==0)rows.Children.Add(new TextBlock{Text=state.Loading?"Loading backups…":backupSearch.Length>0?"No matching backups":"No backups yet",Margin=new(26),FontSize=19,Foreground=Brush.Parse("#A9CAE8")});
        }
        state.Refresh=Populate;search.TextChanged+=(_,_)=>{backupSearch=search.Text??"";Populate();};Populate();
        var line=new Border{Height=1,Background=Brush.Parse("#27536D")};Grid.SetRow(line,4);shell.Children.Add(line);
        var footer=new Grid{ColumnDefinitions=new("450,1,*,210")};var policy=state.Inventory?.Policy??new();
        var timing=new StackPanel{Orientation=Orientation.Horizontal,Spacing=28,VerticalAlignment=VerticalAlignment.Center};timing.Children.Add(VectorIcon(ClockGlyph,Brush.Parse("#C9E2FF"),40));var details=new StackPanel{Spacing=6};details.Children.Add(Label(policy.Enabled?$"Daily at {new DateTime(2000,1,1,policy.Hour,policy.Minute,0):h:mm tt}":policy.Configured?"Schedule disabled":IsReviewServer(server.Id)||designPreview?"No schedule yet":"Hourly while running",21,true));var feedback=Label(state.Notice.Length>0?state.Notice:$"Keep {(policy.Configured?policy.Keep:10)} backups",17);feedback.MaxWidth=340;feedback.TextWrapping=TextWrapping.NoWrap;feedback.TextTrimming=TextTrimming.CharacterEllipsis;ToolTip.SetTip(feedback,feedback.Text);details.Children.Add(feedback);timing.Children.Add(details);footer.Children.Add(timing);
        var divider=new Border{Width=1,Height=86,Background=Brush.Parse("#27536D")};Grid.SetColumn(divider,1);footer.Children.Add(divider);
        var location=new StackPanel{Orientation=Orientation.Horizontal,Spacing=32,Margin=new(52,0,10,0),VerticalAlignment=VerticalAlignment.Center};location.Children.Add(VectorIcon(FolderGlyph,Brush.Parse("#88C1FF"),40));var pathText=new StackPanel{Spacing=6};pathText.Children.Add(Label("Backup location",17));var path=new ManagedBackupStore(IsReviewServer(server.Id)||designPreview?ManagementReviewServer(server):server).DirectoryPath;var pathLabel=Label(path,18);pathLabel.MaxWidth=500;pathLabel.TextWrapping=TextWrapping.NoWrap;pathLabel.TextTrimming=TextTrimming.CharacterEllipsis;ToolTip.SetTip(pathLabel,path);pathText.Children.Add(pathLabel);location.Children.Add(pathText);Grid.SetColumn(location,2);footer.Children.Add(location);
        var open=ManagementButton("Open Folder",FolderGlyph,()=>{Directory.CreateDirectory(path);_ = LaunchFolderAsync(path);},210,height:52);Grid.SetColumn(open,3);footer.Children.Add(open);Grid.SetRow(footer,6);shell.Children.Add(footer);
        content.Children.Add(OverviewFrame(shell,new(20,24,20,18)));if(pageScroll is not null)pageScroll.VerticalScrollBarVisibility=ScrollBarVisibility.Disabled;
    }
    private static string FormatBackupBytes(long bytes)=>bytes>=1024*1024?$"{bytes/(1024d*1024):0.#} MB":bytes>=1024?$"{bytes/1024d:0.#} KB":bytes+" B";
    private async Task LaunchFolderAsync(string path){if(!designPreview){ManagedBackupStore.RejectLinks(path);await Launcher.LaunchUriAsync(new Uri(path+Path.DirectorySeparatorChar));}}
    private async Task LoadApprovedBackupsAsync(ServerDefinition server,ApprovedBackupState state)
    {
        if(state.Loading)return;state.Loading=true;
        try
        {
            if(IsReviewServer(server.Id)||designPreview){await InitializeManagementReviewAsync(server);state.Inventory=await new ManagedBackupStore(ManagementReviewServer(server)).ReadAsync(lifetime.Token);}
            else{state.Inventory=await client!.CallAsync<ManagedBackupInventory>("managed-backups-read",server.Id,TimeSpan.FromSeconds(8),lifetime.Token);state.Legacy=(await client.CallAsync<AutomaticBackupState>("backups-history",new BackupHistoryRequest(server.Id),TimeSpan.FromSeconds(8),lifetime.Token)).History;foreach(var point in state.Legacy){var archive=Path.Combine(server.Directory,"backups",point.FileName);ManagedBackupStore.RejectLinks(archive);if(File.Exists(archive))state.LegacySizes[point.RuntimeHour]=new FileInfo(archive).Length;}}
        }catch(Exception e)when(IsBasicSaveError(e)){state.Notice=e.Message;}finally{state.Loading=false;if(!lifetime.IsCancellationRequested&&selectedServer==server.Id&&managementTab==3)Render();}
    }
    private async Task DeleteLegacyBackupAsync(ServerDefinition server,ApprovedBackupState state,BackupHistoryEntry entry)
    {
        if(!await ConfirmManagementAsync("Delete automatic backup?",$"Delete the automatic backup from {entry.CreatedAt.LocalDateTime:g}?","Delete"))return;
        state.Busy=true;Render();try{state.Legacy=(await RunManagedScreenJobAsync(server.Id,"managed-backup-delete-legacy",new LegacyBackupSelection(server.Id,entry.RuntimeHour,true))).Deserialize<AutomaticBackupState>()!.History;state.Notice="Backup deleted.";}catch(Exception e)when(IsBasicSaveError(e)){state.Notice=e.Message;}finally{state.Busy=false;Render();}
    }
    private async Task CreateApprovedBackupAsync(ServerDefinition server,ApprovedBackupState state)
    {
        var body=new StackPanel{Margin=new(26),Spacing=22};body.Children.Add(Label("Create Backup",26,true));var name=EditorText("Manual backup","Backup name");body.Children.Add(name);var dialog=ManagementDialog("Create Backup",body);body.Children.Add(EditorButton("Create Backup",()=>dialog.Close(true),true));if(!await dialog.ShowDialog<bool>(this))return;
        state.Busy=true;Render();try{state.Inventory=IsReviewServer(server.Id)||designPreview?await new ManagedBackupStore(ManagementReviewServer(server)).CreateAsync(name.Text??"",false,ct=>Task.FromResult<IStableServerState>(new ReviewStable(Path.Combine(ManagementReviewServer(server).Directory,"ShooterGame","Saved"))),lifetime.Token):(await RunManagedScreenJobAsync(server.Id,"managed-backup-create",new ManagedBackupCreate(server.Id,name.Text??""))).Deserialize<ManagedBackupInventory>();state.Notice="Backup created and verified.";}catch(Exception e)when(IsBasicSaveError(e)){state.Notice=e.Message;}finally{state.Busy=false;Render();}
    }
    private async Task DeleteApprovedBackupAsync(ServerDefinition server,ApprovedBackupState state,ManagedBackupEntry entry)
    {
        if(!await ConfirmManagementAsync("Delete backup?",$"Delete “{entry.Name}”? This removes this backup archive.","Delete"))return;state.Busy=true;Render();try{state.Inventory=IsReviewServer(server.Id)||designPreview?await new ManagedBackupStore(ManagementReviewServer(server)).DeleteAsync(entry.Id,lifetime.Token):(await RunManagedScreenJobAsync(server.Id,"managed-backup-delete",new ManagedBackupSelection(server.Id,entry.Id,true))).Deserialize<ManagedBackupInventory>();}catch(Exception e)when(IsBasicSaveError(e)){state.Notice=e.Message;}finally{state.Busy=false;Render();}
    }
    private async Task RestoreApprovedBackupAsync(ServerDefinition server,ApprovedBackupState state,ManagedBackupEntry entry)
    {
        if(!await ConfirmManagementAsync("Restore backup?",$"Restore “{entry.Name}”? This stops this map and replaces its world and player saves. Settings and shared cluster data are preserved.","Restore"))return;state.Busy=true;Render();try
        {
            if(IsReviewServer(server.Id)||designPreview){var target=ManagementReviewServer(server);var store=new ManagedBackupStore(target);var point=await store.VerifyAsync(entry.Id,lifetime.Token);var result=await new WorldRestoreService(_=>{reviewServerRunning=false;return Task.FromResult(true);},_=>Task.CompletedTask,(change,ct)=>change(ct)).RestoreAsync(target,store.ArchivePath(entry.Id),point.ArchiveNumber,Guid.NewGuid(),false,lifetime.Token);state.Notice=result.Message;}
            else{var result=(await RunManagedScreenJobAsync(server.Id,"managed-backup-restore",new ManagedBackupSelection(server.Id,entry.Id,true))).Deserialize<RestoreResult>()!;state.Notice=result.Message;}
        }catch(Exception e)when(IsBasicSaveError(e)){state.Notice=e.Message;}finally{state.Busy=false;Render();}
    }
    private async Task ScheduleApprovedBackupsAsync(ServerDefinition server,ApprovedBackupState state)
    {
        var policy=state.Inventory?.Policy??new();var body=new StackPanel{Margin=new(26),Spacing=18};body.Children.Add(Label("Backup Schedule",26,true));var enabled=new CheckBox{Content="Enable daily backups",IsChecked=policy.Enabled,FontSize=19};body.Children.Add(enabled);var values=new Grid{ColumnDefinitions=new("*,16,*,16,*")};
        var hour=SettingsInteger(policy.Hour.ToString());hour.Maximum=23;var minute=SettingsInteger(policy.Minute.ToString());minute.Maximum=59;var keep=SettingsInteger(policy.Keep.ToString());keep.Minimum=1;keep.Maximum=1000;
        foreach(var (title,number,col) in new[]{("Hour",hour,0),("Minute",minute,2),("Keep backups",keep,4)}){var field=new StackPanel{Spacing=8};field.Children.Add(Label(title,18));field.Children.Add(SettingsNumberControl(number));Grid.SetColumn(field,col);values.Children.Add(field);}body.Children.Add(values);var dialog=ManagementDialog("Backup Schedule",body,height:380);body.Children.Add(EditorButton("Save Changes",()=>dialog.Close(true),true));if(!await dialog.ShowDialog<bool>(this))return;
        state.Busy=true;Render();try{var desired=new ManagedBackupPolicy(true,enabled.IsChecked==true,(int)(hour.Value??3),(int)(minute.Value??0),(int)(keep.Value??7),TimeZoneInfo.Local.Id);state.Inventory=IsReviewServer(server.Id)||designPreview?await new ManagedBackupStore(ManagementReviewServer(server)).ScheduleAsync(desired,lifetime.Token):(await RunManagedScreenJobAsync(server.Id,"managed-backup-schedule",new ManagedBackupSchedule(server.Id,desired))).Deserialize<ManagedBackupInventory>();state.Notice="Backup schedule saved.";}catch(Exception e)when(IsBasicSaveError(e)){state.Notice=e.Message;}finally{state.Busy=false;Render();}
    }
}
