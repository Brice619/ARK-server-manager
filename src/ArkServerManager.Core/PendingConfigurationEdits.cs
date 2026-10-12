using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace ArkServerManager.Core;

public sealed record PendingConfigurationEdit(Guid Id, ServerDefinition Server, ServerConfigurationEdit Edit, bool Started = false)
{
    public bool SourceMissing { get; init; }
    public override string ToString() => $"Pending configuration for {Server.Id}, {Edit.FileName}; contents omitted";
}
public sealed record PendingConfigurationDocument(int SchemaVersion, List<PendingConfigurationEdit> Changes);
public sealed record PendingConfigurationStatus(Guid Id, Guid ServerId, string FileName, bool Started);
public sealed record PendingConfigurationDiscard(Guid ServerId, Guid Id, bool Confirmed);
public sealed record PendingConfigurationDiscardResult(Guid ServerId, Guid Id, bool Discarded);
public sealed record PendingConfigurationReviewRequest(Guid ServerId, Guid Id);
public sealed record PendingConfigurationReview(Guid Id, Guid ServerId, string FileName, string Text, bool Started)
{
    public override string ToString() => $"Saved configuration review for {ServerId}, {FileName}; contents omitted";
}
public enum ConfigurationResolutionChoice { KeepCurrentFile, UseSavedText }
public sealed record PendingConfigurationResolutionReview(Guid Id, Guid ServerId, string FileName, string CurrentText, string SavedText, bool Started, string? CurrentSourceSHA256, string Token)
{
    public bool CurrentMissing { get; init; }
    public override string ToString() => $"Configuration resolution review for {ServerId}, {FileName}; contents omitted";
}
public sealed record PendingConfigurationResolution(Guid ServerId, Guid Id, string Token, ConfigurationResolutionChoice Choice, bool Confirmed);
public sealed record PendingConfigurationResolutionResult(Guid ServerId, Guid PreviousId, Guid? ReplacementId, bool Applied, bool KeptCurrent);

/// <summary>Exact reviewed file replacements, applied only inside the caller's stopped lifecycle scope.</summary>
public sealed class PendingConfigurationEdits(CatalogRepository repository, string file,
    Func<ServerDefinition, CancellationToken, Task> requireStopped)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = Gates.GetOrAdd(Path.GetFullPath(file), _ => new(1, 1));
    private readonly AtomicJsonStore<PendingConfigurationDocument> store = new(file, true);

    public async Task<IReadOnlyList<PendingConfigurationStatus>> ReadAsync(Guid serverId, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { return (await LoadAsync(ct)).Changes.Where(p => p.Server.Id == serverId)
            .Select(p => new PendingConfigurationStatus(p.Id, p.Server.Id, p.Edit.FileName, p.Started)).ToArray(); }
        finally { gate.Release(); }
    }
    public async Task<PendingConfigurationReview> ReviewAsync(PendingConfigurationReviewRequest request, CancellationToken ct = default)
    {
        if (request.ServerId == Guid.Empty || request.Id == Guid.Empty) throw new ArgumentException("Choose a saved configuration edit.");
        await gate.WaitAsync(ct);
        try
        {
            var record = (await LoadAsync(ct)).Changes.SingleOrDefault(p => p.Id == request.Id && p.Server.Id == request.ServerId)
                ?? throw new InvalidOperationException("The saved edit changed or was already applied. Refresh its status.");
            return new(record.Id, record.Server.Id, record.Edit.FileName, record.Edit.Text, record.Started);
        }
        finally { gate.Release(); }
    }
    public async Task<PendingConfigurationResolutionReview> ReviewResolutionAsync(PendingConfigurationReviewRequest request, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var entry = (await LoadAsync(ct)).Changes.SingleOrDefault(p => p.Id == request.Id && p.Server.Id == request.ServerId)
                ?? throw new InvalidOperationException("The saved edit changed. Refresh its status.");
            return await ResolutionReviewAsync(entry, ct);
        }
        finally { gate.Release(); }
    }
    public async Task<string> ResolveAsync(Guid operationId, PendingConfigurationResolution request, CancellationToken ct = default)
    {
        if (operationId == Guid.Empty || request.ServerId == Guid.Empty || request.Id == Guid.Empty || !request.Confirmed || !Enum.IsDefined(request.Choice))
            throw new ArgumentException("Review and confirm how to resolve the saved edit.");
        await gate.WaitAsync(ct);
        try
        {
            var document = await LoadAsync(ct);
            var entry = document.Changes.SingleOrDefault(p => p.Id == request.Id && p.Server.Id == request.ServerId)
                ?? throw new InvalidOperationException("The saved edit changed. Review it again.");
            if (document.Changes.Any(p => p.Id == operationId)) throw new InvalidOperationException("Use a new resolution identity.");
            var server = (await repository.ReadAsync(ct)).Servers.Single(s => s.Id == request.ServerId);
            RequireBinding(entry.Server, server); await requireStopped(server, ct);
            var review = await ResolutionReviewAsync(entry, ct);
            if (request.Token is not { Length: 64 } || request.Token != review.Token)
                throw new InvalidOperationException("The saved edit, current file or server changed. Review both versions again.");
            var source = await ServerConfigurationReader.ReadAsync(repository, new(server.Id, entry.Edit.FileName), ct);
            if (source.SourceSHA256 != review.CurrentSourceSHA256) throw new InvalidOperationException("The current file changed during resolution review.");
            var changes = document.Changes.Where(p => p.Id != entry.Id).ToList();
            if (review.CurrentMissing && request.Choice == ConfigurationResolutionChoice.KeepCurrentFile)
                throw new InvalidOperationException("The current file is missing. Choose saved text to restore it or leave recovery pending.");
            if (request.Choice == ConfigurationResolutionChoice.UseSavedText)
            {
                changes.Add(new(operationId, server, entry.Edit with { SourceSHA256 = source.SourceSHA256 ?? entry.Edit.SourceSHA256 }, false)
                    { SourceMissing = review.CurrentMissing });
            }
            await store.SaveAsync(document with { Changes = changes }, ct);
            return entry.Edit.FileName;
        }
        finally { gate.Release(); }
    }
    private async Task<PendingConfigurationResolutionReview> ResolutionReviewAsync(PendingConfigurationEdit entry, CancellationToken ct)
    {
        var current = (await repository.ReadAsync(ct)).Servers.Single(s => s.Id == entry.Server.Id);
        RequireBinding(entry.Server, current);
        var source = await ServerConfigurationReader.ReadAsync(repository, new(current.Id, entry.Edit.FileName), ct);
        var token = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Entry = entry, Server = current, source.SourceSHA256, source.Platform })));
        return new(entry.Id, current.Id, entry.Edit.FileName, source.Text ?? "", entry.Edit.Text, entry.Started, source.SourceSHA256, token)
            { CurrentMissing = source.Text is null };
    }
    public async Task EnqueueAsync(Guid operationId, ServerConfigurationEdit edit, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var server = (await repository.ReadAsync(ct)).Servers.Single(s => s.Id == edit.ServerId);
            var record = new PendingConfigurationEdit(operationId, server, edit); Validate(record);
            var document = await LoadAsync(ct);
            var previous = document.Changes.SingleOrDefault(p => p.Server.Id == edit.ServerId && p.Edit.FileName == edit.FileName);
            if (previous is not null)
            {
                if (previous.Id == operationId && previous.Edit == edit) return;
                throw new InvalidOperationException("This file already has a saved change. Apply or review it before saving another.");
            }
            var source = await ServerConfigurationReader.ReadAsync(repository, new(edit.ServerId, edit.FileName), ct);
            if (source.SourceSHA256 != edit.SourceSHA256) throw new InvalidOperationException("The configuration changed. Reload it before saving.");
            await store.SaveAsync(document with { Changes = [.. document.Changes, record] }, ct);
        }
        finally { gate.Release(); }
    }
    public async Task DiscardAsync(PendingConfigurationDiscard request, CancellationToken ct = default)
    {
        if (!request.Confirmed || request.ServerId == Guid.Empty || request.Id == Guid.Empty) throw new ArgumentException("Review and confirm the saved edit before canceling it.");
        await gate.WaitAsync(ct);
        try
        {
            var document = await LoadAsync(ct);
            var entry = document.Changes.SingleOrDefault(p => p.Id == request.Id && p.Server.Id == request.ServerId)
                ?? throw new InvalidOperationException("The saved edit changed or was already applied. Refresh its status.");
            if (entry.Started) throw new InvalidOperationException("Application has begun. Keep its recovery record and review the current file.");
            await store.SaveAsync(document with { Changes = document.Changes.Where(p => p.Id != request.Id).ToList() }, ct);
        }
        finally { gate.Release(); }
    }
    public async Task ApplyBeforeStartAsync(Guid serverId, CancellationToken ct = default, string? onlyFile = null)
    {
        await gate.WaitAsync(ct);
        try
        {
            var document = await LoadAsync(ct);
            foreach (var recorded in document.Changes.Where(p => p.Server.Id == serverId && (onlyFile is null || p.Edit.FileName == onlyFile)).ToArray())
            {
                var current = (await repository.ReadAsync(ct)).Servers.Single(s => s.Id == serverId);
                RequireBinding(recorded.Server, current);
                await requireStopped(current, ct);
                var source = await ServerConfigurationReader.ReadAsync(repository, new(serverId, recorded.Edit.FileName), ct);
                var alreadyApplied = recorded.Started && source.Text == recorded.Edit.Text;
                if (!alreadyApplied)
                {
                    if (recorded.SourceMissing ? source.Text is not null : source.SourceSHA256 != recorded.Edit.SourceSHA256)
                        throw new InvalidOperationException("A saved configuration conflicts with changed files. Its recovery record was retained.");
                    var started = recorded with { Started = true };
                    document = document with { Changes = document.Changes.Select(p => p.Id == recorded.Id ? started : p).ToList() };
                    await store.SaveAsync(document, ct);
                    async Task VerifiedStopped(ServerDefinition selected, Func<CancellationToken, Task> change, CancellationToken token)
                    {
                        RequireBinding(recorded.Server, selected);
                        await requireStopped(selected, token); await change(token);
                    }
                    if (recorded.SourceMissing)
                        await ServerConfigurationWriter.CreateAsync(repository, serverId, recorded.Edit.FileName, recorded.Edit.Text, VerifiedStopped, ct);
                    else await ServerConfigurationWriter.WriteAsync(repository, recorded.Edit, VerifiedStopped, ct);
                }
                document = document with { Changes = document.Changes.Where(p => p.Id != recorded.Id).ToList() };
                await store.SaveAsync(document, ct);
            }
        }
        finally { gate.Release(); }
    }
    public async Task RemoveDeletedAsync(ServerDeletionRecord deletion, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            DeletedServerRecordCleanup.RequireCompleted(await repository.ReadAsync(ct), deletion);
            var document = await LoadAsync(ct);
            foreach (var entry in document.Changes.Where(p => p.Server.Id == deletion.ServerId))
                DeletedServerRecordCleanup.RequireDirectory(deletion, entry.Server.Directory);
            if (document.Changes.Any(p => p.Server.Id == deletion.ServerId))
                await store.SaveAsync(document with { Changes = document.Changes.Where(p => p.Server.Id != deletion.ServerId).ToList() }, ct);
        }
        finally { gate.Release(); }
    }
    private async Task<PendingConfigurationDocument> LoadAsync(CancellationToken ct)
    {
        var document = await store.LoadAsync(ct) ?? new(1, []);
        if (document.SchemaVersion != 1 || document.Changes is null)
            throw new InvalidDataException("Invalid saved configuration document.");
        foreach (var entry in document.Changes) Validate(entry);
        if (document.Changes.Select(p => p.Id).Distinct().Count() != document.Changes.Count)
            throw new InvalidDataException("Duplicate saved configuration operations.");
        if (document.Changes.Select(p => (p.Server.Id, p.Edit.FileName)).Distinct().Count() != document.Changes.Count)
            throw new InvalidDataException("Duplicate saved configuration files.");
        return document;
    }
    private static void Validate(PendingConfigurationEdit entry)
    {
        if (entry?.Server is not { } server || entry.Edit is not { } edit || entry.Id == Guid.Empty || server.Id == Guid.Empty
            || edit.ServerId != server.Id || !Path.IsPathFullyQualified(server.Directory) || !Enum.IsDefined(server.Edition)
            || edit.FileName is not ("GameUserSettings.ini" or "Game.ini") || edit.SourceSHA256 is not { Length: 64 }
            || edit.SourceSHA256.Any(c => !char.IsAsciiHexDigit(c)) || edit.Text is null || edit.Text.Contains('\0')
            || Encoding.UTF8.GetByteCount(edit.Text) > 4 * 1024 * 1024 || ConfigurationSyntax.Check(edit.Text).Count > 0)
            throw new InvalidDataException("Review the saved configuration before applying it.");
    }
    private static void RequireBinding(ServerDefinition expected, ServerDefinition current)
    {
        if (expected.Id != current.Id || expected.Directory != current.Directory || expected.Edition != current.Edition
            || expected.Map != current.Map || expected.ClusterId != current.ClusterId)
            throw new InvalidOperationException("The saved configuration's server changed. Review its pending change.");
    }
}
