using System.Text;

namespace ArkServerManager.Core;

/// <summary>Reads only bytes appended during this invocation to SteamCMD's Windows console log.</summary>
public sealed class SteamNativeConsoleReader
{
    private readonly string path;
    private long offset, total;
    private string fragment = "";
    private readonly Queue<ProcessLine> tail = [];
    private long sequence;
    private readonly Decoder decoder = Encoding.UTF8.GetDecoder();
    public SteamNativeConsoleReader(string executable)
    {
        path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executable))!, "logs", "console_log.txt");
        RejectLinks();
        offset = File.Exists(path) ? new FileInfo(path).Length : 0;
    }
    public IReadOnlyList<ProcessLine> Tail => tail.ToArray();
    public bool Confirmed(uint appId) => tail.Any(line => IsSuccess(line.Text, appId));
    public static bool IsSuccess(string line, uint appId) => line.Contains($"Success! App '{appId}' fully installed.", StringComparison.Ordinal)
        || line.Contains($"Success! App '{appId}' already up to date.", StringComparison.Ordinal);

    public async Task ReadAsync(CancellationToken ct)
    {
        RejectLinks();
        if (!File.Exists(path)) return;
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (input.Length < offset) throw new IOException("SteamCMD console log changed unexpectedly during installation.");
        input.Position = offset;
        var buffer = new byte[16384];
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            total += count;
            if (total > 16 * 1024 * 1024) throw new InvalidDataException("SteamCMD console output exceeds its supported size.");
            offset += count;
            var characters = new char[Encoding.UTF8.GetMaxCharCount(count)];
            fragment += new string(characters, 0, decoder.GetChars(buffer, 0, count, characters, 0, flush: false));
            int newline;
            while ((newline = fragment.IndexOf('\n')) >= 0)
            {
                tail.Enqueue(new(DateTimeOffset.UtcNow, false, fragment[..newline].TrimEnd('\r'), ++sequence));
                fragment = fragment[(newline + 1)..];
                while (tail.Count > 200) tail.Dequeue();
            }
            if (fragment.Length > 65536) throw new InvalidDataException("SteamCMD console line exceeds its supported size.");
        }
    }
    private void RejectLinks()
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked SteamCMD logs require separate validation.");
    }
}
