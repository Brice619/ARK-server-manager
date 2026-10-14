using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using ArkServerManager.Core;
namespace ArkServerManager.Desktop;
public sealed partial class MainWindow
{
    private void SeedApprovedClusterActivityReview(ClusterDefinition cluster,ServerDefinition[] members)
    {
        if(!designPreview)throw new InvalidOperationException("Activity samples require an isolated design review.");
        var names=new[]{"TheIsland","ScorchedEarth","Aberration","Ragnarok"};
        Guid Map(string name)=>members.FirstOrDefault(s=>s.Map==name)?.Id??members[0].Id;
        var date=new DateTimeOffset(2026,10,14,9,15,0,TimeSpan.FromHours(-4));
        history=new(new[]{new ActivityEntry(Guid.NewGuid(),date,"server",ActivitySeverity.Information,"Server started",Map(names[0]),Stage:"started"),new(Guid.NewGuid(),date.AddMinutes(-5),"backup",ActivitySeverity.Information,"Cluster backup created",cluster.Id),new(Guid.NewGuid(),date.AddMinutes(-10),"settings",ActivitySeverity.Information,"Settings saved",Map(names[1])),new(Guid.NewGuid(),date.AddMinutes(-15),"mods",ActivitySeverity.Information,"Mod list saved",Map(names[2])),new(Guid.NewGuid(),date.AddMinutes(-20),"transfers",ActivitySeverity.Information,"Transfer rules saved",cluster.Id),new(Guid.NewGuid(),date.AddMinutes(-25),"server",ActivitySeverity.Information,"Server stopped",Map(names[3]),Stage:"stopped")},false,0);
    }
    public void VerifyClusterActivityActionsForReview()
    {
        if(!designPreview||selectedCluster is null||managementTab!=6)throw new InvalidOperationException("Use the isolated cluster Activity fixture.");
        void Pump(){Avalonia.Threading.Dispatcher.UIThread.RunJobs();UpdateLayout();}
        bool Has(string text)=>this.GetVisualDescendants().OfType<TextBlock>().Any(t=>t.Text==text);
        var search=this.GetVisualDescendants().OfType<TextBox>().Single(t=>Avalonia.Automation.AutomationProperties.GetName(t)=="Search cluster activity");
        var combos=this.GetVisualDescendants().OfType<ComboBox>().ToArray();var map=combos.Single(c=>c.Items.Contains("All maps"));var events=combos.Single(c=>c.Items.Contains("All events"));
        search.Text="Transfer rules";Pump();if(!Has("Transfer rules saved")||Has("Server started"))throw new InvalidOperationException("Activity search did not filter rows.");search.Text="";Pump();
        map.SelectedItem="The Island";Pump();if(!Has("Server started")||Has("Cluster backup created"))throw new InvalidOperationException("Map filter leaked unrelated activity.");map.SelectedItem="All maps";Pump();
        events.SelectedItem="Stopped";Pump();if(!Has("Server stopped")||Has("Server started"))throw new InvalidOperationException("Event filter failed.");
        var clear=this.GetVisualDescendants().OfType<Button>().Single(b=>Avalonia.Automation.AutomationProperties.GetName(b)=="Clear Filters");clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();if(!Has("Server started")||!Has("Cluster backup created")||clusterActivityEvent!="All events"||clusterActivityMap!="All maps")throw new InvalidOperationException("Clear Filters failed.");
        var time=this.GetVisualDescendants().OfType<Button>().Single(b=>Avalonia.Automation.AutomationProperties.GetName(b)=="Time ▾");time.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();if(clusterActivityNewest)throw new InvalidOperationException("Activity time sorting did not toggle.");
        var details=this.GetVisualDescendants().OfType<TextBlock>().Where(t=>t.Text is "Server started" or "Server stopped" or "Cluster backup created" or "Settings saved" or "Mod list saved" or "Transfer rules saved").Select(t=>t.Text).ToArray();if(details.First()!="Server stopped")throw new InvalidOperationException("Ascending time sort is incorrect.");
        var cluster=catalog!.Clusters.Single(c=>c.Id==selectedCluster);history=new(history!.Entries.Concat(new[]{new ActivityEntry(Guid.NewGuid(),DateTimeOffset.UtcNow,"server",ActivitySeverity.Information,"Other cluster event",Guid.NewGuid()),new(Guid.NewGuid(),DateTimeOffset.UtcNow,"server",ActivitySeverity.Information,"Global event")}).ToArray(),false,0);refreshClusterActivityRows?.Invoke();Pump();if(Has("Other cluster event")||Has("Global event"))throw new InvalidOperationException("Activity scope leaked unrelated records.");
        clusterActivityNewest=true;refreshClusterActivityRows?.Invoke();Pump();
    }
}
