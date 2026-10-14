namespace ArkServerManager.Core;

/// <summary>Documented defaults and vanilla classes used by the settings controls.</summary>
public static class DocumentedSettings
{
    public sealed record Engram(string Name,string ClassName,int Level,int Points);
    public sealed record Creature(string Name,string ClassName,string? NameTag,bool Spawnable,bool Tameable);
    public static readonly IReadOnlyList<Creature> CreatureCatalog=LoadCreatures();
    private static IReadOnlyList<Creature> LoadCreatures()
    {
        using var stream=typeof(DocumentedSettings).Assembly.GetManifestResourceStream("ArkServerManager.Core.creatures-ase.json")!;
        return System.Text.Json.JsonSerializer.Deserialize<Creature[]>(stream)!.OrderBy(c=>c.Name,StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public static readonly IReadOnlyList<Engram> EngramCatalog=LoadEngrams();
    private static IReadOnlyList<Engram> LoadEngrams()
    {
        using var stream=typeof(DocumentedSettings).Assembly.GetManifestResourceStream("ArkServerManager.Core.engrams-ase.json")!;
        return System.Text.Json.JsonSerializer.Deserialize<Engram[]>(stream)!
            .OrderBy(e=>e.Level<=0?int.MaxValue:e.Level).ThenBy(e=>e.Name,StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public static string StatDefault(string prefix,int index)=>prefix switch
    {
        "PerLevelStatsMultiplier_DinoTamed" when index==0=>"0.2",
        "PerLevelStatsMultiplier_DinoTamed" when index==8=>"0.17",
        "PerLevelStatsMultiplier_DinoTamed_Add" when index is 0 or 8=>"0.14",
        "PerLevelStatsMultiplier_DinoTamed_Affinity" when index is 0 or 8=>"0.44",
        _=>"1.0"
    };
    public static readonly (string Name,string ClassName,int Level,int Points)[] VanillaEngrams=
    [ ("Campfire","EngramEntry_Campfire_C",2,3), ("Stone Hatchet","EngramEntry_StoneHatchet_C",2,3),
      ("Storage Box","EngramEntry_StorageBox_Small_C",4,6), ("Simple Bed","EngramEntry_SimpleBed_C",7,8) ];
    public static bool CreatureStat(int index)=>index is 0 or 1 or 2 or 3 or 4 or 7 or 8 or 9;
}
