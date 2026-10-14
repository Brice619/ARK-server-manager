using Avalonia.VisualTree;
using ArkServerManager.Core;
namespace ArkServerManager.Desktop;
public sealed partial class MainWindow
{
    public void VerifyClusterBackupsActionsForReview()
    {
        if(!designPreview||selectedCluster is not {} id)throw new InvalidOperationException();
        void Pump(){Avalonia.Threading.Dispatcher.UIThread.RunJobs();UpdateLayout();}
        var state=clusterBackupPageStates[id];var search=this.GetVisualDescendants().OfType<Avalonia.Controls.TextBox>().Single();search.Text="scheduled";Pump();
        if(this.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().Any(t=>t.Text=="Before mod changes")||!this.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().Any(t=>t.Text=="Scheduled backup"))throw new InvalidOperationException("Cluster backup search failed.");search.Text="";Pump();
        var check=this.GetVisualDescendants().OfType<Avalonia.Controls.CheckBox>().Single(c=>c.Content?.ToString()=="The Island");check.IsChecked=false;Pump();if(state.Inventory!.Maps!.Count!=4)throw new InvalidOperationException("Map scope did not update.");
        var shared=this.GetVisualDescendants().OfType<Avalonia.Controls.CheckBox>().Single(c=>c.Content?.ToString()=="Transfer data (players, configs, etc.)");shared.IsChecked=false;Pump();if(state.Inventory.IncludeShared)throw new InvalidOperationException("Shared scope did not update.");
        check=this.GetVisualDescendants().OfType<Avalonia.Controls.CheckBox>().Single(c=>c.Content?.ToString()=="The Island");check.IsChecked=true;Pump();shared=this.GetVisualDescendants().OfType<Avalonia.Controls.CheckBox>().Single(c=>c.Content?.ToString()=="Transfer data (players, configs, etc.)");shared.IsChecked=true;Pump();
        var header=this.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().Single(t=>t.Text=="Explorer Cluster");if(header.GetVisualAncestors().OfType<Avalonia.Controls.Control>().Any(c=>c.Opacity<.99))throw new InvalidOperationException("Cluster backup scope faded header.");
    }
    private void SeedApprovedClusterBackupsReview(ClusterDefinition cluster,ServerDefinition[] members)
    {
        if(!designPreview)throw new InvalidOperationException();var maps=members.Select(s=>s.Id).ToList();var time=DateTimeOffset.Parse("2026-10-14T08:30:00-04:00");
        ManagedClusterBackupEntry Entry(string name,DateTimeOffset date,double gb)=>new(Guid.NewGuid(),name,name=="Scheduled backup",new(1,cluster.Id,cluster.Edition,date.UtcTicks,new(Guid.NewGuid(),date),maps),true,(long)(gb*1024*1024*1024));
        clusterBackupPageStates[cluster.Id]=new(){Inventory=new(cluster.Id,[Entry("Before mod changes",time,1.8),Entry("Manual backup",time.AddDays(-1).AddHours(12.5),1.7),Entry("Scheduled backup",time.AddDays(-1).AddHours(-5.5),1.7)],new(true,true,3,0,7,TimeZoneInfo.Local.Id),maps,true)};
    }
}
