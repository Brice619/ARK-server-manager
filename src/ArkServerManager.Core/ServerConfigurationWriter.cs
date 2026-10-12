using System.Text;
using System.Security.Cryptography;

namespace ArkServerManager.Core;

public sealed record ServerConfigurationEdit(Guid ServerId, string FileName, string SourceSHA256, string Text)
{
    public override string ToString() => $"Configuration edit for {ServerId}, {FileName}; contents omitted";
}

/// <summary>The caller must hold the server lifecycle lock and verify it is stopped inside the supplied scope.</summary>
public static class ServerConfigurationWriter
{
    public static Task CreateGameAsync(CatalogRepository repository, Guid serverId, string text,
        Func<ServerDefinition, Func<CancellationToken, Task>, CancellationToken, Task> withStoppedServer,
        CancellationToken ct = default) => CreateAsync(repository, serverId, "Game.ini", text, withStoppedServer, ct);

    public static async Task CreateAsync(CatalogRepository repository, Guid serverId, string fileName, string text,
        Func<ServerDefinition, Func<CancellationToken, Task>, CancellationToken, Task> withStoppedServer,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(withStoppedServer);
        if (fileName is not ("Game.ini" or "GameUserSettings.ini")) throw new ArgumentException("Choose a supported configuration file.");
        if (text is null || Encoding.UTF8.GetByteCount(text) > 4 * 1024 * 1024 || text.Contains('\0'))
            throw new ArgumentException("Choose valid configuration text within the size limit.");
        var request = new ServerConfigurationReadRequest(serverId, fileName);
        var before = await ServerConfigurationReader.ReadAsync(repository, request, ct);
        if (before.Text is not null) throw new InvalidOperationException("The configuration file already exists. Reload it before saving.");
        var server = (await repository.ReadAsync(ct)).Servers.Single(s => s.Id == serverId);
        var invoked = false;
        await withStoppedServer(server, async token =>
        {
            if (invoked) throw new InvalidOperationException("The configuration creation scope cannot run twice.");
            invoked = true;
            var current = await ServerConfigurationReader.ReadAsync(repository, request, token);
            var latest = (await repository.ReadAsync(token)).Servers.Single(s => s.Id == serverId);
            if (latest.Directory != server.Directory || latest.Edition != server.Edition || current.Platform != before.Platform || current.Text is not null)
                throw new InvalidOperationException("The installation or configuration changed. Reload before saving.");
            var path = Path.Combine(server.Directory, "ShooterGame", "Saved", "Config", current.Platform + "Server", fileName);
            if (!Directory.Exists(Path.GetDirectoryName(path))) throw new InvalidOperationException("Prepare the server configuration folder first.");
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await InitialServerConfiguration.WritePrivate(temporary, new UTF8Encoding(false, true).GetBytes(text), token);
                var final = await ServerConfigurationReader.ReadAsync(repository, request, token);
                if (final.Text is not null || final.Platform != current.Platform)
                    throw new InvalidOperationException("The configuration file appeared while saving. Reload before saving.");
                token.ThrowIfCancellationRequested();
                File.Move(temporary, path, overwrite: false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }, ct);
        if (!invoked) throw new InvalidOperationException("The stopped-server scope did not authorize creation.");
    }

    public static async Task WriteAsync(CatalogRepository repository, ServerConfigurationEdit edit,
        Func<ServerDefinition, Func<CancellationToken, Task>, CancellationToken, Task> withStoppedServer,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(withStoppedServer);
        if (edit.Text is null || edit.SourceSHA256 is null || edit.SourceSHA256.Length != 64
            || edit.SourceSHA256.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("A loaded configuration snapshot is required.");
        var request = new ServerConfigurationReadRequest(edit.ServerId, edit.FileName);
        var before = await ServerConfigurationReader.ReadAsync(repository, request, ct);
        RequireUnchanged(before.SourceSHA256);
        var server = (await repository.ReadAsync(ct)).Servers.Single(s => s.Id == edit.ServerId);
        var invoked = false;
        await withStoppedServer(server, async token =>
        {
            if (invoked) throw new InvalidOperationException("The configuration write scope cannot run twice.");
            invoked = true;
            var current = await ServerConfigurationReader.ReadAsync(repository, request, token);
            RequireUnchanged(current.SourceSHA256);
            var latestServer = (await repository.ReadAsync(token)).Servers.Single(s => s.Id == edit.ServerId);
            if (latestServer.Directory != server.Directory || latestServer.Edition != server.Edition)
                throw new InvalidOperationException("The selected installation changed. Reload its configuration.");
            var path = Path.Combine(server.Directory, "ShooterGame", "Saved", "Config", current.Platform + "Server", edit.FileName);
            byte[] original;
            await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                8192, FileOptions.Asynchronous))
            {
                if (input.Length > 4 * 1024 * 1024) throw new InvalidDataException("The configuration exceeds the size limit.");
                original = new byte[checked((int)input.Length)];
                await input.ReadExactlyAsync(original, token);
            }
            RequireUnchanged(Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant());
            var encoding = OriginalEncoding(original);
            var preamble = encoding.GetPreamble();
            var content = encoding.GetBytes(edit.Text);
            if (content.Length + preamble.Length > 4 * 1024 * 1024)
                throw new InvalidDataException("The edited configuration exceeds the size limit.");
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var originalAccess = OperatingSystem.IsWindows()
                ? new FileInfo(path).GetAccessControl(System.Security.AccessControl.AccessControlSections.Access) : null;
            var originalMode = OperatingSystem.IsLinux() ? File.GetUnixFileMode(path) : (UnixFileMode?)null;
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                    Share = FileShare.None, BufferSize = 8192, Options = FileOptions.Asynchronous | FileOptions.WriteThrough };
                if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using (var output = OperatingSystem.IsWindows()
                    ? new FileInfo(temporary).Create(FileMode.CreateNew, System.Security.AccessControl.FileSystemRights.FullControl,
                        FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.WriteThrough, originalAccess!)
                    : new FileStream(temporary, options))
                {
                    await output.WriteAsync(preamble, token);
                    await output.WriteAsync(content, token);
                    await output.FlushAsync(token);
                    output.Flush(true);
                }
                // Re-read and revalidate links immediately before publishing. External editors must still cooperate.
                var final = await ServerConfigurationReader.ReadAsync(repository, request, token);
                RequireUnchanged(final.SourceSHA256);
                token.ThrowIfCancellationRequested();
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temporary, originalMode!.Value);
                File.Replace(temporary, path, null, ignoreMetadataErrors: false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }, ct);
        if (!invoked) throw new InvalidOperationException("The stopped-server scope did not authorize a write.");

        void RequireUnchanged(string? hash)
        {
            if (!string.Equals(hash, edit.SourceSHA256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The configuration changed or disappeared. Reload it before saving.");
        }
    }

    private static Encoding OriginalEncoding(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) return new UTF32Encoding(false, true, true);
        if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) return new UTF32Encoding(true, true, true);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) return new UnicodeEncoding(false, true, true);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) return new UnicodeEncoding(true, true, true);
        return new UTF8Encoding(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }), true);
    }
}
