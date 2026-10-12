using System.Text;
using System.Text.RegularExpressions;

namespace ArkServerManager.Core;

public sealed record ServerLogTailResult(Guid ServerId, IReadOnlyList<string> Lines, DateTimeOffset ReadAt);
public static partial class ServerLogTail
{
    public static async Task<ServerLogTailResult> ReadAsync(CatalogRepository repository, Guid serverId, CancellationToken ct = default)
    {
        var server = (await repository.ReadAsync(ct)).Servers.Single(s => s.Id == serverId);
        var configuration = await ServerConfigurationReader.ReadAsync(repository, new(serverId, "GameUserSettings.ini"), ct);
        var ini = new IniDocument(configuration.Text ?? throw new IOException("Load access configuration before reading logs."));
        var secrets = new[] { "ServerAdminPassword", "ServerPassword" }.SelectMany(key => ini.GetValues("ServerSettings", key))
            .Where(value => value.Length > 0).OrderByDescending(value => value.Length).ToArray();
        var lines = new List<string>();
        foreach (var item in new[] { ("Manager", Path.Combine(server.Directory, "logs", "console.log")),
            ("ARK", Path.Combine(server.Directory, "ShooterGame", "Saved", "Logs", "ShooterGame.log")) })
        {
            RejectLinks(item.Item2);
            if (!File.Exists(item.Item2)) continue;
            await using var file = new FileStream(item.Item2, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.Asynchronous);
            var length = file.Length;
            var header = new byte[(int)Math.Min(3, length)];
            await file.ReadExactlyAsync(header, ct);
            Encoding encoding = header.Length >= 2 && header[0] == 0xff && header[1] == 0xfe ? Encoding.Unicode
                : header.Length >= 2 && header[0] == 0xfe && header[1] == 0xff ? Encoding.BigEndianUnicode : Encoding.UTF8;
            var start = Math.Max(0, length - 65536);
            if (encoding != Encoding.UTF8 && start % 2 != 0) start++;
            file.Position = start; var bytes = new byte[(int)(length - start)];
            await file.ReadExactlyAsync(bytes, ct);
            var text = encoding.GetString(bytes).TrimStart('\ufeff');
            if (start > 0) text = text.IndexOf('\n') is var newline && newline >= 0 ? text[(newline + 1)..] : "";
            foreach (var line in text.Split('\n').TakeLast(250))
            {
                if (line.Length == 0) continue;
                var redacted = PasswordAssignment().Replace(line.TrimEnd('\r'), "$1[redacted]");
                foreach (var secret in secrets) redacted = redacted.Replace(secret, "[redacted]", StringComparison.Ordinal);
                lines.Add("[" + item.Item1 + "] " + redacted);
            }
        }
        var latest = (await repository.ReadAsync(ct)).Servers.Single(s => s.Id == serverId);
        var current = await ServerConfigurationReader.ReadAsync(repository, new(serverId, "GameUserSettings.ini"), ct);
        if (latest.Directory != server.Directory || latest.Edition != server.Edition || current.SourceSHA256 != configuration.SourceSHA256)
            throw new IOException("The server access configuration changed while reading logs. Refresh the view.");
        return new(serverId, lines, DateTimeOffset.UtcNow);
    }
    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked server log locations require review.");
    }
    [GeneratedRegex(@"((?:ServerAdminPassword|ServerPassword|AdminPassword)\s*=\s*)[^?\s]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex PasswordAssignment();
}
