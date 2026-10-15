using System.Runtime.InteropServices;
using System.Collections.Concurrent;

namespace ArkServerManager.Core;

public interface ILegacyWindowsLaunchDriveBackend
{
    IReadOnlyList<string> Query(char letter);
    void RemoveExact(char letter, string target);
}
public sealed class NativeLegacyWindowsLaunchDriveBackend : ILegacyWindowsLaunchDriveBackend
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
    public void RemoveExact(char letter, string target)
    {
        if (!OperatingSystem.IsWindows() || !DefineDosDevice(1 | 2 | 4 | 8, letter + ":", target))
            throw new IOException("The owned native launch drive could not be removed.");
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

/// <summary>Removal-only migration for mappings created by older versions. Cannot allocate or create drives.</summary>
public sealed class LegacyWindowsLaunchDriveCleanup(string recordFile, ILegacyWindowsLaunchDriveBackend backend,
    Func<string, CancellationToken, Task<bool>> isStopped)
{
    private readonly AtomicJsonStore<WindowsLaunchDriveDocument> store = new(recordFile);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = Gates.GetOrAdd(Path.GetFullPath(recordFile), _ => new(1, 1));

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
        if (backend is not NativeLegacyWindowsLaunchDriveBackend) return action();
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
