namespace ArkServerManager.Core;

public enum CreationMode { Standalone, Cluster }
public enum CreationPhase { Configure, Creating, ClusterSummary, Finished }
public sealed record ServerDraft(string Name, string Map, ServerSettings Settings, IReadOnlyList<string> Mods);

/// <summary>Creates new catalog entries only. Adding existing entries to clusters is a separate editing operation.</summary>
public sealed class CreationWorkflow
{
    private readonly CatalogRepository repository;
    private readonly string root;
    private readonly Func<ServerDefinition, CancellationToken, Task> provision;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly List<Guid> created = [];
    private readonly CreationMode mode;
    private readonly ArkEdition edition;
    private readonly string? clusterName;
    private Guid? clusterId;
    public CreationPhase Phase { get; private set; } = CreationPhase.Configure;
    public IReadOnlyList<Guid> CreatedServerIds => created.ToArray();

    public CreationWorkflow(CatalogRepository repository, string root, CreationMode mode, ArkEdition edition,
        string? clusterName, Func<ServerDefinition, CancellationToken, Task> provision)
    {
        if (!Enum.IsDefined(mode) || !Enum.IsDefined(edition)) throw new ArgumentException("Invalid creation mode or game.");
        if (mode == CreationMode.Cluster && string.IsNullOrWhiteSpace(clusterName)) throw new ArgumentException("Enter a cluster name.");
        this.repository = repository;
        this.root = Path.GetFullPath(root);
        this.mode = mode;
        this.edition = edition;
        this.clusterName = clusterName;
        this.provision = provision;
    }

    public async Task<ServerDefinition> CreateAsync(ServerDraft draft, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (Phase != CreationPhase.Configure) throw new InvalidOperationException("The guide is not ready to create another server.");
            if (string.IsNullOrWhiteSpace(draft.Name) || string.IsNullOrWhiteSpace(draft.Map)) throw new ArgumentException("Enter a name and choose a map.");
            if (draft.Mods.Any(string.IsNullOrWhiteSpace) || draft.Mods.Distinct().Count() != draft.Mods.Count)
                throw new ArgumentException("Each mod must have a unique ID.");
            var existing = await repository.ReadAsync(cancellationToken);
            using var ports = new PortAllocator().Reserve(existing.Servers.Select(s => s.Settings));
            var settings = draft.Settings.Copy() with
            { Name = draft.Name, GamePort = ports.GamePort, QueryPort = ports.QueryPort, RconPort = ports.RconPort };
            settings.Validate();
            var id = Guid.NewGuid();
            var newClusterId = mode == CreationMode.Cluster ? clusterId ?? Guid.NewGuid() : (Guid?)null;
            var server = new ServerDefinition(id, edition, draft.Map, Path.Combine(root, id.ToString("N")), settings, newClusterId)
            { Mods = [.. draft.Mods] };
            var isFirstClusterMember = mode == CreationMode.Cluster && clusterId is null;
            Phase = CreationPhase.Creating;
            try
            {
                // Provisioning installs files only. It must obtain consent and never launch a server before commit.
                await provision(server, cancellationToken);
                await repository.ChangeAsync(catalog =>
                {
                    if (isFirstClusterMember) catalog.AddCluster(new(newClusterId!.Value, clusterName!, edition, settings.Copy()));
                    catalog.AddServer(server);
                }, cancellationToken);
                clusterId = newClusterId;
                created.Add(id);
                Phase = mode == CreationMode.Cluster ? CreationPhase.ClusterSummary : CreationPhase.Finished;
                return server;
            }
            catch
            {
                Phase = CreationPhase.Configure;
                // Preserve provisioned files after failure rather than deleting an uncommitted installation.
                throw;
            }
        }
        finally { gate.Release(); }
    }

    public async Task CreateAnotherAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (mode != CreationMode.Cluster || Phase != CreationPhase.ClusterSummary)
                throw new InvalidOperationException("Create another is available after a cluster member is created.");
            Phase = CreationPhase.Configure;
        }
        finally { gate.Release(); }
    }

    public async Task FinishAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (Phase != CreationPhase.ClusterSummary) throw new InvalidOperationException("Create a server before finishing the cluster.");
            Phase = CreationPhase.Finished;
        }
        finally { gate.Release(); }
    }
}
