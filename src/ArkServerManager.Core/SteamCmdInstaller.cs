using System.Globalization;

namespace ArkServerManager.Core;

public enum ServerPlatform { Windows, Linux }
public sealed record SteamInstallRequest(ArkEdition Edition, ServerPlatform Platform, string Directory, bool ValidateFiles);
public sealed record SteamInstallResult(uint AppId, string Directory, int ExitCode, bool Success, string Message);

public sealed class SteamCmdInstaller
{
    private readonly string executable;
    private readonly string logsDirectory;
    private readonly SemaphoreSlim gate;
    public SteamCmdInstaller(string executable, string logsDirectory)
    { this.executable = Path.GetFullPath(executable); this.logsDirectory = Path.GetFullPath(logsDirectory); gate = SteamCmdProcessGate.For(this.executable); }

    public static uint AppId(ArkEdition edition) => edition switch
    {
        ArkEdition.ASE => 376030,
        ArkEdition.ASA => 2430930,
        _ => throw new ArgumentOutOfRangeException(nameof(edition))
    };

    public static ServerPlatform PayloadPlatform(ArkEdition edition, ServerPlatform hostPlatform)
    {
        if (!Enum.IsDefined(edition) || !Enum.IsDefined(hostPlatform)) throw new ArgumentException("Choose a supported game and host platform.");
        return edition == ArkEdition.ASA ? ServerPlatform.Windows : hostPlatform;
    }

    public static IReadOnlyList<string> Arguments(SteamInstallRequest request)
    {
        if (!Enum.IsDefined(request.Platform)) throw new ArgumentOutOfRangeException(nameof(request.Platform));
        if (string.IsNullOrWhiteSpace(request.Directory) || request.Directory.IndexOfAny(['\r', '\n', '"']) >= 0)
            throw new ArgumentException("Choose a valid server folder.");
        var directory = Path.GetFullPath(request.Directory);
        if (directory.TrimEnd(Path.DirectorySeparatorChar) == Path.GetPathRoot(directory)!.TrimEnd(Path.DirectorySeparatorChar))
            throw new ArgumentException("Choose a dedicated folder rather than a drive root.");
        var args = new List<string>
        {
            "+@ShutdownOnFailedCommand", "1", "+@NoPromptForPassword", "1",
            "+@sSteamCmdForcePlatformType", PayloadPlatform(request.Edition, request.Platform) == ServerPlatform.Windows ? "windows" : "linux",
            "+force_install_dir", directory, "+login", "anonymous", "+app_update", AppId(request.Edition).ToString(CultureInfo.InvariantCulture)
        };
        if (request.ValidateFiles) args.Add("validate");
        args.Add("+quit");
        return args;
    }

    /// <summary>The caller must obtain installation consent and ensure the target server is stopped.</summary>
    public async Task<SteamInstallResult> InstallOrUpdateAsync(SteamInstallRequest request,
        IProgress<IReadOnlyList<ProcessLine>>? progress = null, CancellationToken cancellationToken = default)
    {
        var arguments = Arguments(request);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var scanStarted = DateTimeOffset.UtcNow;
            var installerProcesses = await new SteamCmdProcessDiscovery().ScanAsync(cancellationToken);
            SteamCmdProcessSafety.RequireIdle(executable, installerProcesses, scanStarted, DateTimeOffset.UtcNow);
            Directory.CreateDirectory(request.Directory);
            Directory.CreateDirectory(logsDirectory);
            var id = AppId(request.Edition);
            var log = Path.Combine(logsDirectory, $"steam-{id}-{Guid.NewGuid():N}.log");
            var nativeConsole = OperatingSystem.IsWindows() ? new SteamNativeConsoleReader(executable) : null;
            await using var process = ManagedProcess.Start(new(executable, Path.GetDirectoryName(executable)!, arguments, log));
            while (!process.Completion.IsCompleted)
            {
                if (nativeConsole is not null) await nativeConsole.ReadAsync(cancellationToken);
                progress?.Report(nativeConsole?.Tail.Count > 0 ? nativeConsole.Tail : process.ConsoleTail);
                await Task.WhenAny(process.Completion, Task.Delay(250, cancellationToken));
                cancellationToken.ThrowIfCancellationRequested();
            }
            var exit = await process.Completion;
            if (nativeConsole is not null) await nativeConsole.ReadAsync(cancellationToken);
            progress?.Report(nativeConsole?.Tail.Count > 0 ? nativeConsole.Tail : process.ConsoleTail);
            if (exit.ExitCode != 0) return new(id, request.Directory, exit.ExitCode, false, "SteamCMD failed. See the installation log.");
            var confirmed = nativeConsole?.Confirmed(id) ?? false;
            using (var reader = File.OpenText(log))
                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                    if (line.Contains($"Success! App '{id}' fully installed.", StringComparison.Ordinal)
                        || line.Contains($"Success! App '{id}' already up to date.", StringComparison.Ordinal)) confirmed = true;
            if (!confirmed) return new(id, request.Directory, exit.ExitCode, false, "SteamCMD did not confirm installation completion.");
            var evaluated = new InstallationDiscovery().Evaluate(request.Directory);
            var platform = PayloadPlatform(request.Edition, request.Platform).ToString();
            var candidate = evaluated.Candidates.FirstOrDefault(c => c.Edition == request.Edition && c.Platform == platform);
            if (candidate is null || new FileInfo(candidate.Executable).Length == 0)
                return new(id, request.Directory, exit.ExitCode, false, "SteamCMD finished, but the expected server executable is missing or empty.");
            return new(id, request.Directory, exit.ExitCode, true, "Server files installed. Runtime compatibility still requires a startup check.");
        }
        finally { gate.Release(); }
    }
}
