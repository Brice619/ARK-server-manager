using System.Text;

namespace ArkServerManager.Core;

public sealed record ServerLaunchPreferences(string ExtraArguments="",bool UseAllAvailableCores=false,bool ServerLogging=false,bool AutoDestroyStructures=false)
{
    public IReadOnlyList<string> Tokens()
    {
        if(ExtraArguments is null||ExtraArguments.Length>4096||ExtraArguments.Any(char.IsControl))throw new ArgumentException("Use a single line of launch arguments.");
        var result=new List<string>();var token=new StringBuilder();var quoted=false;
        foreach(var ch in ExtraArguments){if(ch=='"'){quoted=!quoted;continue;}if(char.IsWhiteSpace(ch)&&!quoted){if(token.Length>0){result.Add(token.ToString());token.Clear();}}else token.Append(ch);}
        if(quoted)throw new ArgumentException("Close the quoted launch argument.");if(token.Length>0)result.Add(token.ToString());
        foreach(var arg in result)
        {
            if(arg.Length<2||arg[0] is not ('-' or '?'))throw new ArgumentException("Extra arguments must start with - or ?.");
            foreach(var option in arg.Split('?').Where(s=>s.Length>0))
            {
                var key=option.TrimStart('-').Split('=')[0];
                if(new[]{"Port","QueryPort","RCONPort","MaxPlayers","WinLiveMaxPlayers","SessionName","clusterid","ClusterDirOverride","mods","automanagedmods","server","log","USEALLAVAILABLECORES","servergamelog","AutoDestroyStructures"}.Contains(key,StringComparer.OrdinalIgnoreCase))
                    throw new ArgumentException("Change the managed server option using its settings field.");
            }
        }
        return result;
    }
    public void Apply(List<string> arguments)
    {
        // Retain the legacy preference for saved-document compatibility, but the
        // editor-only USEALLAVAILABLECORES flag has no dedicated-server effect.
        if(ServerLogging)arguments.Add("-servergamelog");if(AutoDestroyStructures)arguments.Add("-AutoDestroyStructures");
        foreach(var token in Tokens())if(token.StartsWith('?')){var map=arguments.FindIndex(a=>!a.StartsWith('-')&&a.Contains("?listen",StringComparison.OrdinalIgnoreCase));if(map<0)throw new InvalidOperationException("The native map argument is unavailable.");arguments[map]+=token;}else arguments.Add(token);
    }
}
public sealed record ServerLaunchPreferencesState(Guid ServerId,ServerLaunchPreferences Preferences,ServerNetworkScope Scope);
public sealed record ServerLaunchPreferencesEdit(ServerLaunchPreferencesState Expected,ServerLaunchPreferences Desired,ServerNetworkScope Scope);
public static class ServerLaunchPreferencesStore
{
    public static async Task<ServerLaunchPreferencesState> ReadAsync(CatalogRepository repository,Guid id,CancellationToken ct)
    {var server=(await repository.ReadAsync(ct)).Servers.Single(s=>s.Id==id);return new(id,server.LaunchPreferences,server.NetworkScope);}
    public static async Task<ServerLaunchPreferencesState> SaveAsync(CatalogRepository repository,ServerLaunchPreferencesEdit edit,CancellationToken ct)
    {edit.Desired.Tokens();await repository.ChangeAsync(c=>c.UpdateLaunchPreferences(edit),ct);return await ReadAsync(repository,edit.Expected.ServerId,ct);}
}
