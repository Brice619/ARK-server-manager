using System.Text.Json;

namespace ArkServerManager.Core;

public sealed record ServerCreationPublicationRequest(ServerDefinition Server, ClusterDefinition? NewCluster);
public sealed class InitialServerConfigurationRequest
{
    public Guid ServerId { get; init; }
    public string UserSettings { get; init; } = "";
    public string GameSettings { get; init; } = "";
    public override string ToString() => $"Configuration for setup {ServerId}; contents redacted";
}
public sealed record InitialServerConfigurationResult(Guid ServerId, bool Configured);

public sealed class ServerCreationManagerOperations(CatalogRepository repository, string managementRoot,
    BackgroundOperations operations, SemaphoreSlim fileOperationGate,
    Func<ServerInstallationRecord, Func<CancellationToken, Task>, CancellationToken, Task> withStoppedSetup)
{
    public object StartGuidedConfiguration(ManagerRequest request)
    {
        var selection = request.Payload?.Deserialize<GuidedServerConfigurationRequest>()
            ?? throw new ArgumentException("Review the guided setup choices.");
        return SubmitConfiguration(request, GuidedServerConfiguration.Build(selection), selection.Server);
    }

    public object StartConfiguration(ManagerRequest request)
    {
        var selection = request.Payload?.Deserialize<InitialServerConfigurationRequest>()
            ?? throw new ArgumentException("Review the new server configuration.");
        if (selection.ServerId == Guid.Empty || selection.UserSettings is null || selection.GameSettings is null)
            throw new ArgumentException("Choose a valid setup and prepared configuration.");
        return SubmitConfiguration(request, selection);
    }

    private object SubmitConfiguration(ManagerRequest request, InitialServerConfigurationRequest selection, ServerDefinition? expectedServer = null)
    {
        return operations.Submit(request.Id, "server-configuration-preparation", request.Payload, async (progress, token) =>
        {
            await fileOperationGate.WaitAsync(token);
            try
            {
                progress(new("Checking stopped setup files and preparing configuration."));
                await InitialServerConfiguration.WriteAsync(managementRoot, selection.ServerId,
                    selection.UserSettings, selection.GameSettings, async (record, change, stoppedToken) =>
                    {
                        if (expectedServer is not null && (record.ServerId != expectedServer.Id
                            || record.Edition != expectedServer.Edition || record.FinalDirectory != expectedServer.Directory))
                            throw new InvalidOperationException("The guided choices no longer match the installed setup.");
                        await withStoppedSetup(record, change, stoppedToken);
                    }, token);
                progress(new("Configuration prepared. Server creation can continue."));
                return new InitialServerConfigurationResult(selection.ServerId, true);
            }
            finally { fileOperationGate.Release(); }
        });
    }

    public object StartPublication(ManagerRequest request)
    {
        var selection = request.Payload?.Deserialize<ServerCreationPublicationRequest>()
            ?? throw new ArgumentException("Review the prepared server before creating it.");
        if (selection.Server is null || selection.Server.Id == Guid.Empty || selection.Server.Settings is null)
            throw new ArgumentException("Choose a valid prepared server.");
        selection.Server.Settings.Validate();
        ServerMapIdentifier.ForLaunch(selection.Server.Edition, selection.Server.Map);
        return operations.Submit(request.Id, "server-creation", request.Payload, async (progress, token) =>
        {
            await fileOperationGate.WaitAsync(token);
            try
            {
                progress(new("Checking the prepared server, configuration and reviewed ports."));
                var result = await ServerCreationPublication.ExecuteAsync(repository, managementRoot,
                    selection.Server, selection.NewCluster, withStoppedSetup, token);
                progress(new("Server created. It is ready for a separate start request."));
                return result;
            }
            finally { fileOperationGate.Release(); }
        });
    }
}
