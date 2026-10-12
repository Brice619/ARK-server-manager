namespace ArkServerManager.Core;
public sealed record ServerMapChoice(string Identifier, string Name);
public static class ServerMapCatalog
{
    private static readonly ServerMapChoice[] Ase = [
        new("TheIsland", "The Island"), new("TheCenter", "The Center"), new("ScorchedEarth_P", "Scorched Earth"),
        new("Ragnarok", "Ragnarok"), new("Aberration_P", "Aberration"), new("Extinction", "Extinction"),
        new("Valguero_P", "Valguero"), new("Genesis", "Genesis"), new("CrystalIsles", "Crystal Isles"),
        new("Gen2", "Genesis 2"), new("LostIsland", "Lost Island"), new("Fjordur", "Fjordur")];
    private static readonly ServerMapChoice[] Asa = [
        new("TheIsland_WP", "The Island"), new("ScorchedEarth_WP", "Scorched Earth"), new("TheCenter_WP", "The Center"),
        new("Aberration_WP", "Aberration"), new("Extinction_WP", "Extinction"), new("Astraeos_WP", "Astraeos"),
        new("Ragnarok_WP", "Ragnarok"), new("Valguero_WP", "Valguero"), new("LostColony_WP", "Lost Colony"),
        new("Genesis_WP", "Genesis")];
    public static IReadOnlyList<ServerMapChoice> ForEdition(ArkEdition edition) => edition switch
    {
        ArkEdition.ASE => Ase.ToArray(), ArkEdition.ASA => Asa.ToArray(), _ => throw new ArgumentException("Choose an ARK edition.")
    };
    public static string DisplayName(string identifier) => Ase.Concat(Asa).FirstOrDefault(map => map.Identifier.Equals(identifier, StringComparison.OrdinalIgnoreCase))?.Name
        ?? (identifier == "ScorchedEarth" ? "Scorched Earth" : identifier == "Genesis2" ? "Genesis 2" : identifier);
}
