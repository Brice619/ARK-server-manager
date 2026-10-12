using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ArkServerManager.Core;

/// <summary>Publishes complete documents by renaming a sibling file after flushing it.</summary>
public sealed class AtomicJsonStore<T>(string path, bool privateFile = false) where T : class
{
    private readonly string fullPath = Path.GetFullPath(path);
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public async Task<T?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var stream = await AtomicRecordReader.OpenAsync(fullPath, cancellationToken);
            return await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken)
                ?? throw new InvalidDataException("The saved document is empty.");
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(T value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        await gate.WaitAsync(cancellationToken);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                Share = FileShare.None, BufferSize = 4096, Options = FileOptions.WriteThrough };
            if (privateFile && !OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            FileStream stream;
            if (privateFile && OperatingSystem.IsWindows())
            {
                using var identity = WindowsIdentity.GetCurrent();
                var security = new FileSecurity(); security.SetAccessRuleProtection(true, false);
                foreach (var sid in new[] { identity.User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                    security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
                stream = new FileInfo(temporary).Create(FileMode.CreateNew, FileSystemRights.FullControl,
                    FileShare.None, 4096, FileOptions.WriteThrough, security);
            }
            else stream = new FileStream(temporary, options);
            await using (stream)
            {
                await JsonSerializer.SerializeAsync(stream, value, Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (privateFile) File.Move(temporary, fullPath, overwrite: true);
            else if (File.Exists(fullPath)) File.Replace(temporary, fullPath, null);
            else File.Move(temporary, fullPath);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            finally { gate.Release(); }
        }
    }
}
