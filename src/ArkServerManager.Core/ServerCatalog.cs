namespace ArkServerManager.Core;

public enum ArkEdition { ASE, ASA }
public enum AseClientAccess { Steam, SteamAndEpic, Epic }
public enum ServerState { Stopped, Starting, Running, Stopping, Recovering, Failed }

public sealed record ServerSettings
{
    public string Name { get; init; } = "";
    public int MaxPlayers { get; init; } = 70;
    public int GamePort { get; init; }
    public int QueryPort { get; init; }
    public int RconPort { get; init; }
    public Dictionary<string, string> Values { get; init; } = [];

    public ServerSettings Copy() => this with { Values = new(Values) };
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("Enter a server name.");
        if (MaxPlayers < 1) throw new ArgumentException("Player capacity must be positive.");
        var ports = new[] { GamePort, GamePort + 1, QueryPort, RconPort };
        if (ports.Any(p => p is < 1 or > 65535) || ports.Distinct().Count() != 4)
            throw new ArgumentException("Choose distinct valid ports, including the port following the game port.");
    }
}

public sealed record ServerDefinition(Guid Id, ArkEdition Edition, string Map, string Directory,
    ServerSettings Settings, Guid? ClusterId = null)
{
    public List<ClusterSettingOverride> ClusterOverrides { get; init; } = [];
    public bool OverrideClusterMaxPlayers { get; init; }
    public bool OverrideClusterMods { get; init; }
    public List<string> Mods { get; init; } = [];
    public ServerNetworkScope NetworkScope { get; init; } = ServerNetworkScope.LocalNetwork;
    public AseClientAccess AseClients { get; init; } = AseClientAccess.Steam;
    public AsaClientPlatforms AsaClients { get; init; } = AsaClientPlatforms.All;
}

public sealed record ClusterDefinition(Guid Id, string Name, ArkEdition Edition, ServerSettings Settings)
{
    public ServerPreset? Defaults { get; init; }
}
public sealed record ServerPreset(string Name, ArkEdition Edition, ServerSettings Settings, List<string> Mods)
{
    public string? UserSettings { get; init; }
    public string? GameSettings { get; init; }
}

/// <summary>Catalog rules only. Disk and process effects belong to the management service.</summary>
public sealed class ServerCatalog
{
    private readonly Dictionary<Guid, ServerDefinition> servers = [];
    private readonly Dictionary<Guid, ClusterDefinition> clusters = [];
    public IReadOnlyList<ServerDefinition> Servers => servers.Values.Select(Clone).ToArray();
    public IReadOnlyList<ClusterDefinition> Clusters => clusters.Values.Select(CloneCluster).ToArray();

    public void AddCluster(ClusterDefinition cluster)
    {
        if (cluster.Id == Guid.Empty || !Enum.IsDefined(cluster.Edition)) throw new ArgumentException("Invalid cluster identity.");
        if (string.IsNullOrWhiteSpace(cluster.Name)) throw new ArgumentException("Enter a cluster name.");
        if (cluster.Defaults is { } preset && preset.Edition != cluster.Edition) throw new ArgumentException("Cluster defaults target another ARK edition.");
        if (cluster.Defaults is { } defaults) PresetLibrary.ReadImport(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new PresetExportDocument(1, defaults)));
        clusters.Add(cluster.Id, CloneCluster(cluster));
    }

    public void UpdateClusterDefaults(Guid id, string name, int maxPlayers)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.Any(char.IsControl) || maxPlayers < 1)
            throw new ArgumentException("Enter a cluster name and positive default player capacity.");
        var current = clusters[id];
        var defaults = current.Defaults;
        if (defaults is not null)
        {
            var user = new IniDocument(defaults.UserSettings ?? "");
            user.SetValues("ServerSettings", "MaxPlayers", [maxPlayers.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            defaults = defaults with { Settings = defaults.Settings.Copy() with { MaxPlayers = maxPlayers }, UserSettings = user.ToString() };
        }
        clusters[id] = current with { Name = name.Trim(), Settings = current.Settings.Copy() with { Name = name.Trim(), MaxPlayers = maxPlayers }, Defaults = defaults };
    }

    public void SetClusterDefaultPreset(Guid id, ServerPreset preset)
    {
        var cluster = clusters[id];
        if (preset.Edition != cluster.Edition) throw new ArgumentException("Choose defaults for this cluster's ARK edition.");
        PresetLibrary.ReadImport(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new PresetExportDocument(1, preset)));
        clusters[id] = CloneCluster(cluster with { Defaults = preset,
            Settings = cluster.Settings.Copy() with { MaxPlayers = preset.Settings.MaxPlayers, Values = new(preset.Settings.Values) } });
    }

    public void AddServer(ServerDefinition server)
    {
        if (server.Id == Guid.Empty || !Enum.IsDefined(server.Edition) || !Enum.IsDefined(server.NetworkScope) || !Enum.IsDefined(server.AseClients)) throw new ArgumentException("Invalid server identity, client access or network scope.");
        if (string.IsNullOrWhiteSpace(server.Map) || string.IsNullOrWhiteSpace(server.Directory))
            throw new ArgumentException("A map and server folder are required.");
        server.Settings.Validate();
        ClusterSettingOverrides.Validate(server.ClusterOverrides);
        if (server.Edition == ArkEdition.ASA) AsaClientPlatformArguments.Build(server.AsaClients);
        if (server.ClusterId is Guid clusterId)
        {
            var cluster = clusters[clusterId];
            if (cluster.Edition != server.Edition) throw new ArgumentException("ASA and ASE cannot share a cluster.");
        }
        EnsurePortsAvailable(server);
        servers.Add(server.Id, Clone(server));
    }

    public void AddExistingServerToCluster(Guid serverId, Guid clusterId)
    {
        var server = servers[serverId];
        var cluster = clusters[clusterId];
        if (server.ClusterId.HasValue) throw new InvalidOperationException("Moving an existing cluster member is not supported.");
        if (server.Edition != cluster.Edition) throw new ArgumentException("ASA and ASE cannot share a cluster.");
        // Listener ports identify the local process and must stay unique across members.
        // Cluster port inheritance is an unresolved product decision, so do not copy those ports.
        var inherited = cluster.Settings.Copy() with
        {
            GamePort = server.Settings.GamePort,
            QueryPort = server.Settings.QueryPort,
            RconPort = server.Settings.RconPort
        };
        inherited.Validate();
        servers[serverId] = server with { ClusterId = clusterId, Settings = inherited };
    }

    public IReadOnlyList<Guid> OperationTargets(Guid serverId)
    {
        var server = servers[serverId];
        return server.ClusterId is Guid clusterId
            ? servers.Values.Where(s => s.ClusterId == clusterId).Select(s => s.Id).ToArray()
            : [serverId];
    }

    /// <summary>Changes grouping only. The host must coordinate stopped processes and shared storage.</summary>
    public void SetClusterMembership(IReadOnlyList<Guid> serverIds, Guid? clusterId)
    {
        ArgumentNullException.ThrowIfNull(serverIds);
        if (serverIds.Count == 0 || serverIds.Any(id => id == Guid.Empty)
            || serverIds.Distinct().Count() != serverIds.Count)
            throw new ArgumentException("Select distinct servers to regroup.");
        // Validate the entire selection before changing any member.
        var selected = serverIds.Select(id => servers[id]).ToArray();
        if (clusterId is Guid target)
        {
            var cluster = clusters[target];
            if (selected.Any(server => server.Edition != cluster.Edition))
                throw new ArgumentException("ASA and ASE cannot share a cluster.");
        }
        foreach (var server in selected)
            servers[server.Id] = server with { ClusterId = clusterId };
    }

    /// <summary>Catalog mutation only; the caller coordinates validated file deletion and transaction recovery.</summary>
    public void RemoveServer(Guid serverId)
    {
        if (!servers.Remove(serverId)) throw new KeyNotFoundException("The selected server no longer exists.");
        // Cluster removal is a separate catalog operation.
    }

    public void ApplyPreset(Guid serverId, ServerPreset preset)
    {
        var server = servers[serverId];
        if (server.Edition != preset.Edition) throw new ArgumentException("The preset targets a different ARK edition.");
        RequireModClientCompatibility(server, preset.Mods.Count);
        preset.Settings.Validate();
        var replacement = server with { Settings = preset.Settings.Copy(), Mods = [.. preset.Mods] };
        EnsurePortsAvailable(replacement, serverId);
        servers[serverId] = replacement;
    }

    public void UpdateSettings(Guid serverId, ServerSettings settings)
    {
        settings.Validate();
        var replacement = servers[serverId] with { Settings = settings.Copy() };
        EnsurePortsAvailable(replacement, serverId);
        servers[serverId] = replacement;
    }
    public void SetClusterOverrides(Guid serverId, IReadOnlyList<ClusterSettingOverride> overrides, bool maxPlayers, bool mods)
    {
        ClusterSettingOverrides.Validate(overrides);
        var server = servers[serverId];
        if (server.ClusterId is null) throw new ArgumentException("Choose a map belonging to a cluster.");
        servers[serverId] = server with { ClusterOverrides = [.. overrides], OverrideClusterMaxPlayers = maxPlayers, OverrideClusterMods = mods };
    }

    public void UpdateAseClients(Guid serverId, AseClientAccess expected, AseClientAccess desired)
    {
        if (!Enum.IsDefined(desired) || !Enum.IsDefined(expected)) throw new ArgumentException("Choose supported player clients.");
        var server = servers[serverId];
        if (server.Edition != ArkEdition.ASE) throw new ArgumentException("These client options apply to ASE.");
        if (server.AseClients != expected) throw new InvalidOperationException("Player client settings changed. Reload before saving.");
        if (desired != AseClientAccess.Steam && server.Mods.Count > 0)
            throw new InvalidOperationException("ASE Workshop mods require Steam clients.");
        servers[serverId] = server with { AseClients = desired };
    }

    public void UpdateAsaClients(Guid serverId, AsaClientPlatforms expected, AsaClientPlatforms desired)
    {
        AsaClientPlatformArguments.Build(expected); AsaClientPlatformArguments.Build(desired);
        var server = servers[serverId];
        if (server.Edition != ArkEdition.ASA) throw new ArgumentException("These platform options apply to ASA.");
        if (server.AsaClients != expected) throw new InvalidOperationException("Player platforms changed. Reload before saving.");
        servers[serverId] = server with { AsaClients = desired };
    }

    public void AddMod(Guid serverId, ModMetadata mod)
    {
        var server = servers[serverId];
        if (mod.Edition != server.Edition) throw new ArgumentException("This mod targets a different ARK edition.");
        RequireModClientCompatibility(server, 1);
        if (string.IsNullOrWhiteSpace(mod.Id)) throw new ArgumentException("A mod ID is required.");
        if (server.Mods.Contains(mod.Id)) throw new ArgumentException("This mod is already added.");
        servers[serverId] = server with { Mods = [.. server.Mods, mod.Id] };
    }

    public void SetModOrder(Guid serverId, IReadOnlyList<string> orderedIds)
    {
        var server = servers[serverId];
        if (orderedIds.Count != server.Mods.Count || orderedIds.Distinct().Count() != orderedIds.Count
            || !orderedIds.ToHashSet().SetEquals(server.Mods))
            throw new ArgumentException("The load order must include every installed mod exactly once.");
        servers[serverId] = server with { Mods = [.. orderedIds] };
    }

    // Downloaded-file removal is performed by the management service before committing this change.
    public void RemoveMod(Guid serverId, string modId)
    {
        var server = servers[serverId];
        if (!server.Mods.Contains(modId)) throw new ArgumentException("This mod is not installed.");
        servers[serverId] = server with { Mods = server.Mods.Where(id => id != modId).ToList() };
    }

    public void ReplaceMods(Guid serverId, IReadOnlyList<string> orderedIds)
    {
        AsaModLaunchArguments.Build(orderedIds);
        RequireModClientCompatibility(servers[serverId], orderedIds.Count);
        servers[serverId] = servers[serverId] with { Mods = orderedIds.ToList() };
    }

    public static void RequireModClientCompatibility(ServerDefinition server, int count)
    {
        if (server.Edition == ArkEdition.ASE && server.AseClients != AseClientAccess.Steam && count > 0)
            throw new InvalidOperationException("ASE Workshop mods require Steam clients. Change player client access before adding mods.");
    }

    private void EnsurePortsAvailable(ServerDefinition candidate, Guid? excluding = null)
    {
        var ports = Ports(candidate.Settings).ToHashSet();
        if (servers.Values.Where(s => s.Id != excluding).Any(s => Ports(s.Settings).Any(ports.Contains)))
            throw new ArgumentException("A port is already assigned to another server.");
    }

    private static IEnumerable<int> Ports(ServerSettings s) => [s.GamePort, s.GamePort + 1, s.QueryPort, s.RconPort];
    private static ServerDefinition Clone(ServerDefinition s) => s with { Settings = s.Settings.Copy(), Mods = [.. s.Mods], ClusterOverrides = [.. s.ClusterOverrides] };
    private static ClusterDefinition CloneCluster(ClusterDefinition cluster) => cluster with
    {
        Settings = cluster.Settings.Copy(), Defaults = cluster.Defaults is null ? null : cluster.Defaults with
        { Settings = cluster.Defaults.Settings.Copy(), Mods = [.. cluster.Defaults.Mods] }
    };
}
