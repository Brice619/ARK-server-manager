namespace ArkServerManager.Core;

public sealed record ExistingServerRegistrationSelection(ExistingServerImportReview Expected, Guid ServerId,
    string Map, string Name, int MaxPlayers, int GamePort, int QueryPort, int RconPort, List<string> Mods, bool Confirmed);
public sealed record ExistingServerTransferSelection(ExistingServerRegistrationSelection Registration,
    InstallationImportMode Mode, string Destination, bool TransferConfirmed);
public sealed record ExistingServerTransferRegistrationResult(Guid ServerId, ArkEdition Edition, string Directory,
    bool OriginalRemoved, string? CleanupRequired, string Message);
public sealed record PreparedExistingServerRegistration(ExistingServerImportReview Review, ServerDefinition Server)
{
    public override string ToString() => $"Existing server registration {Server.Id}; configuration values redacted";
}

/// <summary>Registration preserves files. The caller owns the lifecycle scope and verifies stopped state.</summary>
public sealed class ExistingServerRegistration(CatalogRepository repository, ExistingServerImportReviewService reviews,
    Func<string, CancellationToken, Task> requireStopped)
{
    public async Task<PreparedExistingServerRegistration> PrepareAsync(ExistingServerRegistrationSelection selection, CancellationToken ct = default)
    {
        if (!selection.Confirmed || selection.Expected is null || selection.ServerId == Guid.Empty
            || string.IsNullOrWhiteSpace(selection.Map) || string.IsNullOrWhiteSpace(selection.Name) || selection.Mods is null)
            throw new ArgumentException("Review and confirm the existing server's map and settings.");
        var expected = selection.Expected;
        var current = await reviews.ReadAsync(new(expected.Directory, expected.Edition, expected.Platform), ct);
        if (current.Token != expected.Token) throw new InvalidOperationException("The installation changed after review. Review it again before registration.");
        var platform = OperatingSystem.IsWindows() ? "Windows" : "Linux";
        if (current.Platform != platform && !(OperatingSystem.IsLinux() && current.Edition == ArkEdition.ASA && current.Platform == "Windows"))
            throw new NotSupportedException("This installation's server platform cannot run on this computer.");
        if (selection.Map.Length > 256)
            throw new ArgumentException("Enter the documented map package identifier.");
        ServerMapIdentifier.ForLaunch(current.Edition, selection.Map);
        AsaModLaunchArguments.Build(selection.Mods);
        var configuration = await InstallationConfigurationReader.ReadAsync(current.Directory, current.Edition, current.Platform, ct);
        if (configuration.GameUserSettings is null)
            throw new InvalidOperationException("These files do not contain an existing server configuration. Configure the server before adopting it as an existing server.");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in new[] { (Name: "GameUserSettings.ini", Text: configuration.GameUserSettings), (Name: "Game.ini", Text: configuration.Game) })
            foreach (var entry in new IniDocument(file.Text ?? "").Entries().GroupBy(e => (e.Section.ToUpperInvariant(), e.Key.ToUpperInvariant())))
            {
                if (entry.Count() != 1 || PresetLibrary.IsPasswordSetting(entry.Single().Key)) continue;
                var item = entry.Single();
                if (new[] { "SessionName", "ServerName", "MaxPlayers", "Port", "GamePort", "QueryPort", "RCONPort", "RCONEnabled", "ActiveMods", "ModIDS" }
                    .Contains(item.Key, StringComparer.OrdinalIgnoreCase)) continue;
                var key = file.Name == "GameUserSettings.ini" && item.Section.Equals("ServerSettings", StringComparison.OrdinalIgnoreCase)
                    ? item.Key : file.Name + "::" + item.Section + "::" + item.Key;
                values[key] = item.Value;
            }
        var settings = new ServerSettings { Name = selection.Name.Trim(), MaxPlayers = selection.MaxPlayers,
            GamePort = selection.GamePort, QueryPort = selection.QueryPort, RconPort = selection.RconPort, Values = values };
        settings.Validate();
        var server = new ServerDefinition(selection.ServerId, current.Edition, selection.Map, current.Directory, settings)
            { Mods = [.. selection.Mods] };
        var catalog = await repository.ReadAsync(ct);
        var validation = new ServerCatalog();
        foreach (var cluster in catalog.Clusters) validation.AddCluster(cluster);
        foreach (var existing in catalog.Servers) validation.AddServer(existing);
        validation.AddServer(server);
        return new(current, server);
    }

    public async Task<ServerDefinition> RegisterInPlaceAsync(ExistingServerRegistrationSelection selection, CancellationToken ct = default)
    {
        var prepared = await PrepareAsync(selection, ct);
        await requireStopped(prepared.Server.Directory, ct);
        var latest = await reviews.ReadAsync(new(prepared.Review.Directory, prepared.Review.Edition, prepared.Review.Platform), ct);
        if (latest.Token != prepared.Review.Token) throw new InvalidOperationException("The source changed before registration. No files were changed.");
        await repository.ChangeAsync(catalog =>
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (var existing in catalog.Servers)
            {
                var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(existing.Directory));
                if (path.Equals(prepared.Server.Directory, comparison)
                    || path.StartsWith(prepared.Server.Directory + Path.DirectorySeparatorChar, comparison)
                    || prepared.Server.Directory.StartsWith(path + Path.DirectorySeparatorChar, comparison))
                    throw new InvalidOperationException("The installation became managed by another request. Open its existing server.");
            }
            catalog.AddServer(prepared.Server);
        }, ct);
        return prepared.Server;
    }

    public async Task<ExistingServerTransferRegistrationResult> TransferAndRegisterAsync(
        ExistingServerTransferSelection selection, string managementRoot, CancellationToken ct = default)
    {
        if (selection.Mode is not (InstallationImportMode.Copy or InstallationImportMode.Move) || !selection.TransferConfirmed)
            throw new ArgumentException("Confirm whether to copy or move the selected installation.");
        var prepared = await PrepareAsync(selection.Registration, ct);
        var destination = InstallationTransfer.CheckManagedDestination(managementRoot, prepared.Server.Directory, selection.Destination);
        // Application tools, records and shared cluster state are not server destinations.
        var relative = Path.GetRelativePath(Path.GetFullPath(managementRoot), destination);
        var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        if (new[] { "app", "app-settings", "Dependencies", "Clusters" }.Contains(first, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a dedicated server folder outside application tools and shared cluster data.");
        var server = prepared.Server with { Directory = destination };
        var catalog = await repository.ReadAsync(ct);
        RequireSeparateDirectory(catalog.Servers, destination);
        var validation = new ServerCatalog();
        foreach (var cluster in catalog.Clusters) validation.AddCluster(cluster);
        foreach (var existing in catalog.Servers) validation.AddServer(existing);
        validation.AddServer(server);
        var journal = new ExistingServerImportJournal(managementRoot);
        var accepted = new ExistingServerImportRecord(1, selection, server, null);
        await journal.AcceptAsync(accepted, ct);
        var transfer = new InstallationTransfer(async (source, token) =>
        {
            await requireStopped(source, token);
            var latest = await reviews.ReadAsync(new(source, prepared.Review.Edition, prepared.Review.Platform), token);
            if (latest.Token != prepared.Review.Token) throw new InvalidOperationException("The installation changed after review. Review it again before transferring.");
            return true;
        });
        var result = await transfer.ExecuteAsync(prepared.Server.Directory, destination, prepared.Server.Edition,
            selection.Mode, true, ct, (proof, token) => journal.SaveProofAsync(server.Id, proof, token));
        // Once files have been published, finish registration even if the client canceled.
        // A registration failure preserves the destination for explicit inspection/adoption.
        try
        {
            await repository.ChangeAsync(current => { RequireSeparateDirectory(current.Servers, destination); current.AddServer(server); }, CancellationToken.None);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { throw new InvalidOperationException($"Files are available at {destination}, but registration did not finish. Inspect that folder and use its current location to register it. " + error.Message, error); }
        if (result.CleanupRequired is null)
        {
            var completed = (await journal.ReadAsync(CancellationToken.None)).Single(r => r.Server.Id == server.Id);
            await journal.RemoveAsync(completed, CancellationToken.None);
        }
        return new(server.Id, server.Edition, destination, result.OriginalRemoved, result.CleanupRequired, result.Message);
    }

    public async Task<ExistingServerTransferRegistrationResult> RecoverPublishedAsync(string managementRoot,
        ExistingServerImportRecoveryRequest request, CancellationToken ct = default)
    {
        if (!request.Confirmed || request.ServerId == Guid.Empty) throw new ArgumentException("Confirm recovery of this import.");
        var journal = new ExistingServerImportJournal(managementRoot);
        var record = (await journal.ReadAsync(ct)).SingleOrDefault(r => r.Server.Id == request.ServerId)
            ?? throw new InvalidOperationException("This import is no longer recorded. Review the registered servers.");
        if (!Directory.Exists(record.Server.Directory) && !File.Exists(record.Server.Directory))
        {
            // An unpublished transfer can restart from its unchanged, stopped original.
            // Existing publication is never inferred or overwritten by this route.
            await PrepareAsync(record.Selection.Registration, ct);
            await requireStopped(record.Selection.Registration.Expected.Directory, ct);
            await journal.RemoveAsync(record, ct);
            return await TransferAndRegisterAsync(record.Selection, managementRoot, ct);
        }
        if (record.Proof is not { } proof)
            throw new InvalidOperationException("The destination exists without publication evidence. Inspect it before another import.");
        await requireStopped(record.Server.Directory, ct);
        await InstallationTransferProofs.VerifyAsync(proof, record.Server.Directory, ct);
        var current = await repository.ReadAsync(ct);
        var present = current.Servers.SingleOrDefault(s => s.Id == request.ServerId);
        if (present is not null && !SameServer(present, record.Server))
            throw new InvalidOperationException("The registered server changed. Review it before import recovery.");
        RequireSeparateDirectory(current.Servers.Where(s => s.Id != request.ServerId), record.Server.Directory);
        var validation = new ServerCatalog();
        foreach (var cluster in current.Clusters) validation.AddCluster(cluster);
        foreach (var server in current.Servers.Where(s => s.Id != request.ServerId)) validation.AddServer(server);
        validation.AddServer(record.Server);

        if (record.Selection.Mode == InstallationImportMode.Move)
        {
            if (Directory.Exists(proof.Source) && Directory.Exists(proof.RetiredDirectory))
                throw new IOException("Both the original and retired folders exist. Inspect them before recovery.");
            if (File.Exists(proof.Source) || File.Exists(proof.RetiredDirectory)) throw new IOException("An import cleanup path became a file.");
            if (Directory.Exists(proof.Source))
            {
                await requireStopped(proof.Source, ct);
                await InstallationTransferProofs.VerifyAsync(proof, proof.Source, ct);
                InstallationTransferProofs.CheckAncestors(proof.RetiredDirectory);
                await requireStopped(proof.Source, ct);
                Directory.Move(proof.Source, proof.RetiredDirectory);
            }
            if (Directory.Exists(proof.RetiredDirectory))
            {
                await requireStopped(proof.RetiredDirectory, ct);
                await InstallationTransferProofs.VerifyAsync(proof, proof.RetiredDirectory, ct);
                // Only the recorded sibling quarantine, verified byte-for-byte, may be removed.
                InstallationTransferProofs.CheckAncestors(proof.RetiredDirectory);
                Directory.Delete(proof.RetiredDirectory, true);
            }
        }
        await repository.ChangeAsync(catalog =>
        {
            var existing = catalog.Servers.SingleOrDefault(s => s.Id == request.ServerId);
            if (existing is not null)
            {
                if (!SameServer(existing, record.Server)) throw new InvalidOperationException("The registered server changed during recovery.");
                return;
            }
            RequireSeparateDirectory(catalog.Servers, record.Server.Directory); catalog.AddServer(record.Server);
        }, ct);
        await journal.RemoveAsync(record, ct);
        return new(record.Server.Id, record.Server.Edition, record.Server.Directory,
            record.Selection.Mode == InstallationImportMode.Move, null, "Verified import recovered and registered.");
    }

    private static bool SameServer(ServerDefinition first, ServerDefinition second) =>
        System.Text.Json.JsonSerializer.Serialize(first) == System.Text.Json.JsonSerializer.Serialize(second);

    private static void RequireSeparateDirectory(IEnumerable<ServerDefinition> servers, string directory)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var existing in servers)
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(existing.Directory));
            if (path.Equals(directory, comparison) || path.StartsWith(directory + Path.DirectorySeparatorChar, comparison)
                || directory.StartsWith(path + Path.DirectorySeparatorChar, comparison))
                throw new InvalidOperationException("Choose a folder separate from every registered server.");
        }
    }
}
