using ArkServerManager.Core;
namespace ArkServerManager.Desktop;
public sealed partial class MainWindow
{
    private bool clusterCreationBusy;
    private async Task CreateClusterMapAsync(ClusterDefinition cluster)
    {
        if (client is null || clusterCreationBusy) return;
        clusterCreationBusy = true;
        try
        {
            var current = await client.CallAsync<CatalogDocument>("catalog", null, TimeSpan.FromSeconds(8), lifetime.Token);
            var actual = current.Clusters.Single(item => item.Id == cluster.Id);
            var baseline = current.Servers.FirstOrDefault(server => server.ClusterId == actual.Id);
            ServerPreset? inherited = null;
            if (actual.Defaults is not null) inherited = actual.Defaults;
            else if (baseline is not null)
                inherited = await client.CallAsync<ServerPreset>("presets-capture", new PresetCaptureRequest(baseline, "Cluster defaults"), TimeSpan.FromSeconds(12), lifetime.Token);
            ResetPage(); ResetCreationDraft();
            creationExistingCluster = true; creationClusterId = actual.Id;
            draftCluster = true; draftClusterName = actual.Name; draftEdition = actual.Edition;
            draftName = actual.Name; draftCapacity = actual.Settings.MaxPlayers;
            draftInternet = baseline?.NetworkScope == ServerNetworkScope.Internet;
            draftAseClients = baseline?.AseClients ?? AseClientAccess.Steam;
            draftAsaClients = baseline?.AsaClients ?? AsaClientPlatforms.All;
            creationPreset = new("Cluster defaults", actual.Edition, actual.Settings.Copy(), inherited?.Mods ?? [])
                { UserSettings = inherited?.UserSettings, GameSettings = inherited?.GameSettings };
            if (inherited?.UserSettings is { } settings)
            {
                var ini = new IniDocument(settings);
                draftAdminPassword = ini.GetValues("ServerSettings", "ServerAdminPassword").LastOrDefault() ?? "";
                draftJoinPassword = ini.GetValues("ServerSettings", "ServerPassword").LastOrDefault() ?? "";
                draftMode = bool.TryParse(ini.GetValues("ServerSettings", "ServerPVE").LastOrDefault(), out var pve) && pve ? "PvE" : "PvP";
                draftDescription = ini.GetValues("MessageOfTheDay", "Message").LastOrDefault() ?? "";
                if (int.TryParse(ini.GetValues("MessageOfTheDay", "Duration").LastOrDefault(), out var seconds) && seconds > 0) draftWelcomeDuration = seconds;
            }
            creationStep = 2; creationPage = true; Render();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsBasicSaveError(error))
        { clusterPresetNotice = "Cluster defaults could not be loaded. Refresh before creating a new map."; }
        finally { clusterCreationBusy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }
}
