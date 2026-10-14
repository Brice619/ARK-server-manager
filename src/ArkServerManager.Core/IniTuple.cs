namespace ArkServerManager.Core;

/// <summary>Preserves unrecognized members when editing repeated ARK configuration objects.</summary>
public sealed class IniTuple
{
    private readonly List<KeyValuePair<string,string>> members=[];
    public IniTuple(string text)
    {
        text=text.Trim();if(!text.StartsWith('(')||!text.EndsWith(')'))throw new ArgumentException("Enter an ARK configuration object.");
        var start=1;var depth=0;var quoted=false;
        for(var i=1;i<text.Length;i++)
        {
            if(text[i]=='"'&&(i==0||text[i-1]!='\\'))quoted=!quoted;
            if(!quoted){if(text[i]=='(')depth++;if(text[i]==')')depth--;}
            if(!quoted&&(text[i]==','&&depth==0||i==text.Length-1))
            {
                var part=text[start..i].Trim();var split=part.IndexOf('=');if(split<=0)throw new ArgumentException("Invalid ARK object member.");
                members.Add(new(part[..split].Trim(),part[(split+1)..].Trim()));start=i+1;
            }
        }
        if(quoted)throw new ArgumentException("Unclosed quoted ARK value.");
    }
    public string? Get(string key)=>members.LastOrDefault(m=>m.Key.Equals(key,StringComparison.OrdinalIgnoreCase)).Value?.Trim('"');
    public void Set(string key,string value,bool quoted=false)
    {
        if(string.IsNullOrWhiteSpace(key)||key.IndexOfAny(['=','(',')',',','\n','\r'])>=0||value.Any(char.IsControl)||value.Contains('"'))throw new ArgumentException("Use a single configuration value.");
        if(!quoted&&value.IndexOfAny([',','(',')'])>=0)throw new ArgumentException("Use a scalar configuration value.");
        var index=members.FindIndex(m=>m.Key.Equals(key,StringComparison.OrdinalIgnoreCase));var entry=new KeyValuePair<string,string>(key,quoted?$"\"{value}\"":value);
        if(index<0)members.Add(entry);else members[index]=entry;
    }
    public override string ToString()=>"("+string.Join(',',members.Select(m=>m.Key+"="+m.Value))+")";
}
