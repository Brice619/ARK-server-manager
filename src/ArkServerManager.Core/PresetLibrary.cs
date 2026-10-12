using System.Collections.Concurrent;
using System.Text.Json;

namespace ArkServerManager.Core;

public sealed record PresetEntry(Guid Id, ServerPreset Preset);
public sealed record PresetLibraryDocument(int SchemaVersion, List<PresetEntry> Entries);
public sealed record PresetExportDocument(int SchemaVersion, ServerPreset Preset);

public sealed class PresetLibrary
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly string path;
    private readonly AtomicJsonStore<PresetLibraryDocument> store;
    private readonly SemaphoreSlim gate;

    public PresetLibrary(string path)
    {
        this.path = Path.GetFullPath(path);
        store = new(this.path);
        gate = Gates.GetOrAdd(this.path, _ => new(1, 1));
    }

    public async Task<IReadOnlyList<PresetEntry>> ListAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { return (await LoadAsync(ct)).Entries.Select(Clone).ToArray(); }
        finally { gate.Release(); }
    }

    public Task<PresetEntry> SaveAsync(ServerPreset preset, Guid? existingId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (preset.Settings is null || preset.Settings.Values is null || preset.Mods is null)
            throw new ArgumentException("Preset settings and mod list are required.");
        preset = Clone(preset);
        Validate(preset);
        return ChangeAsync(entries =>
        {
            var entry = new PresetEntry(existingId ?? Guid.NewGuid(), preset);
            if (existingId is Guid id)
            {
                var index = entries.FindIndex(e => e.Id == id);
                if (index < 0) throw new ArgumentException("The preset no longer exists.");
                entries[index] = entry;
            }
            else entries.Add(entry);
            return Clone(entry);
        }, ct);
    }

    public Task<PresetEntry> RenameAsync(Guid id, string name, CancellationToken ct = default)
        => ChangeAsync(entries =>
        {
            var index = entries.FindIndex(e => e.Id == id);
            if (index < 0) throw new ArgumentException("The preset no longer exists.");
            var entry = entries[index] with { Preset = entries[index].Preset with { Name = name } };
            Validate(entry.Preset);
            entries[index] = entry;
            return Clone(entry);
        }, ct);

    public Task<PresetEntry> EditReviewedAsync(PresetEntry expected, ServerPreset replacement, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.Id == Guid.Empty) throw new ArgumentException("Choose a saved preset.");
        Validate(expected.Preset); Validate(replacement);
        expected = Clone(expected); replacement = Clone(replacement);
        return ChangeAsync(entries =>
        {
            var index = entries.FindIndex(e => e.Id == expected.Id);
            if (index < 0) throw new InvalidOperationException("The preset was deleted. Refresh your library.");
            var current = entries[index];
            // A timed-out reply can be retried with exactly the same reviewed change.
            if (SameContents(current.Preset, replacement)) return Clone(current);
            if (!SameContents(current.Preset, expected.Preset))
                throw new InvalidOperationException("The preset changed while you were editing. Refresh before editing again.");
            var updated = new PresetEntry(expected.Id, replacement);
            entries[index] = updated;
            return Clone(updated);
        }, ct);
    }

    private static bool SameContents(ServerPreset a, ServerPreset b)
        => a.Name == b.Name && a.Edition == b.Edition && a.Mods.SequenceEqual(b.Mods)
            && a.UserSettings == b.UserSettings && a.GameSettings == b.GameSettings
            && a.Settings.Name == b.Settings.Name && a.Settings.MaxPlayers == b.Settings.MaxPlayers
            && a.Settings.GamePort == b.Settings.GamePort && a.Settings.QueryPort == b.Settings.QueryPort
            && a.Settings.RconPort == b.Settings.RconPort && a.Settings.Values.Count == b.Settings.Values.Count
            && a.Settings.Values.All(p => b.Settings.Values.TryGetValue(p.Key, out var value) && value == p.Value);

    public Task<PresetEntry> ImportAsNewAsync(Guid importId, ServerPreset preset, CancellationToken ct = default)
    {
        if (importId == Guid.Empty) throw new ArgumentException("Import identity is required.");
        Validate(preset);
        preset = Clone(preset);
        return ChangeAsync(entries =>
        {
            var existing = entries.SingleOrDefault(e => e.Id == importId);
            if (existing is not null)
            {
                if (JsonSerializer.Serialize(existing.Preset) != JsonSerializer.Serialize(preset))
                    throw new ArgumentException("This import identity has already been used with different contents.");
                return Clone(existing);
            }
            var added = new PresetEntry(importId, preset);
            entries.Add(added);
            return Clone(added);
        }, ct);
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
        => ChangeAsync(entries => entries.RemoveAll(e => e.Id == id) > 0, ct);

    public async Task<PresetExportDocument> ExportAsync(Guid id, CancellationToken ct = default)
    {
        var entry = (await ListAsync(ct)).SingleOrDefault(e => e.Id == id)
            ?? throw new ArgumentException("The preset no longer exists.");
        return new(1, WithoutPasswords(entry.Preset));
    }

    public static ServerPreset WithoutPasswords(ServerPreset preset)
    {
        var exported = Clone(preset);
        exported = exported with { UserSettings = RemovePasswords(exported.UserSettings), GameSettings = RemovePasswords(exported.GameSettings) };
        foreach (var key in exported.Settings.Values.Keys.Where(IsPasswordSetting).ToArray())
            exported.Settings.Values.Remove(key);
        return exported;
    }

    // Preview is read-only. The UI must choose new/save-over-existing explicitly before SaveAsync.
    public static ServerPreset ReadImport(ReadOnlySpan<byte> json)
    {
        if (json.Length is < 1 or > 4 * 1024 * 1024) throw new InvalidDataException("Invalid preset file size.");
        try
        {
            var document = JsonSerializer.Deserialize<PresetExportDocument>(json, new JsonSerializerOptions
            {
                MaxDepth = 32,
                UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
            });
            if (document is null || document.SchemaVersion != 1) throw new InvalidDataException("Unsupported preset file version.");
            Validate(document.Preset);
            return Clone(document.Preset);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NullReferenceException)
        { throw new InvalidDataException("The preset file contains invalid data.", error); }
    }

    private async Task<T> ChangeAsync<T>(Func<List<PresetEntry>, T> change, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var fileLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var document = await LoadAsync(ct);
            var result = change(document.Entries);
            await store.SaveAsync(document, ct);
            return result;
        }
        finally { gate.Release(); }
    }

    private async Task<PresetLibraryDocument> LoadAsync(CancellationToken ct)
    {
        var document = await store.LoadAsync(ct) ?? new(1, []);
        try
        {
            if (document.SchemaVersion != 1 || document.Entries is null
                || document.Entries.Any(e => e.Id == Guid.Empty) || document.Entries.Select(e => e.Id).Distinct().Count() != document.Entries.Count)
                throw new InvalidDataException("Invalid preset library.");
            foreach (var entry in document.Entries) Validate(entry.Preset);
            return document;
        }
        catch (Exception error) when (error is ArgumentException or NullReferenceException)
        { throw new InvalidDataException("The preset library contains invalid data.", error); }
    }

    private static void Validate(ServerPreset preset)
    {
        if (preset is null || preset.Settings is null || preset.Settings.Values is null)
            throw new ArgumentException("Preset settings are required.");
        if (string.IsNullOrWhiteSpace(preset.Name) || !Enum.IsDefined(preset.Edition))
            throw new ArgumentException("A preset name and valid ARK edition are required.");
        preset.Settings.Validate();
        foreach (var text in new[] { preset.UserSettings, preset.GameSettings })
            if (text is not null && (text.Contains('\0') || ConfigurationSyntax.Check(text).Count > 0))
                throw new ArgumentException("The preset configuration has invalid syntax.");
        if (preset.Mods is null || preset.Mods.Any(string.IsNullOrWhiteSpace) || preset.Mods.Distinct().Count() != preset.Mods.Count)
            throw new ArgumentException("The preset mod list is invalid.");
    }
    private static ServerPreset Clone(ServerPreset preset) => preset with { Settings = preset.Settings.Copy(), Mods = [.. preset.Mods] };
    private static string? RemovePasswords(string? text)
    {
        if (text is null) return null;
        var ini = new IniDocument(text);
        ini.RemoveKeys(["ServerPassword", "ServerAdminPassword", "AdminPassword"]);
        return ini.ToString();
    }
    public static bool IsPasswordSetting(string name)
    {
        var key = name.Split("::", StringSplitOptions.None)[^1];
        return key.Equals("ServerPassword", StringComparison.OrdinalIgnoreCase)
            || key.Equals("ServerAdminPassword", StringComparison.OrdinalIgnoreCase)
            || key.Equals("AdminPassword", StringComparison.OrdinalIgnoreCase);
    }
    private static PresetEntry Clone(PresetEntry entry) => entry with { Preset = Clone(entry.Preset) };
}
