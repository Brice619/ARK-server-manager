using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using ArkServerManager.Core;

internal static class NativeAseSmoke
{
    public static async Task<object> RunAsaRemovalAsync(string sourceEvidence, string evidence, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(sourceEvidence) || !Path.IsPathFullyQualified(evidence))
            throw new ArgumentException("Choose absolute Windows verification paths.");
        if (Directory.Exists(evidence) || File.Exists(evidence)) throw new IOException("Choose a new removal evidence folder.");
        var originalRepository = new CatalogRepository(Path.Combine(sourceEvidence, "catalog.json"));
        var original = (await originalRepository.ReadAsync(ct)).Servers.Single();
        if (original.Edition != ArkEdition.ASA || original.ClusterId is not null || original.Mods.Count != 1
            || original.Settings.Name != "ARK Server Manager Verification")
            throw new InvalidDataException("Use the dedicated one-mod ASA verification installation.");
        if ((await new InstallationStopGuard().CheckAsync(original.Directory, ct)).State != InstallationStopState.Stopped)
            throw new IOException("The verification server is not stopped.");
        var source = await ServerConfigurationReader.ReadAsync(originalRepository, new(original.Id, "GameUserSettings.ini"), ct);
        var ini = new IniDocument(source.Text!);
        var admin = ini.GetValues("ServerSettings", "ServerAdminPassword").Single();
        var join = ini.GetValues("ServerSettings", "ServerPassword").Single();
        var native = Path.Combine(original.Directory, "ShooterGame", "Binaries", "Win64", "ShooterGame");
        var library = Path.Combine(native, "ModsUserData", "83374", "library.json");
        var shared = Path.Combine(native, "ModsUserData", "83374", "shared_info.json");
        var user = Path.Combine(native, "ModsUserData", "83374", "local", "user_info.json");
        var beforeShared = await File.ReadAllBytesAsync(shared, ct); var beforeUser = await File.ReadAllBytesAsync(user, ct);
        var beforeConfiguration = await File.ReadAllBytesAsync(Path.Combine(original.Directory, "ShooterGame", "Saved", "Config", "WindowsServer", "GameUserSettings.ini"), ct);
        Directory.CreateDirectory(evidence);
        // Preserve the native library in private verification storage for investigating
        // a failed cleanup; do not return its contents in diagnostic output.
        File.Copy(library, Path.Combine(evidence, "library-before-removal.json"), overwrite: false);
        await RemovedAsaModFiles.DeleteAsync(original, original.Mods, ct);
        var afterShared = await File.ReadAllBytesAsync(shared, ct); var afterUser = await File.ReadAllBytesAsync(user, ct);
        var afterConfiguration = await File.ReadAllBytesAsync(Path.Combine(original.Directory, "ShooterGame", "Saved", "Config", "WindowsServer", "GameUserSettings.ini"), ct);
        var sharedPreserved = beforeShared.SequenceEqual(afterShared);
        var userPreserved = beforeUser.SequenceEqual(afterUser);
        var configurationPreserved = beforeConfiguration.SequenceEqual(afterConfiguration);
        var definition = original with { Mods = [] };
        var repository = new CatalogRepository(Path.Combine(evidence, "catalog.json"));
        await repository.ChangeAsync(c => c.AddServer(definition), ct);
        var runtime = await RunRegisteredAsync(definition, repository, evidence, admin, join, ct);
        var mods = Path.Combine(native, "Mods", "83374");
        var remaining = Directory.Exists(mods) ? Directory.GetDirectories(mods).Where(path => Path.GetFileName(path).StartsWith(original.Mods.Single() + "_", StringComparison.Ordinal)).ToArray() : [];
        using var libraryDocument = JsonDocument.Parse(await File.ReadAllTextAsync(library, ct));
        var libraryRemoved = !libraryDocument.RootElement.GetProperty("installedMods").EnumerateArray()
            .Any(entry => entry.GetProperty("installedFile").GetProperty("modId").GetUInt64() == ulong.Parse(original.Mods.Single()));
        var result = new { ModId = original.Mods.Single(), Runtime = runtime, SharedMetadataPreservedDuringCleanup = sharedPreserved,
            UserMetadataPreservedDuringCleanup = userPreserved, ConfigurationPreservedDuringCleanup = configurationPreserved,
            NativeModFoldersAbsentAfterRestart = remaining.Length == 0, LibraryEntryAbsentAfterRestart = libraryRemoved,
            GameplayConnectionVerified = false };
        await File.WriteAllTextAsync(Path.Combine(evidence, "mod-removal-result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), ct);
        return result;
    }

    public static async Task<object> RunAsaModAsync(string sourceEvidence, string evidence, string modId, CancellationToken ct,
        bool overrideModsDirectory = false)
    {
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(sourceEvidence) || !Path.IsPathFullyQualified(evidence))
            throw new ArgumentException("Choose absolute Windows verification paths.");
        AsaModLaunchArguments.Build([modId]);
        if (Directory.Exists(evidence) || File.Exists(evidence)) throw new IOException("Choose a new mod evidence folder.");
        var original = (await new CatalogRepository(Path.Combine(sourceEvidence, "catalog.json")).ReadAsync(ct)).Servers.Single();
        if (original.Edition != ArkEdition.ASA || original.ClusterId is not null || original.Mods.Count != 0
            || original.Settings.Name != "ARK Server Manager Verification")
            throw new InvalidDataException("Use the dedicated standalone vanilla ASA verification installation.");
        if ((await new InstallationStopGuard().CheckAsync(original.Directory, ct)).State != InstallationStopState.Stopped)
            throw new IOException("The verification server is not stopped.");
        var source = await ServerConfigurationReader.ReadAsync(new CatalogRepository(Path.Combine(sourceEvidence, "catalog.json")),
            new(original.Id, "GameUserSettings.ini"), ct);
        var ini = new IniDocument(source.Text ?? throw new InvalidDataException("The existing verification configuration is missing."));
        var admin = ini.GetValues("ServerSettings", "ServerAdminPassword").Single();
        var join = ini.GetValues("ServerSettings", "ServerPassword").Single();
        var definition = original with { Mods = [modId] };
        Directory.CreateDirectory(evidence);
        var repository = new CatalogRepository(Path.Combine(evidence, "catalog.json"));
        await repository.ChangeAsync(c => c.AddServer(definition), ct);
        var gamePath = Path.Combine(original.Directory, "ShooterGame", "Saved", "Config", "WindowsServer", "Game.ini");
        byte[]? originalGame = null;
        if (overrideModsDirectory)
        {
            originalGame = File.Exists(gamePath) ? await File.ReadAllBytesAsync(gamePath, ct) : null;
            if (originalGame is not null) await File.WriteAllBytesAsync(Path.Combine(evidence, "Game-before-override.ini"), originalGame, ct);
            var nativeMods = Path.Combine(original.Directory, "ShooterGame", "Binaries", "Win64", "ShooterGame", "Mods");
            Directory.CreateDirectory(nativeMods);
            var alternateMods = WindowsNativePath.ShortenExistingDirectory(nativeMods).Replace('\\', '/');
            var gameConfiguration = await ServerConfigurationReader.ReadAsync(repository, new(definition.Id, "Game.ini"), ct);
            var gameIni = new IniDocument(gameConfiguration.Text ?? "");
            gameIni.SetValues("/Script/cfcore.CFCoreEditorSettings", "modsDirectory", [alternateMods]);
            await File.WriteAllTextAsync(gamePath, gameIni.ToString(), ct);
            await File.WriteAllTextAsync(Path.Combine(evidence, "override-plan.json"), JsonSerializer.Serialize(new
                { Setting = "Game.ini::/Script/cfcore.CFCoreEditorSettings::modsDirectory", Directory = alternateMods }), ct);
        }
        var enumeration = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var before = Directory.EnumerateFiles(original.Directory, "*", enumeration)
            .Select(path => Path.GetRelativePath(original.Directory, path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        await File.WriteAllTextAsync(Path.Combine(evidence, "files-before.json"), JsonSerializer.Serialize(before), ct);
        var recognizedRunning = false;
        object runtime;
        try { runtime = await RunRegisteredAsync(definition, repository, evidence, admin, join, ct,
            afterReady: async (server, token) =>
            {
                var observed = await new InstallationStopGuard().CheckAsync(server.Directory, token);
                recognizedRunning = observed.State == InstallationStopState.Running;
                if (!recognizedRunning) throw new IOException("Native process discovery did not recognize the original verification installation as running.");
            }); }
        finally
        {
            if (overrideModsDirectory)
            {
                if ((await new InstallationStopGuard().CheckAsync(original.Directory, CancellationToken.None)).State != InstallationStopState.Stopped)
                    throw new IOException("The verification server could not be confirmed stopped. Its diagnostic override was retained for review.");
                if (originalGame is null) File.Delete(gamePath);
                else await File.WriteAllBytesAsync(gamePath, originalGame, CancellationToken.None);
            }
        }
        var after = Directory.EnumerateFiles(original.Directory, "*", enumeration)
            .Select(path => Path.GetRelativePath(original.Directory, path)).ToArray();
        var added = after.Where(path => !before.Contains(path)).ToArray();
        var result = new { Edition = "ASA", ModId = modId, Runtime = runtime, NewFiles = added,
            NativeProcessDiscoveryRecognizedOriginalDirectory = recognizedRunning,
            CandidateModFiles = after.Where(path => path.Replace('\\', '/').Contains("/Mods/", StringComparison.OrdinalIgnoreCase)).ToArray(),
            ModGameplayEffectVerified = false };
        await File.WriteAllTextAsync(Path.Combine(evidence, "mod-install-result.json"),
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), ct);
        return result;
    }

    public static async Task<object> RunAseModAsync(string sourceEvidence, string evidence, string modId, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(sourceEvidence) || !Path.IsPathFullyQualified(evidence))
            throw new ArgumentException("Choose absolute Windows verification paths.");
        AsaModLaunchArguments.Build([modId]);
        if (Directory.Exists(evidence) || File.Exists(evidence)) throw new IOException("Choose a new mod evidence folder.");
        var original = (await new CatalogRepository(Path.Combine(sourceEvidence, "catalog.json")).ReadAsync(ct)).Servers.Single();
        if (original.Edition != ArkEdition.ASE || original.ClusterId is not null || original.Mods.Count != 0
            || original.Settings.Name != "ARK Server Manager Verification")
            throw new InvalidDataException("Use the dedicated standalone vanilla ASE verification installation.");
        if ((await new InstallationStopGuard().CheckAsync(original.Directory, ct)).State != InstallationStopState.Stopped)
            throw new IOException("The verification installation is not stopped.");
        var config = Path.Combine(original.Directory, "ShooterGame", "Saved", "Config", "WindowsServer");
        var userPath = Path.Combine(config, "GameUserSettings.ini"); var gamePath = Path.Combine(config, "Game.ini");
        var userBytes = await File.ReadAllBytesAsync(userPath, ct);
        var gameBytes = File.Exists(gamePath) ? await File.ReadAllBytesAsync(gamePath, ct) : null;
        var user = new IniDocument(await File.ReadAllTextAsync(userPath, ct));
        var admin = user.GetValues("ServerSettings", "ServerAdminPassword").Single();
        var join = user.GetValues("ServerSettings", "ServerPassword").Single();
        var definition = original with { Mods = [modId] };
        Directory.CreateDirectory(evidence);
        var repository = new CatalogRepository(Path.Combine(evidence, "catalog.json"));
        await repository.ChangeAsync(c => c.AddServer(definition), ct);
        var generated = GuidedServerConfiguration.Build(new() { Server = definition, PvE = true,
            AdministratorPassword = admin, JoinPassword = join, UserSettingsTemplate = user.ToString(),
            GameSettingsTemplate = gameBytes is null ? null : await File.ReadAllTextAsync(gamePath, ct) });
        try
        {
            await File.WriteAllTextAsync(userPath, generated.UserSettings, ct);
            await File.WriteAllTextAsync(gamePath, generated.GameSettings, ct);
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ARKServerManager-Verification/1.0");
            Console.WriteLine("Preparing the production ASE automatic Workshop installer.");
            await new AseAutomaticMods(http).PrepareAsync(definition, ServerPlatform.Windows, ct);
            var runtime = await RunRegisteredAsync(definition, repository, evidence, admin, join, ct);
            var mods = Path.Combine(original.Directory, "ShooterGame", "Content", "Mods");
            var folder = Path.Combine(mods, modId);
            var result = new { Edition = "ASE", ModId = modId, Runtime = runtime,
                InstalledDescriptorExists = File.Exists(Path.Combine(mods, modId + ".mod")),
                InstalledModFiles = Directory.Exists(folder) ? Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Length : 0,
                ModGameplayEffectVerified = false };
            await File.WriteAllTextAsync(Path.Combine(evidence, "mod-install-result.json"),
                JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), ct);
            return result;
        }
        finally
        {
            if ((await new InstallationStopGuard().CheckAsync(original.Directory, CancellationToken.None)).State != InstallationStopState.Stopped)
                throw new IOException("Native stop is unverified; diagnostic mod configuration is retained for review.");
            await File.WriteAllBytesAsync(userPath, userBytes, CancellationToken.None);
            if (gameBytes is null) File.Delete(gamePath); else await File.WriteAllBytesAsync(gamePath, gameBytes, CancellationToken.None);
        }
    }

    public static async Task<object> VerifyConfigurationAsync(string evidence, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(evidence)) throw new ArgumentException("Use an absolute verification path.");
        var repository = new CatalogRepository(Path.Combine(evidence, "catalog.json"));
        var server = (await repository.ReadAsync(ct)).Servers.Single();
        var stopped = await new InstallationStopGuard().CheckAsync(server.Directory, ct);
        if (stopped.State != InstallationStopState.Stopped) throw new IOException("The verification server is not stopped.");
        var source = await ServerConfigurationReader.ReadAsync(repository, new(server.Id, "GameUserSettings.ini"), ct);
        GuidedServerConfiguration.ValidateIdentity(server, source.Text ?? throw new InvalidDataException("The generated configuration is missing."));
        var ini = new IniDocument(source.Text!);
        var rcon = ini.GetValues("ServerSettings", "RCONEnabled").SequenceEqual(new[] { "True" }, StringComparer.OrdinalIgnoreCase)
            && ini.GetValues("ServerSettings", "RCONPort").SequenceEqual(new[] { server.Settings.RconPort.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        var pve = ini.GetValues("ServerSettings", "ServerPVE").SequenceEqual(new[] { "True" }, StringComparer.OrdinalIgnoreCase);
        var result = new { Edition = server.Edition.ToString(), NativeStopped = true,
            IdentityPortsAndCapacityPreserved = true, RconConfigurationPreserved = rcon, PvEPreserved = pve,
            EngineSettingsVersion = ini.GetValues("/Script/ShooterGame.ShooterGameUserSettings", "Version") };
        await File.WriteAllTextAsync(Path.Combine(evidence, "configuration-check-result.json"),
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), ct);
        return result;
    }

    public static async Task<object> RunAsync(string directory, string evidence, CancellationToken ct, bool resume = false, bool generatedConfiguration = false,
        Func<ServerDefinition, CancellationToken, Task>? afterReady = null, ArkEdition edition = ArkEdition.ASE)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This diagnostic checks native Windows ARK.");
        if (!Enum.IsDefined(edition)) throw new ArgumentException("Choose a supported ARK edition.");
        if (!Path.IsPathFullyQualified(directory) || !Path.IsPathFullyQualified(evidence)) throw new ArgumentException("Use absolute verification paths.");
        if (resume)
        {
            var existing = new CatalogRepository(Path.Combine(evidence, "catalog.json"));
            var current = await existing.ReadAsync(ct);
            var server = current.Servers.Single();
            if (server.Directory != directory || server.Edition != edition || server.ClusterId is not null || server.Mods.Count != 0)
                throw new InvalidDataException("The existing verification catalog does not match this server.");
            if ((await new InstallationStopGuard().CheckAsync(directory, ct)).State != InstallationStopState.Stopped)
                throw new IOException("The verification server is not stopped.");
            var config = await ServerConfigurationReader.ReadAsync(existing, new(server.Id, "GameUserSettings.ini"), ct);
            var ini = new IniDocument(config.Text!);
            var adminValues = ini.GetValues("ServerSettings", "ServerAdminPassword");
            var joinValues = ini.GetValues("ServerSettings", "ServerPassword");
            var adminPassword = adminValues.Count == 1 && !string.IsNullOrWhiteSpace(adminValues[0]) ? adminValues[0] : Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            var joinPassword = joinValues.Count == 1 && !string.IsNullOrWhiteSpace(joinValues[0]) ? joinValues[0] : Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            ini.SetValues("ServerSettings", "ServerAdminPassword", [adminPassword]);
            ini.SetValues("ServerSettings", "ServerPassword", [joinPassword]);
            ini.SetValues("ServerSettings", "RCONEnabled", ["True"]);
            ini.SetValues("ServerSettings", "RCONPort", [server.Settings.RconPort.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            ini.SetValues("SessionSettings", "SessionName", [server.Settings.Name]);
            if (generatedConfiguration)
            {
                var original = Path.Combine(directory, "ShooterGame", "Saved", "Config", "WindowsServer", "GameUserSettings.ini");
                File.Copy(original, Path.Combine(evidence, "before-generated-configuration.ini"), overwrite: false);
                var generated = GuidedServerConfiguration.Build(new() { Server = server, PvE = true,
                    AdministratorPassword = adminPassword, JoinPassword = joinPassword });
                ini = new IniDocument(generated.UserSettings);
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "ShooterGame", "Saved", "Config", "WindowsServer", "GameUserSettings.ini"), ini.ToString(), ct);
            return await RunRegisteredAsync(server, existing, evidence, adminPassword, joinPassword, ct,
                generatedConfiguration ? "generated-config-result.json" : "result.json", afterReady);
        }
        if (Directory.Exists(evidence) || File.Exists(evidence)) throw new IOException("Choose a new evidence folder.");
        var saved = Path.Combine(directory, "ShooterGame", "Saved");
        if (Directory.Exists(saved)) throw new IOException("This diagnostic requires a fresh installation without existing saved state.");
        var check = await new InstallationStopGuard().CheckAsync(directory, ct);
        if (check.State != InstallationStopState.Stopped) throw new IOException("The verification installation could not be verified stopped.");
        var game = FindAvailablePortSet(); var query = game + 2; var rcon = game + 3;
        Directory.CreateDirectory(evidence);
        var configuration = Path.Combine(saved, "Config", "WindowsServer"); Directory.CreateDirectory(configuration);
        var admin = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var join = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var definition = new ServerDefinition(Guid.NewGuid(), edition, edition == ArkEdition.ASE ? "TheIsland" : "TheIsland_WP", directory,
            new() { Name = "ARK Server Manager Verification", MaxPlayers = 2, GamePort = game, QueryPort = query, RconPort = rcon });
        var freshGenerated = GuidedServerConfiguration.Build(new() { Server = definition, PvE = true,
            AdministratorPassword = admin, JoinPassword = join });
        var text = freshGenerated.UserSettings;
        await using (var file = new FileStream(Path.Combine(configuration, "GameUserSettings.ini"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        await using (var writer = new StreamWriter(file)) await writer.WriteAsync(text.AsMemory(), ct);
        var repository = new CatalogRepository(Path.Combine(evidence, "catalog.json"));
        await repository.ChangeAsync(catalog => catalog.AddServer(definition), ct);
        return await RunRegisteredAsync(definition, repository, evidence, admin, join, ct);
    }

    private static async Task<object> RunRegisteredAsync(ServerDefinition definition, CatalogRepository repository,
        string evidence, string admin, string join, CancellationToken ct, string resultName = "result.json",
        Func<ServerDefinition, CancellationToken, Task>? afterReady = null)
    {
        var directory = definition.Directory;
        var saved = Path.Combine(directory, "ShooterGame", "Saved");
        var journal = new ActivityJournal(Path.Combine(evidence, "activity.jsonl"));
        var factory = new RegisteredRconRuntimeFactory(repository,
            server =>
            {
                var launch = NativeServerLaunchPlan.Build(server, new(ServerPlatform.Windows, true, null), Path.Combine(evidence, "console.log"));
                return launch with { Arguments = [.. launch.Arguments, "-MULTIHOME=127.0.0.1"], Secrets = [admin, join] };
            },
            (process, endpoint, token) => new ArkGracefulShutdown().RequestWithResponseConfirmationAsync(process, endpoint,
                response => response.Trim().Equals("World Saved", StringComparison.OrdinalIgnoreCase), token),
            (id, incident) => journal.RecordIncidentAsync(id, incident),
            TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(20), TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(2), RconResponseMode.SinglePacket,
            observeState: (id, _, state) => Console.WriteLine("Verification server state: " + state));
        await using var runtime = factory.Create(definition.Id);
        var started = DateTimeOffset.UtcNow;
        var ready = false; var graceful = false;
        await File.WriteAllTextAsync(Path.Combine(evidence, resultName), JsonSerializer.Serialize(new
            { StartedAt = started, Completed = false, State = "Starting", AdministrationReady = false }), ct);
        try
        {
            await runtime.StartAsync(definition, ct);
            ready = runtime.State == ServerState.Running;
            if (afterReady is not null) await afterReady(definition, ct);
            Console.WriteLine("Authenticated local server administration is ready. Checking save and shutdown.");
            graceful = await runtime.StopAsync(ct);
            var stopped = await new InstallationStopGuard().CheckAsync(directory, ct);
            var persisted = await ServerConfigurationReader.ReadAsync(repository, new(definition.Id, "GameUserSettings.ini"), ct);
            var persistedIni = new IniDocument(persisted.Text!);
            var settingsPreserved = persistedIni.GetValues("ServerSettings", "ServerAdminPassword").SequenceEqual(new[] { admin })
                && persistedIni.GetValues("ServerSettings", "ServerPassword").SequenceEqual(new[] { join })
                && (definition.Edition != ArkEdition.ASE || persistedIni.GetValues("/Script/ShooterGame.ShooterGameUserSettings", "Version").SequenceEqual(new[] { "5" }));
            var result = new { Edition = definition.Edition.ToString(), Platform = "Windows", AdministrationReady = ready,
                SettingsPreserved = settingsPreserved,
                GracefulShutdown = graceful, NativeStopped = stopped.State == InstallationStopState.Stopped,
                ElapsedSeconds = (DateTimeOffset.UtcNow - started).TotalSeconds,
                WorldFiles = Directory.Exists(saved) ? Directory.GetFiles(saved, "*.ark", SearchOption.AllDirectories).Length : 0,
                GameplayConnectionVerified = false, ModsVerified = false };
            await File.WriteAllTextAsync(Path.Combine(evidence, resultName), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), ct);
            return result;
        }
        catch
        {
            await File.WriteAllTextAsync(Path.Combine(evidence, resultName), JsonSerializer.Serialize(new { AdministrationReady = ready, GracefulShutdown = graceful, Completed = false }), CancellationToken.None);
            throw;
        }
    }

    private static int FindAvailablePortSet()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var game = RandomNumberGenerator.GetInt32(49152, 60000);
            try
            {
                using var first = new UdpClient(new IPEndPoint(IPAddress.Any, game));
                using var second = new UdpClient(new IPEndPoint(IPAddress.Any, game + 1));
                using var query = new UdpClient(new IPEndPoint(IPAddress.Any, game + 2));
                using var rcon = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                rcon.Bind(new IPEndPoint(IPAddress.Any, game + 3));
                return game;
            }
            catch (SocketException) { }
        }
        throw new IOException("No available verification port set could be found.");
    }
}
