using System.Security.Cryptography;
using System.Text;

namespace ArkServerManager.Core;

public sealed record SettingsValueEdit(string Section,string Key,List<string> Values);
public static class SettingsDraft
{
    public static string Apply(string original,IEnumerable<SettingsValueEdit> edits)
    {
        var ini=new IniDocument(original);
        foreach(var edit in edits)ini.SetValues(edit.Section,edit.Key,edit.Values);
        var result=ini.ToString();if(ConfigurationSyntax.Check(result).Count>0)throw new ArgumentException("Review the configuration syntax before saving.");return result;
    }
    public static ServerConfigurationFile ReadPreview(string root,Guid id,string file)
    {
        var path=PreviewPath(root,file);var bytes=File.Exists(path)?File.ReadAllBytes(path):null;
        return new(id,file,"Windows",bytes is null?null:Encoding.UTF8.GetString(bytes),[]){SourceSHA256=bytes is null?null:Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()};
    }
    public static async Task SavePreviewAsync(string root,ServerConfigurationFile expected,string text,CancellationToken ct)
    {
        var path=PreviewPath(root,expected.FileName);var current=ReadPreview(root,expected.ServerId,expected.FileName);
        if(current.SourceSHA256!=expected.SourceSHA256)throw new IOException("The dummy configuration changed. Reload before saving.");
        if(ConfigurationSyntax.Check(text).Count>0)throw new ArgumentException("Review the INI syntax before saving.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            await InitialServerConfiguration.WritePrivate(temporary,new UTF8Encoding(false).GetBytes(text),ct);
            if(ReadPreview(root,expected.ServerId,expected.FileName).SourceSHA256!=expected.SourceSHA256)throw new IOException("The dummy configuration changed while saving.");
            if(File.Exists(path))File.Replace(temporary,path,path+".bak");else File.Move(temporary,path);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    private static string PreviewPath(string root,string file)
    {
        if(file is not ("Game.ini" or "GameUserSettings.ini"))throw new ArgumentException("Choose a supported configuration file.");
        return Path.Combine(Path.GetFullPath(root),"app-settings","solo-settings-dummy",file);
    }
}
