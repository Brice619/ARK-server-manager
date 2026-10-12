using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private readonly Dictionary<Guid, Guid> clusterToolMaps = new();

    private void RenderClusterMapTool(ClusterDefinition cluster, ServerDefinition[] members)
    {
        if (members.Length == 0)
        {
            var empty = new StackPanel { Spacing = 12 };
            empty.Children.Add(Label("No maps in this cluster", 24, true));
            empty.Children.Add(Label("Map tools become available after a server is added.", 16));
            content.Children.Add(Surface(empty)); return;
        }
        var index = clusterToolMaps.TryGetValue(cluster.Id, out var id) ? Array.FindIndex(members, m => m.Id == id) : -1;
        if (index < 0) index = 0;
        var member = members[index]; clusterToolMaps[cluster.Id] = member.Id;
        var selection = new WrapPanel { Margin = new Thickness(0, 8, 0, 20) };
        var caption = Label("Map", 16); caption.VerticalAlignment = VerticalAlignment.Center;
        caption.Margin = new Thickness(0, 0, 18, 10); selection.Children.Add(caption);
        var picker = new ComboBox
        {
            ItemsSource = members.Select(m => m.Settings.Name.Equals(MapName(m.Map), StringComparison.OrdinalIgnoreCase)
                ? m.Settings.Name : m.Settings.Name + " · " + MapName(m.Map)).ToArray(),
            SelectedIndex = index, Width = 420, FontSize = 16, Margin = new Thickness(0, 0, 24, 10)
        };
        Avalonia.Automation.AutomationProperties.SetName(picker, "Map to manage");
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedIndex < 0 || picker.SelectedIndex >= members.Length) return;
            var chosen = members[picker.SelectedIndex];
            if (clusterToolMaps[cluster.Id] == chosen.Id) return;
            clusterToolMaps[cluster.Id] = chosen.Id; Render();
        };
        selection.Children.Add(picker);
        var scope = Label("Applies to the selected map", 15); scope.Foreground = Muted;
        scope.VerticalAlignment = VerticalAlignment.Center; scope.Margin = new Thickness(0, 0, 0, 10);
        selection.Children.Add(scope); content.Children.Add(selection);
        switch (managementTab)
        {
            case 2: RenderServerMods(member); break;
            case 4: RenderPlayers(member); break;
            case 5: RenderConsole(member); break;
        }
    }

    private bool IsModsViewVisible => managementTab == 2 && !settingsPage && !installationsPage && !activityPage && !creationPage
        && (selectedServer is not null || selectedCluster is not null);
}
