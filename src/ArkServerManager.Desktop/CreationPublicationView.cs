using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private sealed class CreationPublicationJob(ServerCreationPublicationRequest selection, GuidedServerConfigurationRequest configuration)
    {
        public readonly ServerCreationPublicationRequest Selection = selection;
        public readonly GuidedServerConfigurationRequest Configuration = configuration;
        public readonly Guid Id = Guid.NewGuid();
        public bool Busy, Accepted, Finished, Failed;
        public string Message = "Creation has not started.";
    }
    private readonly Dictionary<Guid, CreationPublicationJob> creationPublicationJobs = [];
    private readonly List<ServerDefinition> createdArks = [];
    private Guid creationClusterId = Guid.NewGuid();
    private GuidedServerConfigurationRequest? creationClusterDefaults;
    private bool? creationClusterInternet;

    private bool CanPublishCreation()
    {
        if (client is null || creationCheck is null || checkedCreationRequest != CreationCheckRequest()
            || !creationConfigurationJobs.TryGetValue(creationServerId, out var configuration)
            || !configuration.Finished || configuration.Failed) return false;
        return ConfigurationFingerprint(configuration.Selection) == ConfigurationFingerprint(CreationConfigurationRequest(creationCheck));
    }

    private void BeginCreationPublication()
    {
        if (!CanPublishCreation()) return;
        if (!creationPublicationJobs.TryGetValue(creationServerId, out var view) || view.Failed)
        {
            var configuration = creationConfigurationJobs[creationServerId].Selection;
            var server = configuration.Server! with { ClusterId = draftCluster == true ? creationClusterId : null };
            var cluster = draftCluster == true && createdArks.Count == 0 && !creationExistingCluster
                ? new ClusterDefinition(creationClusterId, draftClusterName, server.Edition, server.Settings.Copy()) : null;
            if (cluster is not null)
            {
                var files = GuidedServerConfiguration.Build(configuration);
                cluster = cluster with { Defaults = new ServerPreset("Cluster defaults", server.Edition, server.Settings.Copy(), InheritedCreationMods(server.Mods, draftCustomMap, draftMapModId))
                    { UserSettings = files.UserSettings, GameSettings = files.GameSettings } };
            }
            view = new(new(server, cluster), configuration); creationPublicationJobs[creationServerId] = view;
        }
        _ = TrackCreationPublicationAsync(view);
    }

    private async Task TrackCreationPublicationAsync(CreationPublicationJob view)
    {
        if (client is null || view.Busy || view.Finished || lifetime.IsCancellationRequested) return;
        view.Busy = true; view.Message = "Connecting to server creation…"; Render();
        try
        {
            var job = await client.CallAsync<BackgroundOperation>(view.Accepted ? "operation-status" : "server-creation-publish-start",
                view.Accepted ? new OperationStatusRequest(view.Id) : (object)view.Selection,
                TimeSpan.FromSeconds(8), lifetime.Token, view.Accepted ? null : view.Id);
            while (true)
            {
                if (job.Id != view.Id || job.Kind != "server-creation" || !Enum.IsDefined(job.State))
                    throw new InvalidDataException("Unexpected server creation result.");
                view.Accepted = true; var message = job.Progress?.Message ?? "Server creation is queued.";
                if (job.State == BackgroundOperationState.Succeeded)
                {
                    var result = job.Result?.Deserialize<ServerCreationPublicationRecord>();
                    if (result is null || result.SchemaVersion != 1 || result.ServerId != view.Selection.Server.Id
                        || result.Phase != ServerCreationPublicationPhase.Registered)
                        throw new InvalidDataException("Created server could not be verified.");
                    view.Finished = true; message = "Server created.";
                    if (creationServerId == view.Selection.Server.Id)
                    {
                        if (!createdArks.Any(s => s.Id == view.Selection.Server.Id)) createdArks.Add(view.Selection.Server);
                        if (draftCluster == true && creationClusterDefaults is null)
                        {
                            var prepared = creationConfigurationJobs[view.Selection.Server.Id];
                            var original = view.Configuration;
                            creationClusterDefaults = new()
                            {
                                Server = original.Server! with { Mods = InheritedCreationMods(original.Server.Mods, prepared.CustomMap, prepared.MapModId) },
                                PvE = original.PvE, AdministratorPassword = original.AdministratorPassword, JoinPassword = original.JoinPassword,
                                WelcomeMessage = original.WelcomeMessage, WelcomeDurationSeconds = original.WelcomeDurationSeconds,
                                UserSettingsTemplate = original.UserSettingsTemplate, GameSettingsTemplate = original.GameSettingsTemplate
                            };
                            creationClusterInternet = draftInternet;
                        }
                        creationStep = 5;
                    }
                    await RefreshAsync();
                }
                else if (job.State is BackgroundOperationState.Failed or BackgroundOperationState.Canceled)
                { view.Finished = view.Failed = true; message = job.Error ?? "Creation stopped. Review the setup before retrying."; }
                if (message != view.Message) { view.Message = message; Render(); }
                if (view.Finished) break;
                await Task.Delay(1000, lifetime.Token);
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(view.Id), TimeSpan.FromSeconds(8), lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or InvalidOperationException or OperationCanceledException or JsonException or ArgumentException)
        { view.Message = "Connection lost. Creation may still be running. Reconnect to check its result."; }
        finally { view.Busy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }

    private void RenderCreationCompletion()
    {
        var body = new StackPanel { Width = 760, MaxWidth = 760, Spacing = 24, HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 60, 0, 32) };
        var check = Label("✓", 56, true); check.Foreground = Brush.Parse("#64DD8A");
        check.HorizontalAlignment = HorizontalAlignment.Center; check.VerticalAlignment = VerticalAlignment.Center;
        body.Children.Add(new Border { Width = 96, Height = 96, CornerRadius = new CornerRadius(48),
            BorderBrush = Brush.Parse("#64DD8A"), BorderThickness = new Thickness(3),
            HorizontalAlignment = HorizontalAlignment.Center, Child = check });
        var title = Label(draftCluster == true ? "Your cluster is taking shape" : "Your ARK is created", 36, true);
        title.HorizontalAlignment = HorizontalAlignment.Center; body.Children.Add(title);
        if (draftCluster == true) { var name = Label(draftClusterName, 25); name.Foreground = Muted; name.HorizontalAlignment = HorizontalAlignment.Center; body.Children.Add(name); }
        foreach (var server in createdArks)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24 };
            row.Children.Add(MapPicture(server.Map == "TheIsland_WP" ? "TheIsland" : server.Map, 220, 125));
            var details = new StackPanel { Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
            details.Children.Add(Label(MapName(server.Map == "TheIsland_WP" ? "TheIsland" : server.Map) + " · " + server.Edition, 24, true));
            details.Children.Add(Label(server.Settings.Name, 17));
            var status = Label("●  Created · Stopped", 17); status.Foreground = Brush.Parse("#64DD8A"); details.Children.Add(status);
            row.Children.Add(details); body.Children.Add(Surface(row));
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, HorizontalAlignment = HorizontalAlignment.Center };
        if (draftCluster == true) actions.Children.Add(ActionButton("＋  Create another ARK", AddAnotherCreatedArk, true));
        if (createdArks.LastOrDefault() is { } latest && latest.NetworkScope == ServerNetworkScope.Internet)
            actions.Children.Add(ActionButton("Network setup", () => { ResetPage(); selectedServer = latest.Id; managementTab = 1; settingsCategory = 1; Render(); }));
        actions.Children.Add(ActionButton("Finish", () => { ResetPage(); Render(); })); body.Children.Add(actions);
        var note = Label(draftCluster == true ? "New ARKs inherit cluster settings. You can edit their overrides." : "Your server files and settings have been created.", 16);
        note.Foreground = Muted; note.HorizontalAlignment = HorizontalAlignment.Center; body.Children.Add(note); content.Children.Add(body);
    }

    private void AddAnotherCreatedArk()
    {
        if (creationClusterDefaults?.Server is not { } baseline) return;
        creationServerId = Guid.NewGuid(); creationStep = 2; draftMap = null;
        draftCustomMap = false; draftMapModId = "";
        creationModDraft = null;
        draftMapFilter = "";
        draftName = baseline.Settings.Name; draftCapacity = baseline.Settings.MaxPlayers;
        draftMode = creationClusterDefaults.PvE == true ? "PvE" : "PvP";
        draftAdminPassword = creationClusterDefaults.AdministratorPassword; draftJoinPassword = creationClusterDefaults.JoinPassword;
        draftDescription = creationClusterDefaults.WelcomeMessage;
        draftWelcomeDuration = creationClusterDefaults.WelcomeDurationSeconds;
        draftInternet = creationClusterInternet;
        draftAseClients = baseline.AseClients;
        draftAsaClients = baseline.AsaClients;
        creationPreset = new("Cluster defaults", baseline.Edition, baseline.Settings.Copy(), [.. baseline.Mods])
            { UserSettings = creationClusterDefaults.UserSettingsTemplate, GameSettings = creationClusterDefaults.GameSettingsTemplate };
        customizationCategory = 0; checkedCreationRequest = null; creationCheck = null; creationCheckMessage = creationValidation = "";
        Render();
    }

    private static List<string> InheritedCreationMods(IReadOnlyList<string> mods, bool customMap, string mapModId)
        => mods.Where(mod => !customMap || !ulong.TryParse(mapModId, out var mapId) || !ulong.TryParse(mod, out var modId) || modId != mapId).ToList();
}
