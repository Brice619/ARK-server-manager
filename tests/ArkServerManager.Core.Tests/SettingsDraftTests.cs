using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class SettingsDraftTests
{
    [Fact]
    public void IndexedStatsAndDocumentedEngramClassesSaveWithoutChangingOtherEntries()
    {
        const string section="/Script/ShooterGame.ShooterGameMode";
        var engrams=DocumentedSettings.VanillaEngrams;
        Assert.Equal("EngramEntry_StoneHatchet_C",engrams.Single(e=>e.Name=="Stone Hatchet").ClassName);Assert.Equal("EngramEntry_SimpleBed_C",engrams.Single(e=>e.Name=="Simple Bed").ClassName);
        var edits=new List<SettingsValueEdit>{new(section,"PerLevelStatsMultiplier_Player[7]",["2.5"]),new(section,"PerLevelStatsMultiplier_DinoTamed[0]",[DocumentedSettings.StatDefault("PerLevelStatsMultiplier_DinoTamed",0)]),new(section,"OverrideNamedEngramEntries",engrams.Select(e=>$"(EngramClassName=\"{e.ClassName}\",EngramPointsCost={e.Points},EngramLevelRequirement={e.Level},EngramHidden=False,RemoveEngramPreReq=False)").ToList())};
        var output=SettingsDraft.Apply("; retained\n[Other]\nModValue=unchanged\n",edits);var ini=new IniDocument(output);
        Assert.Equal("2.5",ini.GetValues(section,"PerLevelStatsMultiplier_Player[7]").Single());Assert.Equal("0.2",ini.GetValues(section,"PerLevelStatsMultiplier_DinoTamed[0]").Single());Assert.Equal(4,ini.GetValues(section,"OverrideNamedEngramEntries").Count);Assert.Contains("ModValue=unchanged",output);Assert.Empty(ConfigurationSyntax.Check(output));
        Assert.Throws<ArgumentException>(()=>new IniDocument("").SetValues(section,"[InjectedSection]",["1"]));Assert.Throws<ArgumentException>(()=>new IniDocument("").SetValues(section,"Stat[not-an-index]",["1"]));
    }
    [Fact]
    public async Task ConfigurationSaveCreatesMissingGameAndUpdatesLaunchFields()
    {
        var root=Path.Combine(Path.GetTempPath(),"ark-settings-binding-"+Guid.NewGuid().ToString("N"));
        try
        {
            var repository=new CatalogRepository(Path.Combine(root,"catalog.json"));var server=new ServerDefinition(Guid.NewGuid(),ArkEdition.ASE,"TheIsland",Path.Combine(root,"server"),new(){Name="Fixture",GamePort=7777,QueryPort=27015,RconPort=32330});await repository.ChangeAsync(c=>c.AddServer(server));
            var binary=Path.Combine(server.Directory,"ShooterGame","Binaries",OperatingSystem.IsWindows()?"Win64":"Linux",OperatingSystem.IsWindows()?"ShooterGameServer.exe":"ShooterGameServer");Directory.CreateDirectory(Path.GetDirectoryName(binary)!);await File.WriteAllTextAsync(binary,"never launched");
            var config=Path.Combine(server.Directory,"ShooterGame","Saved","Config",OperatingSystem.IsWindows()?"WindowsServer":"LinuxServer");Directory.CreateDirectory(config);var user=Path.Combine(config,"GameUserSettings.ini");await File.WriteAllTextAsync(user,"[ServerSettings]\nServerPassword=keep\n");
            var queue=new PendingConfigurationEdits(repository,Path.Combine(root,"pending.json"),(_,_)=>Task.CompletedTask);
            await queue.EnqueueAsync(Guid.NewGuid(),new(server.Id,"Game.ini",SettingsConfigurationBinding.MissingFileHash,"[/Script/ShooterGame.ShooterGameMode]\nEggHatchSpeedMultiplier=3\n"));await queue.ApplyBeforeStartAsync(server.Id);Assert.Contains("EggHatchSpeedMultiplier=3",await File.ReadAllTextAsync(Path.Combine(config,"Game.ini")));
            var original=await ServerConfigurationReader.ReadAsync(repository,new(server.Id,"GameUserSettings.ini"));var desired=SettingsDraft.Apply(original.Text!,[new("SessionSettings","SessionName",["Saved fixture"]),new("SessionSettings","Port",["7787"]),new("/Script/Engine.GameSession","MaxPlayers",["12"])]);
            await queue.EnqueueAsync(Guid.NewGuid(),new(server.Id,"GameUserSettings.ini",original.SourceSHA256!,desired));await queue.ApplyBeforeStartAsync(server.Id);
            var saved=(await repository.ReadAsync()).Servers.Single();Assert.Equal(7787,saved.Settings.GamePort);Assert.Equal(12,saved.Settings.MaxPlayers);Assert.Equal("Saved fixture",saved.Settings.Name);Assert.Contains("ServerPassword=keep",await File.ReadAllTextAsync(user));
            var launch=NativeServerLaunchPlan.Build(saved,new(OperatingSystem.IsWindows()?ServerPlatform.Windows:ServerPlatform.Linux,true,null),Path.Combine(root,"console.log"));Assert.Contains(launch.Arguments,a=>a.Contains("Port=7787"));
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Fact]
    public async Task DummyFilesPersistSeparatelyAndRejectStaleSaves()
    {
        var root=Path.Combine(Path.GetTempPath(),"ark-settings-"+Guid.NewGuid().ToString("N"));
        try
        {
            var id=Guid.NewGuid();var original=SettingsDraft.ReadPreview(root,id,"GameUserSettings.ini");
            var text=SettingsDraft.Apply("; preserved comment\n[ServerSettings]\nServerPassword=keep-this\n",[new("SessionSettings","SessionName",["Dummy"]),new("/Script/Engine.GameSession","MaxPlayers",["12"])]);
            await SettingsDraft.SavePreviewAsync(root,original,text,default);
            var reopened=SettingsDraft.ReadPreview(root,id,"GameUserSettings.ini");Assert.Equal(text,reopened.Text);Assert.NotNull(reopened.SourceSHA256);
            Assert.Contains("; preserved comment",reopened.Text);Assert.Contains("ServerPassword=keep-this",reopened.Text);
            await Assert.ThrowsAsync<IOException>(()=>SettingsDraft.SavePreviewAsync(root,original,"[Other]\nValue=wrong\n",default));
            Assert.Null(SettingsDraft.ReadPreview(root,id,"Game.ini").Text);Assert.False(File.Exists(Path.Combine(root,"catalog.json")));
            await SettingsDraft.SavePreviewAsync(root,SettingsDraft.ReadPreview(root,id,"Game.ini"),"[/Script/ShooterGame.ShooterGameMode]\nEggHatchSpeedMultiplier=2.5\n",default);
            Assert.Contains("EggHatchSpeedMultiplier=2.5",SettingsDraft.ReadPreview(root,id,"Game.ini").Text);
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Fact]
    public void RepeatedOverridesRetainUnknownTupleMembersAndUnrelatedSettings()
    {
        var tuple=new IniTuple("(EngramClassName=\"EngramEntry_Campfire_C\",EngramPointsCost=3,Custom=(Nested=1,Value=2))");tuple.Set("EngramPointsCost","7");
        var result=SettingsDraft.Apply("; keep\n[/Script/ShooterGame.ShooterGameMode]\nOther=unchanged\nOverrideNamedEngramEntries=(EngramClassName=\"Other_C\",EngramPointsCost=2)\n",[new("/Script/ShooterGame.ShooterGameMode","OverrideNamedEngramEntries",["(EngramClassName=\"Other_C\",EngramPointsCost=2)",tuple.ToString()])]);
        Assert.Contains("Other=unchanged",result);Assert.Contains("Custom=(Nested=1,Value=2)",result);Assert.Equal(2,new IniDocument(result).GetValues("/Script/ShooterGame.ShooterGameMode","OverrideNamedEngramEntries").Count);
    }
    [Fact]
    public async Task LaunchPreferencesPersistAndRejectStaleEdits()
    {
        var root=Path.Combine(Path.GetTempPath(),"ark-launch-settings-"+Guid.NewGuid().ToString("N"));
        try
        {
            var repository=new CatalogRepository(Path.Combine(root,"catalog.json"));var server=new ServerDefinition(Guid.NewGuid(),ArkEdition.ASE,"TheIsland",Path.Combine(root,"server"),new(){Name="Fixture",GamePort=7777,QueryPort=27015,RconPort=32330});await repository.ChangeAsync(c=>c.AddServer(server));
            var expected=await ServerLaunchPreferencesStore.ReadAsync(repository,server.Id,default);var desired=new ServerLaunchPreferences("-NoBattlEye ?AllowFlyerCarryPvE=True",true,true,true);
            await ServerLaunchPreferencesStore.SaveAsync(repository,new(expected,desired,ServerNetworkScope.LocalNetwork),default);
            var reopened=await ServerLaunchPreferencesStore.ReadAsync(new CatalogRepository(Path.Combine(root,"catalog.json")),server.Id,default);Assert.Equal(desired,reopened.Preferences);
            var arguments=new List<string>{"TheIsland?listen"};reopened.Preferences.Apply(arguments);Assert.DoesNotContain("-USEALLAVAILABLECORES",arguments);Assert.Contains("-servergamelog",arguments);Assert.Contains("-AutoDestroyStructures",arguments);Assert.Contains("TheIsland?listen?AllowFlyerCarryPvE=True",arguments);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>ServerLaunchPreferencesStore.SaveAsync(repository,new(expected,new(),ServerNetworkScope.LocalNetwork),default));
            Assert.Throws<ArgumentException>(()=>new ServerLaunchPreferences("?Port=9000").Tokens());
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
