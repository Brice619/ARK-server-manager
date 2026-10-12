using Avalonia.Controls;
using ArkServerManager.Core;
using System.Text.Json;
using System.Security.Cryptography;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private sealed class CreationConfigurationJob(GuidedServerConfigurationRequest selection, bool cluster, string clusterName, bool internet,
        bool customMap = false, string mapModId = "")
    {
        public readonly GuidedServerConfigurationRequest Selection = selection;
        public readonly bool Cluster = cluster, Internet = internet;
        public readonly string ClusterName = clusterName;
        public readonly bool CustomMap = customMap;
        public readonly string MapModId = mapModId;
        public readonly Guid Id = Guid.NewGuid();
        public bool Busy, Accepted, Finished, Failed;
        public string Message = "Configuration has not been prepared.";
    }
    private readonly Dictionary<Guid, CreationConfigurationJob> creationConfigurationJobs = [];

    private void AddCreationConfiguration(StackPanel body, ServerCreationCheck check)
    {
        if (!installationJobs.TryGetValue(creationServerId, out var installation)
            || installation.Result is null || !installation.Finished || installation.Failed) return;
        var request = CreationConfigurationRequest(check);
        creationConfigurationJobs.TryGetValue(creationServerId, out var view);
        if (view is not null && ConfigurationFingerprint(view.Selection) != ConfigurationFingerprint(request))
        {
            body.Children.Add(Label("These choices differ from the prepared setup. You can return to the prepared choices and check them again.", 15));
            body.Children.Add(ActionButton("Return to prepared choices", view.Busy ? null : () => RestorePreparedCreationChoices(view)));
            return;
        }
        if (view is not null) body.Children.Add(Label(view.Message, 15));
        if (view?.Finished == true && !view.Failed) return;
        body.Children.Add(ActionButton(view?.Busy == true ? "Preparing configuration…"
            : view?.Accepted == true && !view.Finished ? "Reconnect to preparation"
            : view?.Failed == true ? "Retry configuration preparation" : "Prepare server configuration",
            client is null || view?.Busy == true ? null : () =>
            {
                if (view is null || view.Failed)
                { view = new(request, draftCluster == true, draftClusterName, draftInternet == true, draftCustomMap, draftMapModId); creationConfigurationJobs[creationServerId] = view; }
                _ = TrackCreationConfigurationAsync(view);
            }));
    }

    private void RestorePreparedCreationChoices(CreationConfigurationJob job)
    {
        if (job.Busy || job.Selection.Server is not { } server || server.Id != creationServerId) return;
        draftEdition = server.Edition;
        draftCustomMap = job.CustomMap; draftMapModId = job.MapModId;
        draftAseClients = server.AseClients;
        draftAsaClients = server.AsaClients;
        draftMap = server.Map == "TheIsland_WP" ? "TheIsland" : server.Map;
        draftName = server.Settings.Name; draftCapacity = server.Settings.MaxPlayers;
        draftMode = job.Selection.PvE == true ? "PvE" : "PvP";
        draftAdminPassword = job.Selection.AdministratorPassword; draftJoinPassword = job.Selection.JoinPassword;
        draftDescription = job.Selection.WelcomeMessage;
        draftWelcomeDuration = job.Selection.WelcomeDurationSeconds;
        draftCluster = job.Cluster; draftClusterName = job.ClusterName; draftInternet = job.Internet;
        creationPreset = new("Prepared settings", server.Edition, server.Settings.Copy(), [.. server.Mods])
            { UserSettings = job.Selection.UserSettingsTemplate, GameSettings = job.Selection.GameSettingsTemplate };
        // Fresh location and port checks are still required; this only restores the in-memory review choices.
        checkedCreationRequest = null; creationCheck = null; creationValidation = "";
        creationCheckMessage = "Prepared choices restored. Check the server location and ports again before creating.";
        creationStep = 4; Render();
    }

    private GuidedServerConfigurationRequest CreationConfigurationRequest(ServerCreationCheck check) => new()
        {
            Server = new(creationServerId, draftEdition!.Value, ServerMapIdentifier.ForLaunch(draftEdition.Value, draftMap!), check.Directory,
                new() { Name = draftName, MaxPlayers = (int)draftCapacity!.Value, GamePort = check.GamePort,
                    QueryPort = check.QueryPort, RconPort = check.RconPort,
                    Values = creationPreset is null ? [] : new(creationPreset.Settings.Values) })
                { Mods = CreationModIds(),
                    NetworkScope = draftInternet == true ? ServerNetworkScope.Internet : ServerNetworkScope.LocalNetwork,
                    AseClients = draftAseClients, AsaClients = draftAsaClients },
            PvE = draftMode switch { "PvE" => true, "PvP" => false, _ => null }, AdministratorPassword = draftAdminPassword, JoinPassword = draftJoinPassword,
            WelcomeMessage = draftDescription, WelcomeDurationSeconds = draftWelcomeDuration is decimal duration ? (int)duration : null,
            UserSettingsTemplate = creationPreset?.UserSettings, GameSettingsTemplate = creationPreset?.GameSettings
        };

    private static string ConfigurationFingerprint(GuidedServerConfigurationRequest request)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));

    private async Task TrackCreationConfigurationAsync(CreationConfigurationJob view)
    {
        if (client is null || view.Busy || view.Finished || lifetime.IsCancellationRequested) return;
        view.Busy = true; view.Message = "Connecting to configuration preparation…"; Render();
        try
        {
            var job = await client.CallAsync<BackgroundOperation>(view.Accepted ? "operation-status" : "server-creation-guided-configure-start",
                view.Accepted ? new OperationStatusRequest(view.Id) : (object)view.Selection,
                TimeSpan.FromSeconds(8), lifetime.Token, view.Accepted ? null : view.Id);
            while (true)
            {
                if (job.Id != view.Id || job.Kind != "server-configuration-preparation" || !Enum.IsDefined(job.State))
                    throw new InvalidDataException("Unexpected configuration preparation result.");
                view.Accepted = true;
                var message = job.Progress?.Message ?? "Configuration preparation is queued.";
                if (job.State == BackgroundOperationState.Succeeded)
                {
                    var result = job.Result?.Deserialize<InitialServerConfigurationResult>();
                    if (result is null || result.ServerId != view.Selection.Server!.Id || !result.Configured)
                        throw new InvalidDataException("Prepared configuration could not be verified.");
                    view.Finished = true; message = "Configuration prepared. Final server creation is the next step.";
                }
                else if (job.State is BackgroundOperationState.Failed or BackgroundOperationState.Canceled)
                { view.Finished = view.Failed = true; message = job.Error ?? "Preparation stopped. Review this setup before retrying."; }
                if (message != view.Message) { view.Message = message; Render(); }
                if (view.Finished) break;
                await Task.Delay(1000, lifetime.Token);
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(view.Id), TimeSpan.FromSeconds(8), lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or InvalidOperationException or OperationCanceledException or JsonException or ArgumentException)
        { view.Message = "Connection lost. Preparation may still be running. Reconnect to check its result."; }
        finally { view.Busy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }
}
