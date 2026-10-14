using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Input.Platform;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private sealed class ApprovedPlayerState{public ManagedPlayerInventory? Inventory;public string? Selected;public bool Loading,Busy;public string Notice="";public Action? Refresh;}
    private readonly Dictionary<Guid,ApprovedPlayerState> approvedPlayerStates=[];
    private Avalonia.Threading.DispatcherTimer? approvedPlayersTimer;
    private void ApprovedPlayers(ServerDefinition server)
    {
        SelectAdministrationServer(server);
        if(!approvedPlayerStates.TryGetValue(server.Id,out var state)){approvedPlayerStates[server.Id]=state=new();playerCategory=1;Avalonia.Threading.Dispatcher.UIThread.Post(()=>_ = LoadApprovedPlayersAsync(server,state));}
        if(client is not null&&!IsReviewServer(server.Id)&&approvedPlayersTimer is null){approvedPlayersTimer=new(){Interval=TimeSpan.FromSeconds(10)};approvedPlayersTimer.Tick+=(_,_)=>{if(lifetime.IsCancellationRequested){approvedPlayersTimer.Stop();return;}if(managementTab==4&&selectedServer is {} id&&catalog?.Servers.SingleOrDefault(s=>s.Id==id) is {} selected&&approvedPlayerStates.TryGetValue(id,out var current)&&ManagedMapRunning(selected))_ = LoadApprovedPlayersAsync(selected,current);};approvedPlayersTimer.Start();}
        var page=new Grid{ColumnDefinitions=new("1.9*,16,*"),Height=ManagementBodyHeight};
        var left=new Grid{RowDefinitions=new("48,22,20,18,*"),Height=ManagementBodyHeight-42};
        var toolbar=new Grid{ColumnDefinitions=new("*,20,128,14,146,14,140")};var search=EditorText(playerFilter,"Search players...");search.Height=48;Avalonia.Automation.AutomationProperties.SetName(search,"Search players");toolbar.Children.Add(SearchField(search));
        foreach(var (title,index,col) in new[]{("Online",0,2),("All players",1,4),("Banned",2,6)}){var filter=EditorButton(title,()=>{playerCategory=index;Render();},width:index==0?128:index==1?146:140);filter.Height=48;filter.Background=Brush.Parse(playerCategory==index?"#0D3C55":"#102636");filter.BorderBrush=Brush.Parse(playerCategory==index?"#00D8FF":"#32708F");Grid.SetColumn(filter,col);toolbar.Children.Add(filter);}left.Children.Add(toolbar);
        var live=ManagedMapRunning(server)&&!IsReviewServer(server.Id)&&!designPreview;
        var notice=Label(state.Notice.Length>0?state.Notice:live?state.Loading?"Refreshing players…":"Live player status":"Live status unavailable while stopped",17);notice.Foreground=Brush.Parse("#B5D3F1");notice.TextTrimming=TextTrimming.CharacterEllipsis;ToolTip.SetTip(notice,notice.Text);var statusLine=new StackPanel{Orientation=Orientation.Horizontal,Spacing=12,VerticalAlignment=VerticalAlignment.Center};statusLine.Children.Add(VectorIcon("M12,1 A11,11 0 1 0 12,23 A11,11 0 1 0 12,1 M12,6 L12,7 M12,10 L12,18",Brush.Parse(live?"#22BFFF":"#FF5454"),20));statusLine.Children.Add(notice);Grid.SetRow(statusLine,2);left.Children.Add(statusLine);
        var columns=Bounds.Width>=1450?"278,190,217,*":"1.57*,1.07*,1.23*,.83*";var table=new Grid{RowDefinitions=new("58,*")};var heading=new Grid{ColumnDefinitions=new(columns),Margin=new(22,0,42,0)};
        foreach(var (title,col) in new[]{("Player",0),(server.Edition==ArkEdition.ASA?"EOS ID":"Steam ID",1),("Last seen",2),("Access",3)}){var label=Label(title,18);label.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(label,col);heading.Children.Add(label);}table.Children.Add(heading);
        var rows=new StackPanel{Spacing=0};var scroll=new ScrollViewer{Content=rows,VerticalScrollBarVisibility=ScrollBarVisibility.Visible,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,HorizontalContentAlignment=HorizontalAlignment.Stretch};ConfigureEditorScrollbar(scroll,false);Grid.SetRow(scroll,1);table.Children.Add(scroll);var tableFrame=OverviewFrame(table,new(0));approvedPlayerTable=tableFrame;Grid.SetRow(tableFrame,4);left.Children.Add(tableFrame);
        var leftFrame=OverviewFrame(left,new(20,24,20,18));page.Children.Add(leftFrame);
        var detail=new StackPanel{Spacing=0};var rightFrame=OverviewFrame(detail,new(22,26));approvedPlayerDetails=rightFrame;Grid.SetColumn(rightFrame,2);page.Children.Add(rightFrame);
        void Populate()
        {
            rows.Children.Clear();var candidates=(state.Inventory?.Players??[]).Where(p=>(playerCategory!=0||p.Online)&&(playerCategory!=2||p.Banned)&&(playerFilter.Length==0||p.Name.Contains(playerFilter,StringComparison.OrdinalIgnoreCase)||p.AccountId.Contains(playerFilter,StringComparison.OrdinalIgnoreCase))).ToList();
            if(state.Selected is null||!candidates.Any(p=>p.AccountId==state.Selected))state.Selected=candidates.FirstOrDefault()?.AccountId;
            foreach(var player in candidates)
            {
                var row=new Grid{ColumnDefinitions=new(columns),Height=64,Margin=new(22,0,22,0)};
                var name=Label(player.Name,20,true);name.VerticalAlignment=VerticalAlignment.Center;name.TextTrimming=TextTrimming.CharacterEllipsis;row.Children.Add(name);
                var id=Label(MaskedPlayerId(player.AccountId),19);id.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(id,1);row.Children.Add(id);
                var seen=Label(player.LastSeen?.LocalDateTime.ToString("MMM d, yyyy")??"Unknown",18);seen.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(seen,2);row.Children.Add(seen);
                var access=Label(player.Admin?"Admin":"Player",18);access.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(access,3);row.Children.Add(access);
                var button=new Button{Content=row,HorizontalAlignment=HorizontalAlignment.Stretch,Margin=new(0,0,20,0),Padding=new(0),HorizontalContentAlignment=HorizontalAlignment.Stretch,Background=Brush.Parse(state.Selected==player.AccountId?"#0D4057":"#0C1D2A"),BorderBrush=Brush.Parse(state.Selected==player.AccountId?"#00CFFF":"#27536D"),BorderThickness=state.Selected==player.AccountId?new(1):new(0,0,0,1),CornerRadius=new(4)};Avalonia.Automation.AutomationProperties.SetName(button,"Select player "+player.Name);button.Click+=(_,_)=>{state.Selected=player.AccountId;Populate();};rows.Children.Add(button);
            }
            if(candidates.Count==0)rows.Children.Add(new TextBlock{Text=state.Loading?"Loading players…":playerFilter.Length>0?"No matching players":playerCategory==0?live?"No players reported online":"Live status unavailable while stopped":playerCategory==2?"No recorded banned players":"No known players yet",Margin=new(22),FontSize=19,Foreground=Brush.Parse("#A9CAE8")});
            detail.Children.Clear();var selected=candidates.SingleOrDefault(p=>p.AccountId==state.Selected);
            var title=Label(selected?.Name??"Select a player",30,true);title.TextTrimming=TextTrimming.CharacterEllipsis;detail.Children.Add(title);detail.Children.Add(new Border{Height=1,Background=Brush.Parse("#27536D"),Margin=new(0,8,0,28)});
            detail.Children.Add(Label(server.Edition==ArkEdition.ASA?"EOS ID":"Steam ID",20));
            var identity=new Grid{ColumnDefinitions=new("*,66"),Margin=new(0,10,0,28)};var field=EditorText(selected is null?"":MaskedPlayerId(selected.AccountId));field.Height=56;field.IsReadOnly=true;identity.Children.Add(field);Action? copyAction=null;if(selected is not null)copyAction=async ()=>{if(Clipboard is {} clipboard)await clipboard.SetTextAsync(selected.AccountId);};var copy=ManagementButton("",CopyGlyph,copyAction,66,color:"#22BFFF",height:56);Grid.SetColumn(copy,1);identity.Children.Add(copy);detail.Children.Add(identity);
            detail.Children.Add(Label("Access level",20));var accessChoice=new ComboBox{HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch,ItemsSource=new[]{"Player","Admin"},SelectedItem=selected?.Admin==true?"Admin":"Player",IsEnabled=selected is not null&&!state.Busy&&!live,Height=56,Margin=new(0,10,0,0),FontSize=20,Padding=new(18,10),Background=Brush.Parse("#071A27"),BorderBrush=Brush.Parse("#32708F")};accessChoice.SelectionChanged+=(_,_)=>{if(selected is not null&&(accessChoice.SelectedItem as string=="Admin")!=selected.Admin&&!state.Busy)_ = EditApprovedPlayerAsync(server,state,selected,"Admin",accessChoice.SelectedItem as string=="Admin");};detail.Children.Add(accessChoice);
            detail.Children.Add(new Border{Height=1,Background=Brush.Parse("#27536D"),Margin=new(0,34,0,30)});
            var actions=new Grid{ColumnDefinitions=new("*,14,*,14,1.16*")};var kick=ManagementButton("Kick","M4,1 L11,1 L11,12 L20,17 L20,23 L3,23 L3,17 L5,12 Z",selected?.Online==true&&live&&!state.Busy?()=>_ = EditApprovedPlayerAsync(server,state,selected,"Kick",true):null,double.NaN,height:60);actions.Children.Add(kick);
            var ban=ManagementButton(selected?.Banned==true?"Unban":"Ban","M12,1 A11,11 0 1 0 12,23 A11,11 0 1 0 12,1 M5,20 L20,5",selected is null||state.Busy?null:()=>_ = EditApprovedPlayerAsync(server,state,selected,"Ban",!selected.Banned),double.NaN,color:"#FF5454",height:60);Grid.SetColumn(ban,2);actions.Children.Add(ban);
            var allow=ManagementButton(selected?.Allowlisted==true?"Remove":"Allowlist","M9,1 A5,5 0 1 0 9,11 A5,5 0 1 0 9,1 M1,23 L1,20 A8,7 0 0 1 17,20 M20,12 L20,24 M14,18 L26,18",selected is null||state.Busy||live?null:()=>_ = EditApprovedPlayerAsync(server,state,selected,"Allowlist",!selected.Allowlisted),double.NaN,color:"#22BFFF",height:60);ToolTip.SetTip(allow,"Controls the exclusive join allowlist. Exclusive join must be enabled in server settings.");Grid.SetColumn(allow,4);actions.Children.Add(allow);detail.Children.Add(actions);
            if(selected?.BanPending==true)detail.Children.Add(new TextBlock{Text="Ban change queued for next start.",Margin=new(0,20,0,0),FontSize=17,Foreground=Brush.Parse("#A9CAE8")});
        }
        state.Refresh=Populate;search.TextChanged+=(_,_)=>{playerFilter=search.Text??"";Populate();};Populate();content.Children.Add(page);if(pageScroll is not null)pageScroll.VerticalScrollBarVisibility=ScrollBarVisibility.Disabled;
    }
    private static string MaskedPlayerId(string id)=>"••••  "+id[^Math.Min(4,id.Length)..];
    private async Task LoadApprovedPlayersAsync(ServerDefinition server,ApprovedPlayerState state)
    {
        if(state.Loading||state.Busy)return;state.Loading=true;
        try
        {
            if(IsReviewServer(server.Id)||designPreview){await InitializeManagementReviewAsync(server);state.Inventory=await new ManagedPlayerStore(ManagementReviewRoot,ManagementReviewServer(server)).ReadAsync(lifetime.Token);}
            else
            {
                state.Inventory=await client!.CallAsync<ManagedPlayerInventory>("managed-players-read",server.Id,TimeSpan.FromSeconds(8),lifetime.Token);
                if(ManagedMapRunning(server))
                {
                    var observed=await client.CallAsync<ServerPlayerQueryResult>("server-players-read",new ServerPlayerQueryRequest(server.Id),TimeSpan.FromSeconds(8),lifetime.Token);var online=observed.Players.Select(p=>p.AccountId).ToHashSet();state.Inventory=await client.CallAsync<ManagedPlayerInventory>("managed-players-read",server.Id,TimeSpan.FromSeconds(8),lifetime.Token);state.Inventory=state.Inventory with{Players=state.Inventory.Players.Select(p=>p with{Online=online.Contains(p.AccountId)}).ToList()};
                }
            }
        }catch(Exception e)when(IsBasicSaveError(e)){state.Notice="Player data could not be fully refreshed. "+e.Message;}finally{state.Loading=false;if(!lifetime.IsCancellationRequested&&selectedServer==server.Id&&managementTab==4)Render();}
    }
    private async Task EditApprovedPlayerAsync(ServerDefinition server,ApprovedPlayerState state,ManagedPlayer selected,string action,bool enabled)
    {
        state.Busy=true;Render();try
        {
            state.Inventory=IsReviewServer(server.Id)||designPreview?await new ManagedPlayerStore(ManagementReviewRoot,ManagementReviewServer(server)).EditAsync(selected.AccountId,action,enabled,queued:false,ct:lifetime.Token):(await RunManagedScreenJobAsync(server.Id,"managed-player-edit",new ManagedPlayerEdit(server.Id,selected.AccountId,action,enabled))).Deserialize<ManagedPlayerInventory>();
            state.Notice=action=="Ban"&&state.Inventory?.Players.Single(p=>p.AccountId==selected.AccountId).BanPending==true?"Ban change queued for next server start.":action=="Kick"?"Kick command sent.":action=="Admin"?"Access level saved. Applies on next start.":action=="Allowlist"?"Allowlist saved. Applies on next start.":"Player action completed.";
        }catch(Exception e)when(IsBasicSaveError(e)){state.Notice=e.Message;}finally{state.Busy=false;Render();}
    }
}
