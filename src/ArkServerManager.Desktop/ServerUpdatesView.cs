using System.Text.Json;
using Avalonia.Controls;
using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private Guid? updateViewServer;
    private Guid updateRequestId;
    private bool updateCheckBusy, updateCheckAccepted, updateCheckFailed;
    private string updateCheckNotice = "";
    private ServerUpdateCheckResult? updateCheckResult;

    private void BeginServerUpdateCheck(ServerDefinition server)
    {
        if (updateCheckBusy) return;
        updateViewServer = server.Id; updateRequestId = Guid.NewGuid(); updateCheckAccepted = false;
        updateCheckFailed = false;
        updateCheckResult = null; updateCheckNotice = "";
        Render(); _ = CheckServerUpdateAsync(server.Id);
    }

    private void RenderServerUpdate(ServerDefinition server)
    {
        content.Children.Add(Label("Server update", 32, true));
        var body = new StackPanel { Spacing = 20 };
        body.Children.Add(Label(server.Settings.Name, 23, true));
        body.Children.Add(Label(updateCheckNotice.Length > 0 ? updateCheckNotice : "Checking the available version…", 17));
        if (updateCheckResult?.Result.Queued == true)
        {
            body.Children.Add(Label("An update is available. Restart only this map now, or keep the update queued until it stops.", 16));
            body.Children.Add(ActionButton("Stop, update and restart", client is null || lifecycleBusy ? null : () =>
            {
                updateViewServer = null; Render(); _ = RunLifecycleActionAsync(server.Id, LifecycleAction.Restart, memberOnly: true);
            }, true));
            body.Children.Add(ActionButton("Keep queued", () => { updateViewServer = null; Render(); }));
        }
        else if (!updateCheckBusy && updateCheckResult is null)
            body.Children.Add(ActionButton(updateCheckFailed ? "Try again" : "Check existing action", () =>
            {
                if (updateCheckFailed) BeginServerUpdateCheck(server);
                else _ = CheckServerUpdateAsync(server.Id);
            }));
        body.Children.Add(ActionButton("Back to server", () => { updateViewServer = null; Render(); }));
        content.Children.Add(Surface(body));
    }

    private async Task CheckServerUpdateAsync(Guid serverId)
    {
        if (client is null || designPreview || updateCheckBusy) return;
        updateCheckBusy = true; Render();
        try
        {
            var job = await client.CallAsync<BackgroundOperation>(updateCheckAccepted ? "operation-status" : "server-update-check-start",
                updateCheckAccepted ? new OperationStatusRequest(updateRequestId) : new ServerUpdateCheckRequest(serverId),
                TimeSpan.FromSeconds(10), lifetime.Token, updateRequestId);
            updateCheckAccepted = true;
            while (true)
            {
                if (job.Id != updateRequestId || job.Kind != "server-update-check" || !Enum.IsDefined(job.State))
                    throw new InvalidDataException("Unexpected update action.");
                if (job.State == BackgroundOperationState.Succeeded)
                {
                    var result = job.Result?.Deserialize<ServerUpdateCheckResult>();
                    if (result is null || result.ServerId != serverId || result.AvailableBuild == 0) throw new InvalidDataException();
                    updateCheckResult = result; updateCheckNotice = result.Result.Message; break;
                }
                if (job.State is BackgroundOperationState.Failed or BackgroundOperationState.Canceled)
                { updateCheckFailed = true; updateCheckNotice = job.Error ?? "The update check could not finish."; break; }
                updateCheckNotice = job.Progress?.Message ?? "Checking the server update…"; Render();
                await Task.Delay(1000, lifetime.Token);
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(updateRequestId),
                    TimeSpan.FromSeconds(10), lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or TimeoutException or ArgumentException or InvalidOperationException or JsonException or OperationCanceledException)
        { updateCheckNotice = "Update result unknown. Check the existing action to reconnect."; }
        finally { updateCheckBusy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }
}
