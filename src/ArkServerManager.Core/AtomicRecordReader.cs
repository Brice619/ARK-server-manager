namespace ArkServerManager.Core;

internal static class AtomicRecordReader
{
    public static async Task<FileStream> OpenAsync(string path, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
            catch (IOException error) when (OperatingSystem.IsWindows()
                && (attempt < 10 && (error.HResult & 0xffff) is 32 or 33 || attempt < 3 && error is FileNotFoundException))
            {
                // ReplaceFile briefly owns an exclusive native handle. Retry only sharing/lock violations.
                await Task.Delay(10, ct);
            }
        }
    }
}
