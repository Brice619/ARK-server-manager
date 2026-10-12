namespace ArkServerManager.Core;

public sealed record ServerCreationCheckRequest(Guid ServerId, ArkEdition Edition, string Map, string Name,
    int MaxPlayers, bool Internet, string? NewClusterName);
public sealed record ServerCreationCheck(Guid ServerId, string Directory, ServerPlatform Platform,
    int GamePort, int QueryPort, int RconPort, IReadOnlyList<string> Findings);

/// <summary>Checks a new guided setup without downloading files, registering a server or retaining port assignments.</summary>
public static class ServerCreationPreflight
{
    public static async Task<ServerCreationCheck> CheckAsync(CatalogRepository repository, string managementRoot,
        ServerCreationCheckRequest request, CancellationToken ct = default)
    {
        if (request.ServerId == Guid.Empty || !Enum.IsDefined(request.Edition)
            || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 256 || request.Name.Any(char.IsControl)
            || request.MaxPlayers < 1 || string.IsNullOrWhiteSpace(request.Map) || request.Map.Length > 256
            || request.Map.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
            throw new ArgumentException("Review the server name, world and player capacity.");
        if (request.NewClusterName is not null && (string.IsNullOrWhiteSpace(request.NewClusterName)
            || request.NewClusterName.Length > 256 || request.NewClusterName.Any(char.IsControl)))
            throw new ArgumentException("Enter a valid cluster name.");
        if (!Path.IsPathFullyQualified(managementRoot) || !Directory.Exists(managementRoot))
            throw new ArgumentException("The installed management folder is unavailable.");
        var platform = OperatingSystem.IsWindows() ? ServerPlatform.Windows : OperatingSystem.IsLinux()
            ? ServerPlatform.Linux : throw new PlatformNotSupportedException("Server creation supports Windows and Linux.");
        var target = Path.Combine(Path.GetFullPath(managementRoot), "servers", request.ServerId.ToString("N"));
        for (var path = target; path is not null; path = Path.GetDirectoryName(path))
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The server location uses a linked path and needs review.");
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("This setup already has files. Review it before creating again.");
        var catalog = await repository.ReadAsync(ct);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (catalog.Servers.Any(s => s.Id == request.ServerId || Overlap(Path.GetFullPath(s.Directory), target, comparison)))
            throw new InvalidOperationException("This setup overlaps an existing server.");
        ct.ThrowIfCancellationRequested();
        using var ports = new PortAllocator().Reserve(catalog.Servers.Select(s => s.Settings));
        var findings = new List<string>
        {
            "Available local ports checked. They will be checked again and assigned during creation.",
            "Server files and runtime prerequisites still need installation checks before creation."
        };
        if (request.Internet) findings.Add("Internet access still requires firewall and router setup; this check does not publish the server.");
        if (platform == ServerPlatform.Linux && request.Edition == ArkEdition.ASA)
            findings.Add("ASA requires a Windows compatibility environment on Linux. Required tools and the server environment are checked before launch; native Linux operation has not been verified.");
        return new(request.ServerId, target, platform, ports.GamePort, ports.QueryPort, ports.RconPort, findings);
    }

    private static bool Overlap(string a, string b, StringComparison comparison)
    {
        a = Path.TrimEndingDirectorySeparator(a); b = Path.TrimEndingDirectorySeparator(b);
        return a.Equals(b, comparison) || a.StartsWith(b + Path.DirectorySeparatorChar, comparison)
            || b.StartsWith(a + Path.DirectorySeparatorChar, comparison);
    }
}
