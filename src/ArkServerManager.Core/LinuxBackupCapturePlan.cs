namespace ArkServerManager.Core;

public enum LinuxBackupCaptureKind { BtrfsSnapshot, VerifiedCopy }

public static class LinuxBackupCapturePlan
{
    public static LinuxBackupCaptureKind Select(IReadOnlyList<string> sources, string snapshotStorage, string copyStorage, IReadOnlyList<LinuxMount> mounts)
    {
        if (sources.Count is < 1 or > 129 || sources.Distinct(StringComparer.Ordinal).Count() != sources.Count)
            throw new ArgumentException("Choose distinct saved-state sources.");
        foreach (var source in sources)
        {
            foreach (var storage in new[] { snapshotStorage, copyStorage })
                if (LinuxMountTopology.Contains(source, storage) || LinuxMountTopology.Contains(storage, source))
                    throw new ArgumentException("Capture storage must be separate from saved state.");
            var mount = LinuxMountTopology.Resolve(source, mounts);
            if (mounts.Any(m => m.Id != mount.Id && LinuxMountTopology.Contains(source, m.Directory)))
                throw new NotSupportedException("Saved state includes nested mounted storage that requires a separate capture.");
        }
        if (sources.All(source => LinuxMountTopology.Resolve(source, mounts).FileSystem is "ext4" or "xfs"))
            return LinuxBackupCaptureKind.VerifiedCopy;
        LinuxMountTopology.RequireSingleBtrfsMount(sources, snapshotStorage, mounts);
        return LinuxBackupCaptureKind.BtrfsSnapshot;
    }
}
