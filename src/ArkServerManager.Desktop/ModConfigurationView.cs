using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private bool modConfigurationText;
    private bool modConfigurationFilesReview;
    private readonly Dictionary<(Guid Server, string Mod), string> modConfigurationPreviewDrafts = new();
    private readonly Dictionary<(Guid Server, string Mod), string> modConfigurationPreviewSaved = new();
    private bool modConfigurationSaving;
    private const string ModPreviewInitialText = "; Design preview only — no mod settings or file locations have been verified.\n";

    private void RenderModConfiguration(ServerDefinition server, string modId)
    {
        content.Children.Add(ActionButton("← Back to mods", () => { configuredMod = null; Render(); }));
        content.Children.Add(Label("Mod configuration", 32, true));
        var identity = new StackPanel { Spacing = 10, Margin = new Thickness(0, 12, 0, 12) };
        identity.Children.Add(Label("Mod ID: " + modId, 23, true));
        var scope = Label(server.Settings.Name + " · " + server.Edition + " · " + MapName(server.Map), 16);
        scope.Foreground = Muted; identity.Children.Add(scope); content.Children.Add(identity);
        if (!designPreview || modConfigurationFilesReview)
        {
            RenderModConfigurationFiles(server, modId); return;
        }
        var tabs = new WrapPanel { Margin = new Thickness(0, 12, 0, 14) };
        tabs.Children.Add(ActionButton("Guided settings", () => { modConfigurationText = false; Render(); }, !modConfigurationText));
        tabs.Children.Add(ActionButton("Configuration text", () => { modConfigurationText = true; Render(); }, modConfigurationText));
        content.Children.Add(tabs);
        if (!modConfigurationText)
        {
            var body = new StackPanel { Spacing = 16 };
            body.Children.Add(Label("Guided settings have not been verified for this mod.", 21, true));
            var explanation = Label("Supported settings and their configuration locations must be checked against the mod's documentation.", 16);
            explanation.Foreground = Muted; body.Children.Add(explanation);
            body.Children.Add(ActionButton("View configuration text", () => { modConfigurationText = true; Render(); }));
            content.Children.Add(Surface(body)); return;
        }
        var key = (server.Id, modId);
        var editor = new TextBox { AcceptsReturn = true, AcceptsTab = true, IsReadOnly = !designPreview,
            Text = designPreview ? modConfigurationPreviewDrafts.GetValueOrDefault(key,
                modConfigurationPreviewSaved.GetValueOrDefault(key, ModPreviewInitialText)) : "",
            PlaceholderText = "Configuration has not been loaded", Height = 280, FontSize = 16,
            FontFamily = new FontFamily("Consolas"), Padding = new Thickness(16), TextWrapping = TextWrapping.NoWrap };
        Avalonia.Automation.AutomationProperties.SetName(editor, "Mod configuration text");
        editor.TextChanged += (_, _) => { if (designPreview) modConfigurationPreviewDrafts[key] = editor.Text ?? ""; };
        content.Children.Add(Surface(editor, new Thickness(14)));
        var actions = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        var revert = ActionButton("Revert", designPreview ? () => { modConfigurationPreviewDrafts.Remove(key); Render(); } : null);
        revert.Margin = new Thickness(0, 0, 14, 0); actions.Children.Add(revert);
        actions.Children.Add(ActionButton("Save Changes", designPreview && !modConfigurationSaving
            ? () => { _ = ConfirmModConfigurationPreviewAsync(server, modId, editor.Text ?? ""); } : null, primary: true));
        content.Children.Add(actions);
        var note = Label(designPreview ? "Preview edits stay in this session. No server or mod files are changed."
            : "Configuration loading, validation and save confirmation are being connected.", 14);
        note.Foreground = Muted; content.Children.Add(note);
    }

    private async Task ConfirmModConfigurationPreviewAsync(ServerDefinition server, string modId, string draft)
    {
        if (!designPreview || modConfigurationSaving || lifetime.IsCancellationRequested) return;
        modConfigurationSaving = true;
        try
        {
            var choice = await ShowPreviewSaveConfirmationAsync(server, "Mod ID " + modId);
            if (choice == SaveChoice.Cancel || lifetime.IsCancellationRequested) return;
            var key = (server.Id, modId);
            modConfigurationPreviewSaved[key] = draft;
            modConfigurationPreviewDrafts.Remove(key);
        }
        catch (InvalidOperationException)
        { connection.Text = "The save confirmation could not be shown. Your draft is still available."; }
        finally
        {
            modConfigurationSaving = false;
            if (!lifetime.IsCancellationRequested && configuredMod == modId && modPageServer == server.Id && IsModsViewVisible) Render();
        }
    }

    private void RenderModConfigurationFiles(ServerDefinition server, string modId)
    {
            var files = new StackPanel { Spacing = 18 };
            files.Children.Add(Label("Configuration files", 23, true));
            var guidance = Label("Check the mod's documentation for its section names, options and configuration file. Choose that file to load your server's current configuration and edit it.", 16);
            guidance.TextWrapping = TextWrapping.Wrap; files.Children.Add(guidance);
            foreach (var file in new[] { "GameUserSettings.ini", "Game.ini" })
            {
                var fileIndex = file == "Game.ini" ? 1 : 0;
                files.Children.Add(ActionButton("Edit " + file, client is null && !designPreview ? null : () =>
                {
                    advancedReturnMod = modId; advancedEditorServer = server.Id; advancedEditorFile = fileIndex;
                    managementTab = 1; Render();
                }));
            }
            var saveHelp = Label("The editor changes the selected server file. When saving, choose the next start or a restart of this map. Other maps keep running.", 15);
            saveHelp.TextWrapping = TextWrapping.Wrap; saveHelp.Foreground = Muted; files.Children.Add(saveHelp);
        content.Children.Add(Surface(files));
    }

    public static MainWindow CreateModConfigurationFilesDesignReview()
    {
        var window = new MainWindow(["--design-preview"]);
        var server = window.catalog!.Servers[0];
        window.selectedServer = server.Id; window.managementTab = 2; window.modPageServer = server.Id; window.configuredMod = "928708";
        window.modConfigurationFilesReview = true; window.Render();
        return window;
    }
}
