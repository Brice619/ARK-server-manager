using System.Globalization;

namespace ArkServerManager.Core;

public sealed record NativeServerLaunchOptions(ServerPlatform Platform, bool DisableBattlEye, string? SharedClusterDirectory);

/// <summary>Prepares direct native launch tokens. Configuration synchronization and live edition validation are separate prerequisites.</summary>
public static class NativeServerLaunchPlan
{
    /// <summary>Explicit managed-storage route; imported clusters use their validated existing override through Build.</summary>
    public static ProcessLaunch BuildManaged(CatalogDocument catalog, Guid serverId, string managementRoot,
        ServerPlatform platform, bool disableBattlEye, string consoleLog)
    {
        var server = catalog.Servers.SingleOrDefault(s => s.Id == serverId)
            ?? throw new ArgumentException("Choose a registered server.");
        var shared = server.ClusterId is Guid clusterId
            ? ManagedClusterStorage.Resolve(catalog, clusterId, managementRoot).Transfers : null;
        return Build(server, new(platform, disableBattlEye, shared), consoleLog);
    }

    public static ProcessLaunch Build(ServerDefinition server, NativeServerLaunchOptions options, string consoleLog)
    {
        server.Settings.Validate();
        if (!Enum.IsDefined(server.Edition) || !Enum.IsDefined(options.Platform)) throw new ArgumentException("Choose a supported game and platform.");
        if (server.Edition == ArkEdition.ASA && options.Platform == ServerPlatform.Linux)
            throw new NotSupportedException("ASA requires its Windows server and a verified Linux compatibility environment.");
        if (!Path.IsPathFullyQualified(server.Directory) || !Path.IsPathFullyQualified(consoleLog))
            throw new ArgumentException("Choose absolute server and console locations.");
        var map = ServerMapIdentifier.ForLaunch(server.Edition, server.Map);
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(server.Directory));
        RejectLinks(consoleLog);
        var relativeExecutable = (server.Edition, options.Platform) switch
        {
            (ArkEdition.ASA, ServerPlatform.Windows) => "ShooterGame/Binaries/Win64/ArkAscendedServer.exe",
            (ArkEdition.ASE, ServerPlatform.Windows) => "ShooterGame/Binaries/Win64/ShooterGameServer.exe",
            _ => "ShooterGame/Binaries/Linux/ShooterGameServer"
        };
        var executable = Path.Combine(directory, relativeExecutable);
        if (!File.Exists(executable)) throw new FileNotFoundException("The selected dedicated-server executable is missing.");
        RejectLinks(executable);
        var number = (int value) => value.ToString(CultureInfo.InvariantCulture);
        var arguments = new List<string>();
        if (!Enum.IsDefined(server.AseClients)) throw new ArgumentException("Choose supported ASE client access.");
        if (server.Edition == ArkEdition.ASE && server.AseClients != AseClientAccess.Steam)
        {
            if (server.Mods.Count > 0) throw new InvalidOperationException("ASE Workshop mods require Steam clients. Change client access before starting a modded server.");
            arguments.Add(server.AseClients == AseClientAccess.Epic ? "-epiconly" : "-crossplay");
        }
        if (server.Edition == ArkEdition.ASA)
        {
            arguments.Add(AsaClientPlatformArguments.Build(server.AsaClients));
            arguments.Add(map + "?listen");
            arguments.Add("-port=" + number(server.Settings.GamePort));
            arguments.Add("-QueryPort=" + number(server.Settings.QueryPort));
            arguments.Add("-WinLiveMaxPlayers=" + number(server.Settings.MaxPlayers));
            arguments.AddRange(AsaModLaunchArguments.Build(server.Mods));
        }
        else arguments.Add(map + "?listen?Port=" + number(server.Settings.GamePort)
            + "?QueryPort=" + number(server.Settings.QueryPort) + "?MaxPlayers=" + number(server.Settings.MaxPlayers));
        if (server.Edition == ArkEdition.ASE && server.Mods.Count > 0)
        {
            AsaModLaunchArguments.Build(server.Mods);
            arguments.Add("-automanagedmods");
        }
        arguments.Add("-server"); arguments.Add("-log");
        if (options.DisableBattlEye) arguments.Add("-NoBattlEye");
        if (server.ClusterId is Guid clusterId)
        {
            if (clusterId == Guid.Empty || string.IsNullOrWhiteSpace(options.SharedClusterDirectory)
                || !Path.IsPathFullyQualified(options.SharedClusterDirectory))
                throw new ArgumentException("Choose an absolute shared cluster directory.");
            var shared = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.SharedClusterDirectory));
            if (Contains(directory, shared) || Contains(shared, directory))
                throw new ArgumentException("Shared cluster data must not overlap a member's installation folder.");
            if (File.Exists(shared)) throw new ArgumentException("Shared cluster data requires a folder, not a file.");
            RejectLinks(shared);
            arguments.Add("-clusterid=" + clusterId.ToString("N"));
            arguments.Add("-ClusterDirOverride=" + shared);
        }
        else if (options.SharedClusterDirectory is not null)
            throw new ArgumentException("A standalone server cannot use a shared cluster directory.");
        server.LaunchPreferences.Apply(arguments);
        return new(executable, Path.GetDirectoryName(executable)!, arguments, Path.GetFullPath(consoleLog));
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked launch locations require separate validation.");
    }

    private static bool Contains(string parent, string child)
    {
        var relative = Path.GetRelativePath(parent, child);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }
}
