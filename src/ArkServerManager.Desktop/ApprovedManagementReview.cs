using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    public string ManagementFixtureDirectoryForReview=>designPreview?ManagementReviewServer(ReviewServer()).Directory:throw new InvalidOperationException();
    public bool ManagementPageReadyForReview=>designPreview&&approvedBackupStates.TryGetValue(ReviewServerId,out var state)&&state.Inventory is not null&&!state.Loading&&!state.Busy;
    public bool ManagementPlayerReadyForReview=>designPreview&&approvedPlayerStates.TryGetValue(ReviewServerId,out var state)&&state.Inventory is not null&&!state.Loading&&!state.Busy;
    private void SeedApprovedManagementReview()
    {
        if(!designPreview)throw new InvalidOperationException();
        var date=DateTimeOffset.Parse("2026-10-13T08:30:00-04:00");
        approvedBackupStates[ReviewServerId]=new(){Inventory=new(ReviewServerId,[new(Guid.NewGuid(),"Before mod changes","Manual",date,420*1024*1024,1),new(Guid.NewGuid(),"Manual backup","Manual",date.AddDays(-1).AddHours(12.5),415*1024*1024,2),new(Guid.NewGuid(),"Scheduled backup","Scheduled",date.AddDays(-1).AddHours(9.5),410*1024*1024,3)],new(true,true,3,0,7,TimeZoneInfo.Local.Id))};
        playerCategory=1;administrationServer=ReviewServerId;
        approvedPlayerStates[ReviewServerId]=new(){Selected="76561198000004821",Inventory=new(ReviewServerId,[new("Brice","76561198000004821",date.AddDays(-1)),new("SurvivorTwo","76561198000007763",date.AddDays(-1),Admin:true),new("IslandRanger","76561198000002940",date.AddDays(-1))])};
        commandConsoles[ReviewServerId]=new(){LogLines=["[2025.07.15 12:14:03] [Manager] Server setup completed","[2025.07.15 12:14:04] [Manager] Settings saved","[2025.07.15 12:14:07] [Init] Loading configuration...","[2025.07.15 12:14:08] [Init] Configuration loaded","[2025.07.15 12:14:10] [Mods] Found 5 mods","[2025.07.15 12:14:12] [World] TheIsland_WP loaded","[2025.07.15 12:14:13] [Manager] Server stopped","[2025.07.15 12:14:13] [Manager] Saving world...","[2025.07.15 12:14:15] [Save] World saved successfully","[2025.07.15 12:14:15] [Manager] Shutdown complete"]};
    }
}
