namespace ArkServerManager.Core;

/// <summary>Plans UMU execution of the Windows ASA payload. Installation, ownership and readiness are separate checks.</summary>
public static class LinuxAsaLaunchPlan
{
    public static ProcessLaunch BuildManaged(CatalogDocument catalog, Guid serverId, string managementRoot,
        string pythonExecutable, string umuLauncher, bool disableBattlEye, string consoleLog)
    {
        var server = catalog.Servers.SingleOrDefault(s => s.Id == serverId)
            ?? throw new ArgumentException("Choose a registered ASA server.");
        if (server.Edition != ArkEdition.ASA) throw new ArgumentException("ASE uses its native Linux server.");
        foreach (var executable in new[] { pythonExecutable, umuLauncher })
        {
            if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
                throw new FileNotFoundException("The verified compatibility launcher or Python executable is missing.");
            RejectLinks(executable);
        }
        var native = NativeServerLaunchPlan.BuildManaged(catalog, serverId, managementRoot,
            ServerPlatform.Windows, disableBattlEye, consoleLog);
        var arguments = native.Arguments.Select(argument => argument.StartsWith("-ClusterDirOverride=", StringComparison.Ordinal)
            ? "-ClusterDirOverride=" + UnixPathForWine(argument["-ClusterDirOverride=".Length..]) : argument).ToArray();
        var environment = UmuServerEnvironment.Build(managementRoot, serverId);
        foreach (var key in new[] { "WINEPREFIX", "UMU_FOLDERS_PATH", "XDG_CACHE_HOME" }) RejectLinks(environment[key]!);
        return new(Path.GetFullPath(pythonExecutable), Path.GetFullPath(native.WorkingDirectory),
            [Path.GetFullPath(umuLauncher), Path.GetFullPath(native.Executable), .. arguments], native.LogFile, native.Secrets)
        { EnvironmentOverrides = environment };
    }

    internal static string UnixPathForWine(string path)
    {
        // Wine's standard Z: mapping exposes the Unix root; reject ambiguous Linux names.
        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal) || path.Contains('\\') || path.Contains(':')
            || path.Contains('\0') || path.Split('/').Any(part => part is "." or ".."))
            throw new ArgumentException("Choose an unambiguous absolute Linux cluster path.");
        return "Z:" + path.Replace('/', '\\');
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked compatibility launch locations require review.");
    }
}
