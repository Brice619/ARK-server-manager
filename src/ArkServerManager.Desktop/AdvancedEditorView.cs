using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private Guid? advancedEditorServer;
    private string? advancedReturnMod;
    private int advancedEditorFile;
    private readonly Dictionary<(Guid Server, int File), string> advancedPreviewDrafts = new();
    private readonly Dictionary<(Guid Server, int File), string> advancedPreviewSaved = new();
    private bool advancedPreviewSaving;
    private readonly Dictionary<(Guid Server, int File), ServerConfigurationFile> advancedLoadedFiles = new();
    private bool advancedReading;
    private string advancedReadNotice = "";
    private readonly Dictionary<(Guid Server, int File), string> advancedValidation = new();
    private (Guid Server, int File)? advancedReadNoticeKey;
    private readonly Dictionary<Guid, List<PendingConfigurationStatus>> advancedQueuedEdits = new();

    private static string AdvancedSample(int file) => file == 0
        ? "; Illustrative configuration — not loaded from a server\n[ServerSettings]\nServerPassword=\nServerAdminPassword=\n\n[SessionSettings]\nSessionName=Preview server\n"
        : "; Illustrative configuration — not loaded from a server\n[/Script/ShooterGame.ShooterGameMode]\n";

    private void RenderAdvancedEditor(ServerDefinition server)
    {
        var compact = Height < 760;
        content.Children.Add(ActionButton(advancedReturnMod is null ? "← Back to Settings" : "← Back to mod configuration", () =>
        {
            advancedEditorServer = null;
            if (advancedReturnMod is { } mod)
            { managementTab = 2; modPageServer = server.Id; configuredMod = mod; advancedReturnMod = null; }
            Render();
        }));
        var identity = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20,
            Margin = new Thickness(0, 14, 0, 12) };
        if (!compact) identity.Children.Add(MapPicture(server.Map, 138, 98));
        var details = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        details.Children.Add(Label(server.Settings.Name, 25, true));
        var edition = Label(server.Edition + " · " + MapName(server.Map), 16); edition.Foreground = Muted;
        details.Children.Add(edition); identity.Children.Add(details);
        if (!compact) content.Children.Add(identity);
        else
        {
            content.Children.Add(Label("Advanced Editor", 28, true));
            content.Children.Add(Label(server.Settings.Name + " · " + server.Edition + " · " + MapName(server.Map), 16));
        }
        if (!compact) content.Children.Add(Label("Advanced Editor", 32, true));
        var explanation = Label(compact ? designPreview ? "Preview edits stay in this session." : "Choose when to apply changes when saving." : designPreview
            ? "Preview edits stay in this session. Server configuration files are not changed."
            : "Edit an existing configuration file, then choose next start or restart when saving.", 16);
        explanation.Foreground = Muted; content.Children.Add(explanation);
        if (advancedPendingEdit is { } pending && pending.ServerId != server.Id)
        {
            content.Children.Add(Label("An earlier configuration save needs reconnection before another file can be saved.", 16));
            content.Children.Add(ActionButton("Return to pending save", () =>
            {
                selectedServer = pending.ServerId; advancedEditorServer = pending.ServerId;
                advancedEditorFile = pending.FileName == "Game.ini" ? 1 : 0; Render();
            }));
        }

        var files = new WrapPanel { Margin = new Thickness(0, compact ? 4 : 20, 0, 0) };
        var names = new[] { "GameUserSettings.ini", "Game.ini" };
        for (var i = 0; i < names.Length; i++)
        {
            var index = i;
            files.Children.Add(ActionButton(names[i], advancedSaveBusy || advancedPendingEdit is not null ? null
                : () => { advancedEditorFile = index; Render(); }, index == advancedEditorFile));
        }
        content.Children.Add(files);
        var key = (server.Id, advancedEditorFile);
        if (!designPreview)
            content.Children.Add(ActionButton(advancedReading ? "Reading configuration…" : "Read configuration file",
                client is null || advancedReading || advancedSaveBusy || advancedPendingEdit is not null ? null : () => { _ = ReadAdvancedConfigurationAsync(server, key.Item2, names[key.Item2]); }));
        var queued = advancedQueuedEdits.GetValueOrDefault(server.Id)?.SingleOrDefault(p => p.FileName == names[key.Item2]);
        if (queued is not null)
        {
            var saved = new StackPanel { Spacing = 12 };
            saved.Children.Add(Label(queued.Started ? "Saved configuration needs recovery review." : "This file has a saved change for the next start.", 17));
            saved.Children.Add(ActionButton("View saved edit…", advancedSaveBusy || advancedReading ? null : () => _ = ViewAdvancedPendingAsync(queued)));
            saved.Children.Add(ActionButton("Resolve saved edit…", advancedSaveBusy || advancedReading ? null : () => _ = ResolveAdvancedPendingAsync(queued)));
            if (!queued.Started) saved.Children.Add(ActionButton("Cancel saved edit…", advancedSaveBusy ? null : () => _ = CancelAdvancedEditAsync(queued)));
            content.Children.Add(Surface(saved));
        }
        var editor = new TextBox
        {
            Text = designPreview ? advancedPreviewDrafts.GetValueOrDefault(key,
                advancedPreviewSaved.GetValueOrDefault(key, AdvancedSample(advancedEditorFile)))
                : advancedPreviewDrafts.GetValueOrDefault(key, advancedLoadedFiles.GetValueOrDefault(key)?.Text ?? ""),
            PlaceholderText = "Configuration unavailable", AcceptsReturn = true, AcceptsTab = true,
            IsReadOnly = !designPreview && (advancedLoadedFiles.GetValueOrDefault(key)?.SourceSHA256 is null || advancedSaveBusy || advancedPendingEdit is not null), FontFamily = new FontFamily("Consolas"), FontSize = 16,
            Height = 310, TextWrapping = TextWrapping.NoWrap,
            Background = Brush.Parse("#0F1921"), Padding = new Thickness(20),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        Avalonia.Automation.AutomationProperties.SetName(editor, names[advancedEditorFile] + " editor");
        editor.TextChanged += (_, _) =>
        { advancedPreviewDrafts[key] = editor.Text ?? ""; advancedValidation.Remove(key); };
        content.Children.Add(Surface(editor, new Thickness(14)));
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 16, 0, 0) };
        footer.Children.Add(ActionButton("Validate", () =>
        {
            try
            {
                var findings = ConfigurationSyntax.Check(editor.Text ?? "");
                advancedValidation[key] = findings.Count == 0 ? "No INI syntax issues found. Game and mod compatibility has not been checked."
                    : string.Join("\n", findings.Take(5).Select(f => $"Line {f.Line}: {f.Message}"))
                        + (findings.Count > 5 ? "\nMore issues remain; fix these and validate again." : "");
            }
            catch (ArgumentException) { advancedValidation[key] = "The file exceeds the supported size."; }
            Render();
        }));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        actions.Children.Add(ActionButton("Revert", !advancedSaveBusy && advancedPendingEdit is null ? () =>
        { advancedPreviewDrafts.Remove(key); advancedValidation.Remove(key); Render(); } : null));
        actions.Children.Add(ActionButton(advancedPendingEdit is not null ? "Reconnect to save" : "Save Changes", designPreview && !advancedPreviewSaving
            ? () => { _ = SaveAdvancedPreviewAsync(server, key.Item2, names[key.Item2], editor.Text ?? ""); }
            : !designPreview && client is not null && !advancedSaveBusy && queued is null && advancedLoadedFiles.GetValueOrDefault(key)?.SourceSHA256 is not null
                && (advancedPendingEdit is null || advancedPendingEdit.ServerId == server.Id && advancedPendingEdit.FileName == names[key.Item2])
                ? () => { _ = SaveAdvancedConfigurationAsync(server, key.Item2, names[key.Item2], editor.Text ?? ""); } : null, primary: true));
        Grid.SetColumn(actions, 1); footer.Children.Add(actions); creationFooter.Children.Add(footer);
        creationFooter.IsVisible = true;
        var note = Label(designPreview ? "Design preview · Confirmed text stays in this session."
            : "Choose next start or restart when saving a running map. Stopped maps apply immediately; file encoding is preserved.", 13);
        note.Foreground = Muted; creationFooter.Children.Add(note);
        if (advancedValidation.TryGetValue(key, out var validation)) creationFooter.Children.Add(Label(validation, 14));
        if (!designPreview && advancedReadNoticeKey == key && advancedReadNotice.Length > 0)
            creationFooter.Children.Add(Label(advancedReadNotice, 15));
    }

    private async Task ReadAdvancedConfigurationAsync(ServerDefinition server, int file, string name)
    {
        if (client is null || designPreview || advancedReading || lifetime.IsCancellationRequested) return;
        advancedReading = true; advancedReadNotice = ""; advancedReadNoticeKey = (server.Id, file); Render();
        try
        {
            var result = await client.CallAsync<ServerConfigurationFile>("server-configuration-read",
                new ServerConfigurationReadRequest(server.Id, name), TimeSpan.FromSeconds(8), lifetime.Token);
            if (result.ServerId != server.Id || result.FileName != name || result.Platform is not ("Windows" or "Linux")
                || result.Findings is null || (result.Text is null) != (result.SourceSHA256 is null)
                || result.SourceSHA256 is { } hash && (hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c))))
                throw new InvalidDataException("Unexpected configuration response.");
            await ReadAdvancedPendingAsync(server.Id);
            var key = (server.Id, file);
            var preserveDraft = advancedPreviewDrafts.TryGetValue(key, out var draft)
                && draft != advancedLoadedFiles.GetValueOrDefault(key)?.Text;
            advancedLoadedFiles[key] = result;
            if (!preserveDraft) advancedPreviewDrafts.Remove(key);
            advancedValidation.Remove(key);
            if (advancedEditorServer == server.Id && advancedEditorFile == file)
                advancedReadNotice = result.Text is null ? "This configuration file has not been created yet."
                    : preserveDraft ? "Current file loaded. Your unsaved draft is retained; Revert shows the current file."
                    : "Configuration loaded. Changes remain unsaved until you confirm Save Changes.";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or InvalidOperationException or System.Text.Json.JsonException)
        {
            if (advancedEditorServer == server.Id && advancedEditorFile == file)
                advancedReadNotice = "The file could not be read. Check the connection and installation, then retry.";
        }
        finally
        {
            advancedReading = false;
            if (!lifetime.IsCancellationRequested && advancedEditorServer == server.Id) Render();
        }
    }

    private async Task SaveAdvancedPreviewAsync(ServerDefinition server, int file, string name, string draft)
    {
        if (!designPreview || advancedPreviewSaving || lifetime.IsCancellationRequested) return;
        advancedPreviewSaving = true;
        try
        {
            var choice = await ShowPreviewSaveConfirmationAsync(server, name);
            if (choice == SaveChoice.Cancel || lifetime.IsCancellationRequested) return;
            advancedPreviewSaved[(server.Id, file)] = draft;
            advancedPreviewDrafts.Remove((server.Id, file));
        }
        catch (InvalidOperationException)
        { connection.Text = "The save confirmation could not be shown. Your draft is still available."; }
        finally
        {
            advancedPreviewSaving = false;
            if (!lifetime.IsCancellationRequested && advancedEditorServer == server.Id) Render();
        }
    }
}
