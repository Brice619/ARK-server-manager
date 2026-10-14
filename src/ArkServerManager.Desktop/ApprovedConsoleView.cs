using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform.Storage;
using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private int approvedConsoleTab;
    private string consoleLevel="All levels";
    private readonly Dictionary<Guid,HashSet<string>> hiddenConsoleLines=[];
    private const string TrashGlyph="M3,5 L21,5 M8,5 L8,1 L16,1 L16,5 M5,5 L6,23 L18,23 L19,5 M10,9 L10,19 M14,9 L14,19";
    private const string FolderGlyph="M1,5 L9,5 L12,8 L23,8 L23,22 L1,22 Z M1,5 L1,2 L9,2 L12,5 L23,5 L23,8";
    private const string CopyGlyph="M8,2 L22,2 L22,18 L18,18 M2,7 L17,7 L17,23 L2,23 Z";
    private const string RefreshGlyph="M20,8 A9,9 0 0 0 4,7 L1,10 M1,3 L1,10 L8,10 M4,16 A9,9 0 0 0 20,17 L23,14 M23,21 L23,14 L16,14";
    private bool ManagedMapRunning(ServerDefinition server)=>managedRuntimeStatus.Any(s=>s.ServerId==server.Id&&s.State==ServerState.Running);
    private double ManagementBodyHeight=>Math.Clamp(Bounds.Height>0?Bounds.Height-452:572,310,572);
    private Button ManagementButton(string title,string glyph,Action? action,double width,bool gold=false,string? color=null,double height=48)
    {
        var button=EditorButton(title,action,gold,width);button.Height=height;
        var brush=Brush.Parse(action is null?"#6080A4":color??(gold?"#FFDA43":"#DCEBFF"));if(double.IsNaN(width))button.HorizontalAlignment=HorizontalAlignment.Stretch;
        var line=new StackPanel{Orientation=Orientation.Horizontal,Spacing=14,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
        if(glyph.Length>0)line.Children.Add(VectorIcon(glyph,brush,25));var text=Label(title,19);text.Foreground=brush;text.VerticalAlignment=VerticalAlignment.Center;line.Children.Add(text);button.Content=line;
        if(action is null){button.Background=Brush.Parse("#152838");button.BorderBrush=Brush.Parse("#29465C");}
        if(color is not null&&action is not null){button.BorderBrush=brush;button.Foreground=brush;button.Background=Brush.Parse(color=="#FF5454"?"#35191E":"#08324C");}
        return button;
    }
    private void ApprovedConsole(ServerDefinition server)
    {
        SelectAdministrationServer(server);
        if(!commandConsoles.TryGetValue(server.Id,out var view))commandConsoles[server.Id]=view=new();
        if((designPreview||IsReviewServer(server.Id))&&view.LogLines.Count==0)
            view.LogLines=["[2026.10.14 12:14:03] [Manager] Dummy server preview created", "[2026.10.14 12:14:04] [Manager] Settings loaded", "[2026.10.14 12:14:07] [Init] Loading configuration...", "[2026.10.14 12:14:08] [Init] Configuration loaded", "[2026.10.14 12:14:10] [Mods] 5 selected mods", "[2026.10.14 12:14:13] [Manager] Dummy server is stopped"];
        if(designPreview||IsReviewServer(server.Id))view.LogNotice="";
        if(client is not null&&!IsReviewServer(server.Id))
        {
            if(consoleLogTimer is null){consoleLogTimer=new(){Interval=TimeSpan.FromSeconds(3)};consoleLogTimer.Tick+=(_,_)=>{if(lifetime.IsCancellationRequested){consoleLogTimer.Stop();return;}if(managementTab==5&&selectedServer is {} id&&catalog?.Servers.SingleOrDefault(s=>s.Id==id) is {} selected&&commandConsoles.TryGetValue(id,out var state))_ = RefreshConsoleLogsAsync(selected,state);};consoleLogTimer.Start();}
            if(view.LogLines.Count==0&&!view.ReadingLogs)_ = RefreshConsoleLogsAsync(server,view);
        }
        var shell=new Grid{RowDefinitions=new("48,18,*,12,42,12,1,16,56"),Height=ManagementBodyHeight-34};
        var toolbar=new Grid{ColumnDefinitions=new("*,308,14,154,14,178")};
        var tabs=new StackPanel{Orientation=Orientation.Horizontal,Spacing=20};
        foreach(var (title,index) in new[]{("Logs",0),("RCON",1)}){var tab=EditorButton(title,()=>{approvedConsoleTab=index;Render();},width:100);tab.Height=48;tab.Background=Brushes.Transparent;tab.BorderThickness=new(0);tabs.Children.Add(new Border{Child=tab,BorderBrush=approvedConsoleTab==index?Brush.Parse("#00E6EE"):Brush.Parse("#27536D"),BorderThickness=new(0,0,0,approvedConsoleTab==index?3:1)});}toolbar.Children.Add(tabs);
        var search=EditorText(consoleFilter,"Search logs...");search.Height=48;Avalonia.Automation.AutomationProperties.SetName(search,"Search logs");var searchField=SearchField(search);Grid.SetColumn(searchField,1);toolbar.Children.Add(searchField);
        var levels=new ComboBox{HorizontalAlignment=HorizontalAlignment.Stretch,ItemsSource=new[]{"All levels","Information","Warning","Error"},SelectedItem=consoleLevel,Height=48,Padding=new(12,6),FontSize=18,Background=Brush.Parse("#102636"),BorderBrush=Brush.Parse("#32708F")};Grid.SetColumn(levels,3);toolbar.Children.Add(levels);
        string[] VisibleLines()=> (approvedConsoleTab==0?view.LogLines:view.Responses.Select(r=>$"[{r.ReceivedAt.LocalDateTime:yyyy.MM.dd HH:mm:ss}] [RCON] {r.Response}").ToArray()).Where(line=>line.Contains(consoleFilter,StringComparison.OrdinalIgnoreCase)&&!(hiddenConsoleLines.GetValueOrDefault(server.Id)?.Contains(line)??false)&&(consoleLevel=="All levels"||LogLevel(line)==consoleLevel)).ToArray();
        var export=ManagementButton("Export Log","M12,1 L12,16 M6,10 L12,16 L18,10 M2,16 L2,23 L22,23 L22,16",()=>_ = ExportApprovedLogAsync(VisibleLines()),178);Grid.SetColumn(export,5);toolbar.Children.Add(export);shell.Children.Add(toolbar);
        var rows=new StackPanel{Spacing=6,Margin=new(16,14,18,14)};
        var output=new ScrollViewer{Content=rows,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Visible};ConfigureEditorScrollbar(output);
        var outputFrame=OverviewFrame(output,new(0));approvedConsoleOutput=outputFrame;Grid.SetRow(outputFrame,2);shell.Children.Add(outputFrame);
        void Populate(){var old=output.Offset;rows.Children.Clear();foreach(var line in VisibleLines())rows.Children.Add(ConsoleLine(line));if(rows.Children.Count==0)rows.Children.Add(Label(approvedConsoleTab==1?"No RCON responses yet.":view.LogNotice.Length>0?view.LogNotice:"No log lines to display.",18));Avalonia.Threading.Dispatcher.UIThread.Post(()=>{if(consolePaused)output.Offset=old;else output.ScrollToEnd();});}
        view.RefreshOutput=Populate;search.TextChanged+=(_,_)=>{consoleFilter=search.Text??"";Populate();};levels.SelectionChanged+=(_,_)=>{consoleLevel=levels.SelectedItem as string??"All levels";Populate();};Populate();
        var controls=new StackPanel{Orientation=Orientation.Horizontal,Spacing=28,HorizontalAlignment=HorizontalAlignment.Right};var follow=new StackPanel{Orientation=Orientation.Horizontal,Spacing=14,VerticalAlignment=VerticalAlignment.Center};follow.Children.Add(SettingsToggle(!consolePaused,on=>{consolePaused=!on;if(on)output.ScrollToEnd();}));follow.Children.Add(Label("Follow output",17));controls.Children.Add(follow);controls.Children.Add(ManagementButton("Clear View",TrashGlyph,()=>{hiddenConsoleLines[server.Id]=new(view.LogLines);view.Responses.Clear();Populate();},174,height:42));Grid.SetRow(controls,4);shell.Children.Add(controls);
        var divider=new Border{Height=1,Background=Brush.Parse("#27536D")};Grid.SetRow(divider,6);shell.Children.Add(divider);
        var commandRow=new Grid{ColumnDefinitions=new("*,16,130,24,200")};var running=!IsReviewServer(server.Id)&&!designPreview&&ManagedMapRunning(server);
        var command=EditorText(view.Draft,"Enter RCON command...");command.Height=56;command.Resources["TextControlBackgroundDisabled"]=Brush.Parse("#182B3D");command.Resources["TextControlBorderBrushDisabled"]=Brush.Parse("#32708F");command.Resources["TextControlForegroundDisabled"]=Brush.Parse("#6080A4");command.IsEnabled=running&&!view.Busy&&view.Pending is null;Avalonia.Automation.AutomationProperties.SetName(command,"Administrator command");command.TextChanged+=(_,_)=>view.Draft=command.Text??"";commandRow.Children.Add(command);
        var send=ManagementButton(view.Pending is not null?"Check result":view.Busy?"Sending…":"Send","M1,10 L24,1 L16,24 L11,14 Z M11,14 L24,1",client is not null&&(running||view.Pending is not null)&&!view.Busy?()=>_ = SendConsoleCommandAsync(server,view):null,130,height:56);Grid.SetColumn(send,2);commandRow.Children.Add(send);command.KeyDown+=(_,e)=>{if(e.Key==Avalonia.Input.Key.Enter&&send.IsEnabled){view.Draft=command.Text??"";e.Handled=true;_ = SendConsoleCommandAsync(server,view);}};
        var status=Label(running&&view.Responses.Count>0?"RCON connected":"RCON disconnected",16);status.Foreground=Brush.Parse("#A9CAE8");status.VerticalAlignment=VerticalAlignment.Center;ToolTip.SetTip(status,view.Notice);Grid.SetColumn(status,4);commandRow.Children.Add(status);Grid.SetRow(commandRow,8);shell.Children.Add(commandRow);
        content.Children.Add(OverviewFrame(shell,new(16,18)));if(pageScroll is not null)pageScroll.VerticalScrollBarVisibility=ScrollBarVisibility.Disabled;
    }
    private static string LogLevel(string line)=>line.Contains("error",StringComparison.OrdinalIgnoreCase)||line.Contains("fatal",StringComparison.OrdinalIgnoreCase)?"Error":line.Contains("warn",StringComparison.OrdinalIgnoreCase)?"Warning":"Information";
    private Control ConsoleLine(string line)
    {
        var text=new TextBlock{FontFamily=new("Consolas"),FontSize=19,Foreground=Brush.Parse("#EDF0FF"),TextWrapping=TextWrapping.NoWrap};
        var timestampEnd=line.IndexOf(']');if(line.StartsWith('[')&&timestampEnd>=0){text.Inlines!.Add(new Avalonia.Controls.Documents.Run(line[..(timestampEnd+1)]){Foreground=Brush.Parse("#9EC8EE")});text.Inlines.Add(new Avalonia.Controls.Documents.Run("  "));line=line[(timestampEnd+1)..].TrimStart();}
        var start=line.IndexOf('[');var end=start>=0?line.IndexOf(']',start):-1;
        if(start>=0&&end>start){text.Inlines!.Add(new Avalonia.Controls.Documents.Run(line[..start]));var tag=line[start..(end+1)];text.Inlines.Add(new Avalonia.Controls.Documents.Run(tag){Foreground=Brush.Parse(tag.Contains("World")?"#69F57B":tag.Contains("Save")?"#FFDA43":tag.Contains("Mods")?"#00EDFF":tag.Contains("Manager")?"#00B9FF":"#D9E4FF")});line=line[(end+1)..];}
        text.Inlines!.Add(new Avalonia.Controls.Documents.Run(line));return text;
    }
    private async Task ExportApprovedLogAsync(string[] lines)
    {
        if(!StorageProvider.CanSave)return;
        using var file=await StorageProvider.SaveFilePickerAsync(new(){Title="Export Log",SuggestedFileName="ark-server-log.txt",DefaultExtension="txt"});if(file is null)return;
        await using var stream=await file.OpenWriteAsync();stream.SetLength(0);await using var writer=new StreamWriter(stream);await writer.WriteAsync(string.Join(Environment.NewLine,lines));
    }
}
