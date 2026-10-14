using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private int playerCategory;
    private string playerFilter = "";
    private string consoleFilter = "";
    private bool consolePaused;
    private Guid? administrationServer;

    private void SelectAdministrationServer(ServerDefinition server)
    {
        if (administrationServer == server.Id) return;
        administrationServer = server.Id; playerCategory = 0; playerFilter = consoleFilter = "";
        consolePaused = false;
    }

}
