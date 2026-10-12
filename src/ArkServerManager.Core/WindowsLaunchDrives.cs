using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Concurrent;

namespace ArkServerManager.Core;

public interface IWindowsLaunchDriveBackend
{
    IReadOnlyList<string> Query(char letter);
    void Create(char letter, string target);
    void RemoveExact(char letter, string target);
    string Resolve(string directory);
}
public sealed class NativeWindowsLaunchDriveBackend : IWindowsLaunchDriveBackend
{
    public IReadOnlyList<string> Query(char letter)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var buffer = new char[32768];
        var count = QueryDosDevice(letter + ":", buffer, (uint)buffer.Length);
        if (count == 0)
        {
            if (Marshal.GetLastWin32Error() == 2) return [];
            throw new IOException("Available native launch drives could not be inspected.");
        }
        return new string(buffer, 0, checked((int)count)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }
    public void Create(char letter, string target)
    {
        if (!OperatingSystem.IsWindows() || !DefineDosDevice(1 | 8, letter + ":", target))
            throw new IOException("The native launch drive could not be created.");
    }
    public void RemoveExact(char letter, string target)
    {
        if (!OperatingSystem.IsWindows() || !DefineDosDevice(1 | 2 | 4 | 8, letter + ":", target))
            throw new IOException("The owned native launch drive could not be removed.");
    }
    public string Resolve(string directory)
    {
        for (var ancestor = Path.GetFullPath(directory); ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            if ((Directory.Exists(ancestor) || File.Exists(ancestor)) && (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked server storage cannot be given a native launch drive automatically.");
        return Path.TrimEndingDirectorySeparator(WindowsNativePath.ResolveFinalExisting(directory));
    }
    [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string name, [Out] char[] destination, uint capacity);
    [DllImport("kernel32.dll", EntryPoint = "DefineDosDeviceW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DefineDosDevice(uint flags, string name, string target);
}

public enum LaunchDrivePhase { Prepared, Active }
public sealed record WindowsLaunchDrive(Guid Id, char Letter, string Directory, string Target, LaunchDrivePhase Phase);
public sealed record WindowsLaunchDriveDocument(int SchemaVersion, List<WindowsLaunchDrive> Drives);

/// <summary>The Host owns the lifecycle/file scope. No server files are moved and occupied letters are preserved.</summary>
public sealed class WindowsLaunchDrives(string recordFile, IWindowsLaunchDriveBackend backend,
    Func<string, CancellationToken, Task<bool>> isStopped)
{
    private readonly AtomicJsonStore<WindowsLaunchDriveDocument> store = new(recordFile);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = Gates.GetOrAdd(Path.GetFullPath(recordFile), _ => new(1, 1));

    public Task<string> GetOrCreateAsync(string directory, CancellationToken ct = default)
        => WithNativeMutexAsync(() => GetOrCreateCoreAsync(directory, ct), ct);

    private async Task<string> GetOrCreateCoreAsync(string directory, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Choose an absolute native server folder.");
        await gate.WaitAsync(ct);
        try
        {
            var canonical = backend.Resolve(directory);
            var document = await LoadAsync(ct);
            var present = document.Drives.Where(e => backend.Query(e.Letter).Count != 0).ToList();
            if (present.Count != document.Drives.Count)
            {
                document = document with { Drives = present };
                await store.SaveAsync(document, ct);
            }
            foreach (var entry in document.Drives.Where(e => e.Directory.Equals(canonical, StringComparison.OrdinalIgnoreCase) && e.Phase == LaunchDrivePhase.Active))
                if (Matches(entry) && backend.Resolve(entry.Letter + ":\\").Equals(canonical, StringComparison.OrdinalIgnoreCase))
                    return entry.Letter + ":\\";
            if (!await isStopped(canonical, ct)) throw new InvalidOperationException("The installation must be verified stopped before creating its native launch path.");
            // Prepared records with an existing mapping have uncertain ownership after
            // interruption. Preserve them and choose a genuinely unused letter.
            for (var letter = 'Z'; letter >= 'D'; letter--)
            {
                if (document.Drives.Any(e => e.Letter == letter) || backend.Query(letter).Count != 0) continue;
                var entry = new WindowsLaunchDrive(Guid.NewGuid(), letter, canonical, "\\??\\" + canonical, LaunchDrivePhase.Prepared);
                document = document with { Drives = [.. document.Drives, entry] };
                await store.SaveAsync(document, ct);
                if (backend.Query(letter).Count != 0) throw new IOException("The selected launch drive became occupied. Its mapping was preserved.");
                backend.Create(letter, entry.Target);
                if (!Matches(entry) || !backend.Resolve(letter + ":\\").Equals(canonical, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The native launch drive's target could not be verified. Its prepared record was retained.");
                document = document with { Drives = document.Drives.Select(e => e.Id == entry.Id ? e with { Phase = LaunchDrivePhase.Active } : e).ToList() };
                await store.SaveAsync(document, ct);
                return letter + ":\\";
            }
            throw new IOException("No free drive letter is available for the native server launch path.");
        }
        finally { gate.Release(); }
    }

    public async Task CleanupStoppedAsync(CancellationToken ct = default)
        => await WithNativeMutexAsync(async () => { await CleanupStoppedCoreAsync(ct); return true; }, ct);

    private async Task CleanupStoppedCoreAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var document = await LoadAsync(ct);
            foreach (var entry in document.Drives.ToArray())
            {
                ct.ThrowIfCancellationRequested();
                if (backend.Query(entry.Letter).Count == 0)
                {
                    document = document with { Drives = document.Drives.Where(e => e.Id != entry.Id).ToList() };
                    await store.SaveAsync(document, ct); continue;
                }
                if (entry.Phase != LaunchDrivePhase.Active || !Matches(entry) || !await isStopped(entry.Directory, ct)) continue;
                if (!Matches(entry)) continue;
                backend.RemoveExact(entry.Letter, entry.Target);
                // If another mapping remains, leave it alone. The exact owned target was removed.
                document = document with { Drives = document.Drives.Where(e => e.Id != entry.Id).ToList() };
                await store.SaveAsync(document, ct);
            }
        }
        finally { gate.Release(); }
    }

    private bool Matches(WindowsLaunchDrive entry)
    {
        var targets = backend.Query(entry.Letter);
        return targets.Count == 1 && targets[0].Equals(entry.Target, StringComparison.OrdinalIgnoreCase);
    }
    private Task<T> WithNativeMutexAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        if (backend is not NativeWindowsLaunchDriveBackend) return action();
        return Task.Run(() =>
        {
            using var mutex = new Mutex(false, "Local\\ARKServerManager.NativeLaunchDrives");
            var acquired = false;
            try
            {
                try
                {
                    var result = WaitHandle.WaitAny([mutex, ct.WaitHandle], TimeSpan.FromSeconds(30));
                    if (result == WaitHandle.WaitTimeout) throw new TimeoutException("Another native launch-path operation is still busy.");
                    if (result == 1) ct.ThrowIfCancellationRequested();
                    acquired = true;
                }
                catch (AbandonedMutexException) { acquired = true; }
                ct.ThrowIfCancellationRequested();
                // A Windows mutex belongs to its OS thread. Keep this outer worker
                // blocked until asynchronous storage work completes, then release here.
                return action().GetAwaiter().GetResult();
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }, ct);
    }
    private async Task<WindowsLaunchDriveDocument> LoadAsync(CancellationToken ct)
    {
        var document = await store.LoadAsync(ct) ?? new(1, []);
        if (document.SchemaVersion != 1 || document.Drives is null || document.Drives.Any(e => e is null || e.Id == Guid.Empty
            || e.Letter is < 'D' or > 'Z' || !Enum.IsDefined(e.Phase) || !Path.IsPathFullyQualified(e.Directory)
            || e.Target != "\\??\\" + e.Directory) || document.Drives.Select(e => e.Id).Distinct().Count() != document.Drives.Count
            || document.Drives.Select(e => e.Letter).Distinct().Count() != document.Drives.Count)
            throw new InvalidDataException("The native launch-drive ownership record is invalid.");
        return document;
    }
}
