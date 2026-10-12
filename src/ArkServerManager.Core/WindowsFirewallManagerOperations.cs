using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ArkServerManager.Core;
public sealed record FirewallSelection(Guid ServerId, string Directory, ArkEdition Edition, int GamePort, int QueryPort,
    ServerNetworkScope Scope, bool Confirmed);
public sealed record FirewallSetupResult(Guid ServerId, ServerNetworkScope Scope, string RuleName);
public sealed record FirewallSetupRecord(int SchemaVersion, Guid ServerId, string Directory, string RuleName, bool Completed, bool Removed)
{
    public Guid? OperationId { get; init; }
    public ServerNetworkScope? Scope { get; init; }
}
public sealed record FirewallRecordedStatus(Guid ServerId, FirewallSetupRecord? Record);
public sealed class WindowsFirewallManagerOperations(CatalogRepository repository, BackgroundOperations operations,
    ServerLifecycleCoordinator lifecycle, Func<string, CancellationToken, Task<int>>? apply = null, string? managementRoot = null)
{
    private AtomicJsonStore<FirewallSetupRecord> Store(Guid id) => new(Path.Combine(managementRoot!, "app-settings", "firewall-" + id.ToString("N") + ".json"));
    public async Task<FirewallRecordedStatus> ReadRecordedAsync(Guid id, CancellationToken ct = default)
    {
        if (managementRoot is null || id == Guid.Empty) throw new ArgumentException("Choose an installed server.");
        var server = (await repository.ReadAsync(ct)).Servers.Single(s => s.Id == id);
        var record = await Store(id).LoadAsync(ct);
        if (record is not null && (record.SchemaVersion != 1 || record.ServerId != id || record.Directory != server.Directory
            || record.RuleName != WindowsServerFirewallPlan.Build(server, ServerNetworkScope.LocalNetwork).Name
            || record.OperationId == Guid.Empty || record.Scope is ServerNetworkScope scope && !Enum.IsDefined(scope)))
            throw new InvalidDataException("The recorded firewall ownership needs review.");
        return new(id, record);
    }
    public BackgroundOperation Start(ManagerRequest request)
    {
        var selection = request.Payload?.Deserialize<FirewallSelection>() ?? throw new ArgumentException("Review firewall setup.");
        if (!selection.Confirmed || selection.ServerId == Guid.Empty || !Enum.IsDefined(selection.Scope))
            throw new ArgumentException("Confirm the firewall scope.");
        if (!OperatingSystem.IsWindows() && apply is null) throw new PlatformNotSupportedException("Automatic firewall setup requires Windows.");
        return operations.Submit(request.Id, "server-firewall-setup", request.Payload, async (progress, ct) =>
        {
            FirewallSetupResult? result = null;
            await lifecycle.WithStatusScopeAsync(async (_, token) =>
            {
                var server = (await repository.ReadAsync(token)).Servers.Single(server => server.Id == selection.ServerId);
                if (server.Directory != selection.Directory || server.Edition != selection.Edition
                    || server.Settings.GamePort != selection.GamePort || server.Settings.QueryPort != selection.QueryPort)
                    throw new InvalidOperationException("Server network settings changed. Review firewall setup again.");
                var rule = WindowsServerFirewallPlan.Build(server, selection.Scope);
                if (!File.Exists(rule.Program)) throw new IOException("The server executable is missing.");
                for (var path = new FileInfo(rule.Program) as FileSystemInfo; path is not null;
                    path = path is FileInfo file ? file.Directory : ((DirectoryInfo)path).Parent)
                    if ((path.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked server paths require separate firewall review.");
                progress(new("Waiting for Windows administrator approval and firewall setup."));
                var record = new FirewallSetupRecord(1, server.Id, server.Directory, rule.Name, false, false)
                    { OperationId = request.Id, Scope = selection.Scope };
                if (managementRoot is not null) await Store(server.Id).SaveAsync(record, token);
                var exit = await (apply ?? ApplyElevatedAsync)(WindowsServerFirewallPlan.CreateScript(server, selection.Scope), token);
                if (exit != 0) throw new IOException("Windows firewall setup did not complete. An existing rule may need review in Windows Firewall.");
                if (managementRoot is not null) await Store(server.Id).SaveAsync(record with { Completed = true }, CancellationToken.None);
                result = new(server.Id, selection.Scope, rule.Name);
            }, ct);
            return result!;
        });
    }

    /// <summary>Caller holds the stopped-map lifecycle scope during deletion.</summary>
    public async Task RemoveForDeletionAsync(ServerDefinition server, CancellationToken ct)
    {
        if (managementRoot is null || !OperatingSystem.IsWindows() && apply is null) return;
        var store = Store(server.Id);
        var record = await store.LoadAsync(ct);
        if (record is null) return;
        if (record.SchemaVersion != 1 || record.ServerId != server.Id || record.Directory != server.Directory
            || record.RuleName != WindowsServerFirewallPlan.Build(server, ServerNetworkScope.LocalNetwork).Name)
            throw new InvalidDataException("Firewall setup record changed. Review it before deletion.");
        if (record.Removed) return;
        var exit = await (apply ?? ApplyElevatedAsync)(WindowsServerFirewallPlan.RemovalScript(server), ct);
        if (exit != 0) throw new IOException("Windows firewall cleanup did not complete. The server files were retained.");
        await store.SaveAsync(record with { Removed = true }, CancellationToken.None);
    }

    private static async Task<int> ApplyElevatedAsync(string script, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("try {\n" + script + "\nexit 0\n} catch { exit 1 }"));
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = "-NoLogo -NoProfile -NonInteractive -EncodedCommand " + encoded
        }) ?? throw new IOException("Windows administrator setup could not start.");
        // Once approved, keep observing the same process. Do not kill privileged work or resend it.
        await process.WaitForExitAsync(CancellationToken.None);
        return process.ExitCode;
    }
}
