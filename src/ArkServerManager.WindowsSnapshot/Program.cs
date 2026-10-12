using Alphaleonis.Win32.Vss;
using ArkServerManager.Core;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class Program
{
    private static TextWriter responseWriter = Console.Out;
    [MTAThread]
    private static int Main(string[] args)
    {
        if (args is ["--capabilities"])
        {
            var windows = OperatingSystem.IsWindows();
            var elevated = windows && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            var loaded = false;
            if (windows)
            {
                try { _ = VssFactoryProvider.Default.GetVssFactory(); loaded = true; }
                catch { }
            }
            Write(new WindowsSnapshotCapability(windows, elevated, loaded,
                !windows ? "Windows snapshots require Windows." : !loaded ? "The required VSS components could not be loaded."
                    : !elevated ? "The VSS components loaded; snapshot creation requires the backup helper's elevated setup."
                    : "The VSS components loaded. No snapshot was created."));
            return 0;
        }
        var pipeMode = args is ["--pipe", _, _];
        if ((args.Length != 0 && !pipeMode) || !OperatingSystem.IsWindows())
        { Write(new { Error = "This snapshot helper requires Windows and a request on its input." }); return 1; }
        try
        {
            using var connectionDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using NamedPipeClientStream? pipe = pipeMode
                ? WindowsSnapshotPipe.ConnectAsync(args[1], int.Parse(args[2]), connectionDeadline.Token).GetAwaiter().GetResult()
                : null;
            using var owner = pipeMode ? Process.GetProcessById(int.Parse(args[2])) : null;
            if (owner is not null)
            {
                // Also exit if the owner dies while native snapshot creation is still running.
                _ = owner.WaitForExitAsync().ContinueWith(_ => Environment.Exit(0), TaskScheduler.Default);
            }
            using var pipeReader = pipe is null ? null : new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
            using var pipeWriter = pipe is null ? null : new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            var input = (TextReader?)pipeReader ?? Console.In;
            responseWriter = (TextWriter?)pipeWriter ?? Console.Out;
            if (pipeMode)
            {
                var elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
                _ = VssFactoryProvider.Default.GetVssFactory();
                Write(new WindowsSnapshotCapability(true, elevated, true, elevated ? "Snapshot helper ready." : "Snapshot privileges are required."));
                if (!elevated) return 1;
            }
            while (input.ReadLine() is { } line)
            {
                var released = Capture(line, input, pipeMode);
                if (!pipeMode || released is null) return 0;
                // Capture's COM scope has ended before acknowledging release.
                Write(new WindowsSnapshotReleaseResponse(1, released.Value));
            }
            return 0;
        }
        catch
        {
            // A disconnected/disposed pipe is itself the failure signal to its owner.
            try { Write(new { Error = "Windows snapshot capture failed. Check backup permissions, required components and volume support." }); }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { }
            return 1;
        }
    }

    private static Guid? Capture(string line, TextReader input, bool pipeMode)
    {
            if (line is null || line.Length > 65536) throw new InvalidDataException("Invalid snapshot request.");
            var request = JsonSerializer.Deserialize<WindowsSnapshotRequest>(line)
                ?? throw new InvalidDataException("Missing snapshot request.");
            if (request.SchemaVersion != 1 || request.Volumes is null || request.Volumes.Count is < 1 or > 26
                || request.Volumes.Any(volume => volume is null || !Regex.IsMatch(volume, @"^[A-Za-z]:\\$", RegexOptions.CultureInvariant))
                || request.Volumes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Volumes.Count)
                throw new InvalidDataException("Invalid volume selection.");
            var volumes = request.Volumes.ToDictionary(volume => volume, GetVolumeName, StringComparer.OrdinalIgnoreCase);
            using var components = VssFactoryProvider.Default.GetVssFactory().CreateVssBackupComponents();
            components.InitializeForBackup(null);
            // Nonpersistent, auto-release views. ARK save coordination happens in the manager before this request.
            components.SetContext(VssSnapshotContext.FileShareBackup);
            components.SetBackupState(false, false, VssBackupType.Copy, false);
            var setId = components.StartSnapshotSet();
            var snapshots = volumes.Values.Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(volume => volume, volume => components.AddToSnapshotSet(volume), StringComparer.OrdinalIgnoreCase);
            // FileShareBackup excludes writers. PrepareForBackup/BackupComplete are invalid in this context.
            // SaveWorld acknowledgement is supplied by the manager; live ARK consistency needs validation.
            components.DoSnapshotSet();
            var propertiesByVolume = snapshots.ToDictionary(pair => pair.Key,
                pair => components.GetSnapshotProperties(pair.Value), StringComparer.OrdinalIgnoreCase);
            foreach (var pair in propertiesByVolume)
            {
                var properties = pair.Value;
                if (properties.SnapshotSetId != setId || properties.SnapshotId != snapshots[pair.Key]
                    || !string.Equals(properties.OriginalVolumeName, pair.Key, StringComparison.OrdinalIgnoreCase)
                    || (properties.SnapshotAttributes & (VssVolumeSnapshotAttributes.Persistent | VssVolumeSnapshotAttributes.NoAutoRelease)) != 0
                    || properties.CreationTimestamp == default)
                    throw new InvalidDataException("Unexpected snapshot ownership or persistence.");
            }
            var capturedAt = propertiesByVolume.Values.Select(properties =>
                new DateTimeOffset(properties.CreationTimestamp.ToUniversalTime())).Max();
            var views = volumes.Select(pair => new WindowsSnapshotVolume(pair.Key, snapshots[pair.Value],
                propertiesByVolume[pair.Value].SnapshotDeviceObject.TrimEnd('\\'))).ToList();
            Write(new WindowsSnapshotResponse(1, new(setId, capturedAt), views));
            // EOF also occurs on parent exit. The COM owner remains alive until all readers release the view.
            var release = input.ReadLine();
            if (pipeMode && release is not null && release != "release:" + setId.ToString("N"))
                throw new InvalidDataException("The release request belongs to another capture.");
            return release is null ? null : setId;
    }

    private static string GetVolumeName(string root)
    {
        var buffer = new StringBuilder(512);
        if (!GetVolumeNameForVolumeMountPoint(root, buffer, buffer.Capacity))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return buffer.ToString();
    }

    private static void Write<T>(T result)
    {
        responseWriter.WriteLine(JsonSerializer.Serialize(result));
        responseWriter.Flush();
    }

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string root, StringBuilder volumeName, int bufferLength);
}

