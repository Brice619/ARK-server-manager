using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Globalization;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private const string GameSection = "/Script/ShooterGame.ShooterGameMode";
    private static SettingField UserNumber(string label,string key,string value="1.0") => new(label,"ServerSettings",key,value,"number");
    private static SettingField GameNumber(string label,string key,string value="1.0") => new(label,GameSection,key,value,"number",1);
    private static SettingField UserFlag(string label,string key,bool value=false,bool invert=false) => new(label,"ServerSettings",key,value?"True":"False",invert?"bool-inverted":"bool");
    private static readonly (string Name,int Index)[] StatNames = [("Health",0),("Stamina",1),("Oxygen",3),("Food",4),("Water",5),("Weight",7),("Melee damage",8),("Movement speed",9),("Fortitude",10),("Crafting skill",11)];

    private void AddApprovedSettingsCategory(Grid grid, Func<string,IEnumerable<SettingField>,Border> group, ServerDefinition server)
    {
        grid.Children.Clear();grid.RowDefinitions=new("Auto,12,Auto,12,Auto,12,Auto,12,Auto");
        void Add(string title,IEnumerable<SettingField> fields,int row=0,int column=0,bool full=false)
        {
            var card=group(title,fields);card.VerticalAlignment=VerticalAlignment.Top;Grid.SetRow(card,row);Grid.SetColumn(card,column);if(full)Grid.SetColumnSpan(card,3);grid.Children.Add(card);
        }
        IEnumerable<SettingField> Stats(string prefix)=>StatNames.Where(s=>prefix=="PerLevelStatsMultiplier_Player"||DocumentedSettings.CreatureStat(s.Index)).Select(s=>new SettingField(s.Name,GameSection,$"{prefix}[{s.Index}]",DocumentedSettings.StatDefault(prefix,s.Index),"stat",1));
        switch(settingsCategory)
        {
            case 1:
                grid.ColumnDefinitions=new("*,12,*");
                Add("Progression",[UserNumber("XP multiplier","XPMultiplier"),UserNumber("Taming speed","TamingSpeedMultiplier")]);
                Add("Harvesting",[UserNumber("Harvest amount","HarvestAmountMultiplier"),UserNumber("Harvest health","HarvestHealthMultiplier"),UserNumber("Resource respawn interval","ResourcesRespawnPeriodMultiplier")],column:2);
                Grid.SetRowSpan(grid.Children.Last(),3);
                Add("Survival",[UserNumber("Player food drain","PlayerCharacterFoodDrainMultiplier"),UserNumber("Player water drain","PlayerCharacterWaterDrainMultiplier"),UserNumber("Player stamina drain","PlayerCharacterStaminaDrainMultiplier"),UserNumber("Dino food drain","DinoCharacterFoodDrainMultiplier")],row:2);
                break;
            case 2:
                Add("Combat",[UserNumber("Player damage multiplier","PlayerDamageMultiplier"),UserNumber("Player resistance multiplier","PlayerResistanceMultiplier"),new("Friendly fire enabled",GameSection,"bDisableFriendlyFire","False","bool-inverted",1)]);
                Add("Survival",[UserNumber("Health recovery","PlayerCharacterHealthRecoveryMultiplier")],column:2);
                Add("Player stat multipliers",Stats("PerLevelStatsMultiplier_Player"),row:2,full:true);
                break;
            case 3:
                Add("Combat",[UserNumber("Dino damage","DinoDamageMultiplier"),UserNumber("Dino resistance","DinoResistanceMultiplier"),UserNumber("Tamed dino damage","TamedDinoDamageMultiplier")]);
                Add("Population",[UserNumber("Dino count","DinoCountMultiplier"),UserNumber("Maximum tamed dinos","MaxTamedDinos","5000"),UserFlag("Allow flyer carrying","AllowFlyerCarryPvE",IsReviewServer(server.Id)||designPreview)],column:2);
                Add("Tamed stat multipliers",Stats("PerLevelStatsMultiplier_DinoTamed"),row:2,full:true);
                Add("Tamed stat additions",Stats("PerLevelStatsMultiplier_DinoTamed_Add"),row:4,full:true);
                Add("Taming affinity multipliers",Stats("PerLevelStatsMultiplier_DinoTamed_Affinity"),row:6,full:true);
                var overrides=CreatureOverridesPanel(server);Grid.SetRow(overrides,8);Grid.SetColumnSpan(overrides,3);grid.Children.Add(overrides);
                break;
            case 4:
                Add("Mating",[GameNumber("Mating interval","MatingIntervalMultiplier"),GameNumber("Mating speed","MatingSpeedMultiplier")]);
                Add("Incubation",[GameNumber("Egg hatch speed","EggHatchSpeedMultiplier"),GameNumber("Egg laying interval","LayEggIntervalMultiplier")],column:2);
                Add("Babies",[GameNumber("Baby maturation speed","BabyMatureSpeedMultiplier"),GameNumber("Baby food consumption","BabyFoodConsumptionSpeedMultiplier")],row:2);
                Add("Imprinting",[GameNumber("Cuddle interval","BabyCuddleIntervalMultiplier"),GameNumber("Imprint amount","BabyImprintAmountMultiplier"),GameNumber("Imprint stat scale","BabyImprintingStatScaleMultiplier"),new("Allow anyone to imprint",GameSection,"bAllowAnyoneBabyImprintCuddle","False","bool",1)],row:2,column:2);
                break;
            case 5:
                Add("Building",[UserNumber("Structure damage","StructureDamageMultiplier"),UserNumber("Structure resistance","StructureResistanceMultiplier"),new("Structure collision",GameSection,"bDisableStructurePlacementCollision",IsReviewServer(server.Id)||designPreview?"True":"False","bool-inverted",1),UserNumber("Platform structure limit multiplier","PerPlatformMaxStructuresMultiplier")],full:true);
                Add("Decay",[UserFlag("PvE structure decay","DisableStructureDecayPvE",false,true),UserNumber("Structure decay period","PvEStructureDecayPeriodMultiplier"),new("Auto-destroy decayed structures","","AutoDestroyStructures","False","launch-bool")],row:2,full:true);
                Add("Limits",[new("Structures in range","ServerSettings","TheMaxStructuresInRange","10500","integer"),new("Maximum platform saddles / rafts","ServerSettings","MaxPlatformSaddleStructureLimit","75","integer")],row:4,full:true);
                break;
            case 6:
                Add("Time",[UserNumber("Day cycle speed","DayCycleSpeedScale"),UserNumber("Daytime speed","DayTimeSpeedScale"),UserNumber("Nighttime speed","NightTimeSpeedScale")]);
                Add("Resources",[UserNumber("Resource respawn interval","ResourcesRespawnPeriodMultiplier"),UserNumber("Structure resource radius (additional multiplier)","StructurePreventResourceRadiusMultiplier"),GameNumber("Player resource radius","ResourceNoReplenishRadiusPlayers"),GameNumber("Structure resource radius","ResourceNoReplenishRadiusStructures")],column:2);
                break;
            case 7:
                var engrams=EngramOverridesPanel(server);Grid.SetColumnSpan(engrams,3);grid.Children.Add(engrams);break;
            case 8:
                Add("Ports",[new("Game port","SessionSettings","Port",server.Settings.GamePort.ToString(),"integer"),new("Query port","SessionSettings","QueryPort",server.Settings.QueryPort.ToString(),"integer"),new("Peer port","SessionSettings","PeerPort",(server.Settings.GamePort+1).ToString(),"readonly"),new("RCON port","ServerSettings","RCONPort",server.Settings.RconPort.ToString(),"integer")]);
                Add("RCON",[UserFlag("Enabled","RCONEnabled",IsReviewServer(server.Id)||designPreview)],row:2);
                var network=NetworkSettingsPanel(server);Grid.SetColumn(network,2);Grid.SetRowSpan(network,3);grid.Children.Add(network);break;
            case 9:
                var advanced=AdvancedSettingsPanel(server);Grid.SetColumnSpan(advanced,3);grid.Children.Add(advanced);break;
        }
    }
}
