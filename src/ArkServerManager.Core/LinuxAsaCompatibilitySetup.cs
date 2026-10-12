using System.Runtime.Versioning;

namespace ArkServerManager.Core;
public sealed record LinuxAsaCompatibilityTools(string Python, string Launcher);
public sealed record LinuxAsaPrefixRecord(int SchemaVersion, Guid ServerId, string Prefix, Guid OperationId,
    int? ProcessId, Guid BootId, long? StartedTicks, bool Completed);
[SupportedOSPlatform("linux")]
public sealed class LinuxAsaCompatibilitySetup(string managementRoot, HttpClient http)
{
    public async Task<LinuxAsaCompatibilityTools> PrepareAsync(ServerDefinition server, Action<OperationProgress> progress, CancellationToken ct)
    {
        if (server.Edition != ArkEdition.ASA || server.Id == Guid.Empty) throw new ArgumentException("Choose an ASA server.");
        var tools = await EnsureToolsAsync(progress, ct);
        var environment = UmuServerEnvironment.Build(managementRoot, server.Id);
        var prefix = environment["WINEPREFIX"]!;
        var store = new AtomicJsonStore<LinuxAsaPrefixRecord>(Path.Combine(managementRoot, "app-settings", "asa-prefix-" + server.Id.ToString("N") + ".json"));
        var previous = await store.LoadAsync(ct);
        if (previous is not null)
        {
            if (previous.SchemaVersion != 1 || previous.ServerId != server.Id || previous.Prefix != prefix || previous.OperationId == Guid.Empty
                || previous.ProcessId.HasValue != previous.StartedTicks.HasValue || previous.ProcessId is <= 0
                || previous.ProcessId.HasValue && previous.BootId == Guid.Empty) throw new InvalidDataException("Review the recorded ASA compatibility setup.");
            if (!previous.Completed && previous.ProcessId is int id
                && (await LinuxProcessIdentity.ReadAsync(id, ct))?.Matches(id, previous.BootId, previous.StartedTicks!.Value) == true)
                throw new IOException("ASA compatibility setup is still running. Check it again later.");
            if (previous.Completed && PrefixReady(prefix)) return tools;
        }
        for (var path = prefix; path is not null; path = Path.GetDirectoryName(path))
            if ((Directory.Exists(path) || File.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked ASA compatibility storage requires review.");
        var record = new LinuxAsaPrefixRecord(1, server.Id, prefix, Guid.NewGuid(), null, Guid.Empty, null, false);
        await store.SaveAsync(record, ct);
        progress(new("Preparing this server's Windows compatibility environment and downloading its runtime."));
        await using var process = ManagedProcess.Start(new("/bin/sh", managementRoot,
            ["-c", "read -r ready && [ \"$ready\" = go ] && exec \"$@\"", "ark-compatibility", tools.Python, tools.Launcher, "wineboot.exe", "-u"],
            Path.Combine(managementRoot, "Dependencies", "Compatibility", "Logs", server.Id.ToString("N") + ".log"))
            { EnvironmentOverrides = environment });
        var identity = await LinuxProcessIdentity.ReadAsync(process.Id, ct) ?? throw new IOException("The compatibility setup exited before preparation.");
        record = record with { ProcessId = process.Id, BootId = identity.BootId, StartedTicks = identity.StartedTicks };
        await store.SaveAsync(record, ct);
        await process.SendStandardInputAsync("go", ct);
        var exit = await process.Completion.WaitAsync(TimeSpan.FromMinutes(30), ct);
        if (exit.ExitCode != 0 || !PrefixReady(prefix)) throw new IOException("ASA compatibility preparation failed. Review its setup log before retrying.");
        await store.SaveAsync(record with { Completed = true }, ct);
        return tools;
    }
    private static bool PrefixReady(string prefix)
    {
        foreach (var name in new[] { "system.reg", "user.reg" })
            if (!File.Exists(Path.Combine(prefix, name)) || new FileInfo(Path.Combine(prefix, name)).Length == 0) return false;
        return Directory.ResolveLinkTarget(Path.Combine(prefix, "dosdevices", "z:"), returnFinalTarget: true)?.FullName == "/";
    }
    public async Task<LinuxAsaCompatibilityTools> EnsureToolsAsync(Action<OperationProgress> progress, CancellationToken ct)
    {
        var python = await new LinuxRequiredToolSetup(managementRoot, compatibility: true).EnsureAsync(progress, ct);
        if (!python.Ready || python.Executable is null) throw new IOException(python.Notice);
        progress(new("Preparing the managed ASA compatibility launcher."));
        var launcher = await new UmuBootstrap(http).EnsureAsync(Path.Combine(managementRoot, "Dependencies", "Compatibility", "UMU"), ct);
        return new(python.Executable, launcher);
    }
}
