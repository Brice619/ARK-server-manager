using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private readonly string managedReviewRoot=Path.Combine(Path.GetTempPath(),"ark-management-review",Guid.NewGuid().ToString("N"));
    private readonly Dictionary<(Guid,string),Guid> managedScreenJobs=[];
    private string ManagementReviewRoot=>designPreview?managedReviewRoot:throw new InvalidOperationException("Review fixtures are unavailable in the installed app.");
    private ServerDefinition ManagementReviewServer(ServerDefinition source)
    {
        var directory=Path.Combine(ManagementReviewRoot,"app-settings","solo-management-review","server");
        return source with{Directory=directory};
    }
    private async Task InitializeManagementReviewAsync(ServerDefinition source)
    {
        if(!designPreview)throw new InvalidOperationException("Use the isolated visual review fixture.");
        var server=ManagementReviewServer(source);var world=Path.Combine(server.Directory,"ShooterGame","Saved","SavedArks");
        ManagedBackupStore.RejectLinks(world);Directory.CreateDirectory(world);var path=Path.Combine(world,"review-world.txt");if(!File.Exists(path))await File.WriteAllTextAsync(path,"Isolated visual review world fixture.\n",lifetime.Token);
        var players=new ManagedPlayerStore(ManagementReviewRoot,server);
        await players.SeedAsync([new("Brice","76561198000004821",DateTimeOffset.Parse("2026-10-12T12:00:00Z")),new("SurvivorTwo","76561198000007763",DateTimeOffset.Parse("2026-10-12T12:00:00Z")),new("IslandRanger","76561198000002940",DateTimeOffset.Parse("2026-10-12T12:00:00Z"))],lifetime.Token);
        var seeded=Path.Combine(server.Directory,".review-initialized");if(!File.Exists(seeded)){await players.EditAsync("76561198000007763","Admin",true,ct:lifetime.Token);await File.WriteAllTextAsync(seeded,"Fixture seed complete",lifetime.Token);}
    }
    private sealed class ReviewStable(string directory):IStableServerState
    {public string Directory=>directory;public ValueTask DisposeAsync()=>ValueTask.CompletedTask;}
    private async Task<JsonElement> RunManagedScreenJobAsync(Guid server,string method,object payload)
    {
        if(client is null)throw new InvalidOperationException("The background manager is unavailable.");
        var key=(server,method);var reconnect=managedScreenJobs.TryGetValue(key,out var requestId);if(!reconnect)managedScreenJobs[key]=requestId=Guid.NewGuid();
        var job=reconnect?await client.CallAsync<BackgroundOperation>("operation-status",new OperationStatusRequest(requestId),TimeSpan.FromSeconds(8),lifetime.Token):await client.CallAsync<BackgroundOperation>(method,payload,TimeSpan.FromSeconds(8),lifetime.Token,requestId);
        while(job.State is BackgroundOperationState.Queued or BackgroundOperationState.Running){await Task.Delay(300,lifetime.Token);job=await client.CallAsync<BackgroundOperation>("operation-status",new OperationStatusRequest(requestId),TimeSpan.FromSeconds(8),lifetime.Token);if(job.Id!=requestId)throw new InvalidDataException("Unexpected action result.");}
        managedScreenJobs.Remove(key);if(job.State!=BackgroundOperationState.Succeeded)throw new InvalidOperationException(job.Error??"The action did not complete.");return job.Result??throw new InvalidDataException("The action returned no result.");
    }
    private Window ManagementDialog(string title,Control body,double width=580,double height=330)
    {
        body.Opacity=0;body.Transitions=[new DoubleTransition{Property=OpacityProperty,Duration=TimeSpan.FromMilliseconds(220)}];
        var dialog=new Window{Title=title,Width=width,Height=height,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Brush.Parse("#0C1C29"),FontFamily=FontFamily,Content=body};dialog.Opened+=(_,_)=>body.Opacity=1;return dialog;
    }
    private async Task<bool> ConfirmManagementAsync(string title,string message,string action)
    {
        var body=new StackPanel{Margin=new(26),Spacing=24};body.Children.Add(Label(title,26,true));var caption=Label(message,19);caption.TextWrapping=TextWrapping.Wrap;body.Children.Add(caption);
        var dialog=ManagementDialog(title,body);var buttons=new StackPanel{Orientation=Orientation.Horizontal,Spacing=20,HorizontalAlignment=HorizontalAlignment.Right};buttons.Children.Add(EditorButton("Cancel",()=>dialog.Close(false),width:130));buttons.Children.Add(EditorButton(action,()=>dialog.Close(true),true,160));body.Children.Add(buttons);return await dialog.ShowDialog<bool>(this);
    }
}
