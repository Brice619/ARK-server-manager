using System.Text.Json;

Console.WriteLine(JsonSerializer.Serialize(args));
Console.Error.WriteLine("fixture error");
if (args is ["--environment", var variable])
{
    Console.WriteLine("environment=" + (Environment.GetEnvironmentVariable(variable) ?? "<missing>"));
    return 0;
}
if (args is ["--update-result", var updateResult])
{
    var startupFixture = Path.Combine(AppContext.BaseDirectory, "update-startup-fixture.txt");
    if (File.Exists(startupFixture) && await File.ReadAllTextAsync(startupFixture) == "fail") return 7;
    await File.WriteAllTextAsync(updateResult + ".opened", JsonSerializer.Serialize(args));
    return 0;
}
var nativeFixturePath = Path.Combine(AppContext.BaseDirectory, "native-server-fixture.json");
if (args.Any(a => a.Contains("?listen", StringComparison.Ordinal)) && File.Exists(nativeFixturePath))
{
    var settings = await LoadNativeSettings(nativeFixturePath);
    using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, settings.Port);
    listener.Start(); Console.WriteLine("native fixture ready");
    while (true)
    {
        using var peer = await listener.AcceptTcpClientAsync();
        await using var stream = peer.GetStream();
        var auth = await ReadFixturePacket(stream);
        var authenticated = auth.Text == settings.Password;
        await WriteFixturePacket(stream, authenticated ? auth.Id : -1, 2, "");
        if (!authenticated) continue;
        var command = await ReadFixturePacket(stream);
        if (command.Text == "FixtureCrash") return 7;
        if (command.Text == "SaveWorld")
            settings = await LoadNativeSettings(nativeFixturePath);
        var response = command.Text switch
        {
            "ListPlayers" => "No Players Connected",
            "SaveWorld" => settings.SaveResponse,
            "DoExit" => "Exit requested",
            _ => "Fixture response"
        };
        await WriteFixturePacket(stream, command.Id, 0, response);
        if (command.Text == "DoExit") return 0;
    }
}
if (args.Contains("--wait"))
{
    Console.WriteLine("ready");
    while (await Console.In.ReadLineAsync() is { } line)
    {
        if (line == "stop") return 0;
        if (line == "crash") return 7;
        Console.WriteLine(line);
    }
}
return args.Contains("--fail") ? 7 : 0;

static async Task<(int Id, string Text)> ReadFixturePacket(Stream stream)
{
    var prefix = new byte[4]; await stream.ReadExactlyAsync(prefix);
    var size = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(prefix);
    if (size is < 10 or > 4096) throw new InvalidDataException("Invalid fixture packet.");
    var bytes = new byte[size]; await stream.ReadExactlyAsync(bytes);
    return (System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes), System.Text.Encoding.UTF8.GetString(bytes, 8, size - 10));
}
static async Task WriteFixturePacket(Stream stream, int id, int type, string text)
{
    var content = System.Text.Encoding.UTF8.GetBytes(text);
    var bytes = new byte[content.Length + 14];
    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, content.Length + 10);
    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), id);
    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), type);
    content.CopyTo(bytes, 12); await stream.WriteAsync(bytes);
}
static async Task<NativeFixtureSettings> LoadNativeSettings(string path)
{
    var settings = JsonSerializer.Deserialize<NativeFixtureSettings>(await File.ReadAllTextAsync(path))!;
    if (!settings.ReadAdministratorFromConfiguration) return settings;
    var shooterGame = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
    var config = Path.Combine(shooterGame, "Saved", "Config", OperatingSystem.IsWindows() ? "WindowsServer" : "LinuxServer", "GameUserSettings.ini");
    var inServerSettings = false;
    foreach (var line in await File.ReadAllLinesAsync(config))
    {
        var text = line.Trim();
        if (text.StartsWith('[')) inServerSettings = text.Equals("[ServerSettings]", StringComparison.OrdinalIgnoreCase);
        else if (inServerSettings && text.StartsWith("ServerAdminPassword=", StringComparison.OrdinalIgnoreCase))
            return settings with { Password = text["ServerAdminPassword=".Length..] };
    }
    throw new InvalidDataException("Fixture administrator password is missing.");
}
sealed record NativeFixtureSettings(int Port, string Password, string SaveResponse, bool ReadAdministratorFromConfiguration = false);
