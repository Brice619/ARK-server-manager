using System.Security.Cryptography;

namespace ArkServerManager.Core;

public enum InstallationImportMode { ManageInPlace, Copy, Move }
public sealed record InstallationTransferResult(string Directory, bool OriginalRemoved, string? CleanupRequired, string Message);
public sealed record InstallationDestinationRequest(string Source, string Destination);
public sealed record InstallationDestinationCheck(string Destination, bool Valid, string Message);

/// <summary>Transfers only an explicitly selected installation after consent and shutdown checks.</summary>
public sealed class InstallationTransfer
{
    public static string CheckManagedDestination(string managementDirectory, string sourceDirectory, string destinationDirectory)
    {
        if (string.IsNullOrWhiteSpace(managementDirectory) || !Path.IsPathFullyQualified(managementDirectory))
            throw new ArgumentException("The management folder must be an absolute path.");
        var target = CheckDestination(sourceDirectory, destinationDirectory);
        var relative = Path.GetRelativePath(Path.GetFullPath(managementDirectory), target);
        if (relative == "." || Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Choose a new server folder inside your management folder.");
        return target;
    }
    /// <summary>Read-only path preflight; execution repeats these checks before transferring.</summary>
    public static string CheckDestination(string sourceDirectory, string destinationDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Path.IsPathFullyQualified(sourceDirectory)
            || string.IsNullOrWhiteSpace(destinationDirectory) || !Path.IsPathFullyQualified(destinationDirectory))
            throw new ArgumentException("Choose absolute source and destination folders.");
        var source = Path.GetFullPath(sourceDirectory);
        var target = Path.GetFullPath(destinationDirectory);
        RejectRoot(source); RejectRoot(target);
        if (Within(source, target) || Within(target, source))
            throw new ArgumentException("Source and destination must not overlap.");
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("The destination already exists.");
        RejectLinkedAncestors(Path.GetDirectoryName(target)!);
        return target;
    }
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Func<string, CancellationToken, Task<bool>> ensureStopped;
    public InstallationTransfer(Func<string, CancellationToken, Task<bool>> ensureStopped) => this.ensureStopped = ensureStopped;
    public InstallationTransfer(InstallationStopGuard guard,
        Func<InstallationStopCheck, CancellationToken, Task<bool>> requestShutdown)
        : this(async (directory, ct) =>
        {
            var check = await guard.EnsureStoppedAsync(directory, requestShutdown, ct);
            if (check.State == InstallationStopState.Unverified)
                throw new InvalidOperationException("The installation's stopped state could not be verified: " + string.Join(" ", check.Findings));
            return check.State == InstallationStopState.Stopped;
        }) { }

    public async Task<InstallationTransferResult> ExecuteAsync(string sourceDirectory, string? destinationDirectory,
        ArkEdition edition, InstallationImportMode mode, bool transferConfirmed, CancellationToken ct = default,
        Func<InstallationTransferProof, CancellationToken, Task>? beforePublish = null)
    {
        if (!Enum.IsDefined(mode) || !Enum.IsDefined(edition)) throw new ArgumentException("Invalid import selection.");
        var source = Path.GetFullPath(sourceDirectory);
        if (!new InstallationDiscovery().Evaluate(source).Candidates.Any(c => c.Edition == edition))
            throw new InvalidDataException("The selected folder is not a recognized installation of this ARK edition.");
        if (mode == InstallationImportMode.ManageInPlace) return new(source, false, null, "Installation selected in its current location.");
        if (!transferConfirmed) throw new InvalidOperationException("Confirm the copy or move before transferring files.");
        if (string.IsNullOrWhiteSpace(destinationDirectory)) throw new ArgumentException("Choose a destination folder.");
        var target = CheckDestination(source, destinationDirectory);
        await gate.WaitAsync(ct);
        var parent = Path.GetDirectoryName(target)!;
        var staging = Path.Combine(parent, ".import-" + Guid.NewGuid().ToString("N"));
        var retired = Path.Combine(Path.GetDirectoryName(source)!, ".import-retired-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (Directory.Exists(target) || File.Exists(target)) throw new IOException("The destination already exists.");
            RejectLinkedAncestors(parent);
            RejectLinks(source);
            if (!await ensureStopped(source, ct)) throw new InvalidOperationException("The server must stop before copying or moving its files.");
            // Inspect the entire tree before any atomic move so links never escape the selected installation.
            var entries = Entries(source).ToArray();
            Directory.CreateDirectory(parent);
            ct.ThrowIfCancellationRequested();
            if (mode == InstallationImportMode.Move && Path.GetPathRoot(source) == Path.GetPathRoot(target))
            {
                if (!await ensureStopped(source, ct)) throw new InvalidOperationException("The server started during preflight; the original was preserved.");
                if (beforePublish is not null)
                {
                    var proof = await InstallationTransferProofs.CaptureAsync(source, source, target, retired, ct);
                    await beforePublish(proof, ct);
                    await InstallationTransferProofs.VerifyAsync(proof, source, ct);
                    if (!await ensureStopped(source, ct)) throw new InvalidOperationException("The server started before publication; the original was preserved.");
                }
                try
                {
                    Directory.Move(source, target);
                    return new(target, true, null, "Installation moved.");
                }
                catch (IOException) when (Directory.Exists(source) && !Directory.Exists(target))
                {
                    // A mount boundary can exist under one Linux root. Fall back to verified copy.
                }
            }
            Directory.CreateDirectory(staging);
            var hashes = new Dictionary<string, byte[]>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(source, entry);
                var copied = Path.Combine(staging, relative);
                if (Directory.Exists(entry)) { Directory.CreateDirectory(copied); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(copied)!);
                await using (var input = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read))
                await using (var output = new FileStream(copied, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await input.CopyToAsync(output, ct);
                    await output.FlushAsync(ct);
                    output.Flush(true);
                }
                hashes.Add(relative, await HashAsync(copied, ct));
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(copied, File.GetUnixFileMode(entry));
            }
            var currentEntries = Entries(source).Select(e => Path.GetRelativePath(source, e)).ToHashSet(hashes.Comparer);
            if (!currentEntries.SetEquals(entries.Select(e => Path.GetRelativePath(source, e))))
                throw new IOException("Installation contents changed during transfer; try again after it is stopped.");
            foreach (var (relative, hash) in hashes)
            {
                var originalHash = await HashAsync(Path.Combine(source, relative), ct);
                var copiedHash = await HashAsync(Path.Combine(staging, relative), ct);
                if (!CryptographicOperations.FixedTimeEquals(hash, originalHash) || !CryptographicOperations.FixedTimeEquals(hash, copiedHash))
                    throw new IOException("An installation file changed during transfer; the original was preserved.");
            }
            ct.ThrowIfCancellationRequested();
            if (!await ensureStopped(source, ct)) throw new InvalidOperationException("The server started during transfer; the original was preserved.");
            if (beforePublish is not null)
            {
                var proof = await InstallationTransferProofs.CaptureAsync(staging, source, target, retired, ct);
                await beforePublish(proof, ct);
                await InstallationTransferProofs.VerifyAsync(proof, source, ct);
                if (!await ensureStopped(source, ct)) throw new InvalidOperationException("The server started before publication; the original was preserved.");
            }
            Directory.Move(staging, target);
            if (mode == InstallationImportMode.Copy) return new(target, false, null, "Installation copied and verified.");
            // Quarantine the checked source beside itself before cleanup. The verified target is authoritative.
            try
            {
                Directory.Move(source, retired);
                Directory.Delete(retired, true);
                return new(target, true, null, "Installation moved through a verified copy.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                var cleanup = Directory.Exists(retired) ? retired : source;
                return new(target, !Directory.Exists(source), cleanup, "Verified installation is ready, but original-file cleanup needs attention: " + error.Message);
            }
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            finally { gate.Release(); }
        }
    }

    private static async Task<byte[]> HashAsync(string path, CancellationToken ct)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await SHA256.HashDataAsync(input, ct);
    }

    private static IEnumerable<string> Entries(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            RejectLinks(entry);
            yield return entry;
            if (Directory.Exists(entry)) foreach (var child in Entries(entry)) yield return child;
        }
    }

    private static void RejectLinks(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked installation files require explicit import handling.");
    }

    private static void RejectLinkedAncestors(string directory)
    {
        for (var ancestor = new DirectoryInfo(directory); ancestor is not null; ancestor = ancestor.Parent)
        {
            // Missing destination folders are allowed; their existing parents must stay within the selected path.
            if (ancestor.Exists) RejectLinks(ancestor.FullName);
            else if (File.Exists(ancestor.FullName)) throw new IOException("A destination parent is a file.");
        }
    }

    private static void RejectRoot(string path)
    {
        if (path.TrimEnd(Path.DirectorySeparatorChar) == Path.GetPathRoot(path)!.TrimEnd(Path.DirectorySeparatorChar))
            throw new ArgumentException("Choose a dedicated installation folder rather than a filesystem root.");
    }

    private static bool Within(string directory, string path)
    {
        var relative = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar);
    }
}
