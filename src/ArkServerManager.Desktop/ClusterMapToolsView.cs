namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private bool IsModsViewVisible => managementTab == 2 && !settingsPage && !installationsPage && !activityPage && !creationPage
        && (selectedServer is not null || selectedCluster is not null);
}
