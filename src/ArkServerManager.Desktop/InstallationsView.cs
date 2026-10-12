using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private bool installationsPage;
    private bool installationSearchStarted;
    private bool installationBusy;
    private Guid? discoveryId;
    private BackgroundOperation? installationOperation;
    private bool installationProgressPage;
    private bool installationConnectionLost;
    private DiscoveryResult? discovery;
    private InstallationCandidate? inspectedInstallation;
    private bool installationImportPage;
    private bool installationStopChecking;
    private InstallationStopCheck? installationStopCheck;
    private string installationStopNotice = "";
    private int? installationImportChoice;
    private bool installationComponents;
    private string installationFilter = "";
    private string installationMessage = "Search known server and Steam locations, or choose a folder manually.";

    private void RenderInstallations()
    {
        if (installationProgressPage) { RenderInstallationProgress(); return; }
        if (designPreview && discovery is null)
            discovery = new DiscoveryResult([
                new InstallationCandidate("Illustrative ASA folder", ArkEdition.ASA, "Preview only", "Windows", [], [], [], ["Compatibility has not been checked."]),
                new InstallationCandidate("Illustrative ASE folder", ArkEdition.ASE, "Preview only", "Windows", [], [], [], ["Compatibility has not been checked."])], []);
        if (inspectedInstallation is { } inspected)
        {
            if (installationSettingsReviewPage) { RenderInstallationSettingsReview(inspected); return; }
            if (installationImportPage) RenderInstallationImport(inspected);
            else RenderInstallationInspection(inspected);
            return;
        }
        content.Children.Add(Label("Installations", 36, true));
        var subtitle = Label("Find and manage ARK server files on this computer.", 18); subtitle.Foreground = Muted;
        content.Children.Add(subtitle);
        var tabs = new WrapPanel { Margin = new Thickness(0, 22, 0, 16) };
        tabs.Children.Add(ActionButton("Detected installations", () => { installationComponents = false; Render(); }, !installationComponents));
        tabs.Children.Add(ActionButton("Components", () => { installationComponents = true; Render(); }, installationComponents));
        content.Children.Add(tabs);
        AddInstallationInventory();
        AddImportRecovery();
        if (installationComponents)
        {
            var components = new StackPanel { Spacing = 18 }; components.Children.Add(Label("Required components", 24, true));
            components.Children.Add(Label("Application runtime", 20, true));
            components.Children.Add(Label("Included with ARK Server Manager. No separate .NET installation is needed.", 16));
            var steamPath = managerRoot is null ? null : Path.Combine(managerRoot, "Dependencies", "SteamCMD",
                OperatingSystem.IsWindows() ? "Windows" : "Linux", OperatingSystem.IsWindows() ? "steamcmd.exe" : "steamcmd.sh");
            components.Children.Add(Label("SteamCMD", 20, true));
            components.Children.Add(Label(designPreview ? "Design preview · Tool status is illustrative."
                : steamPath is not null && File.Exists(steamPath) ? "Tool files found. They are validated before server downloads and updates."
                : "Installed automatically when server files are downloaded or updated.", 16));
            components.Children.Add(Label("Server and backup support", 20, true));
            components.Children.Add(Label("Existing dependencies are checked before use. Missing server tools are prepared when needed; running backups may require system permission.", 16));
            content.Children.Add(Surface(components)); return;
        }
        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        var search = new TextBox { Text = installationFilter, PlaceholderText = "Search installations…", Width = 400,
            FontSize = 16, Padding = new Thickness(12, 10), Margin = new Thickness(0, 0, 18, 12) };
        Avalonia.Automation.AutomationProperties.SetName(search, "Search detected installations"); toolbar.Children.Add(search);
        toolbar.Children.Add(ActionButton(installationBusy ? "Searching…" : "Search this computer", client is null || installationBusy ? null
            : () => { inspectedInstallation = null; _ = SearchInstallationsAsync(); }));
        var choose = ActionButton("Choose folder…", client is null || installationBusy || !StorageProvider.CanPickFolder ? null
            : () => { _ = ChooseInstallationAsync(); }); choose.Margin = new Thickness(16, 0, 0, 12); toolbar.Children.Add(choose);
        content.Children.Add(toolbar);
        if (designPreview || installationOperation is not null || discoveryId is not null)
            content.Children.Add(ActionButton(designPreview ? "Preview search progress" : "View search progress",
                () => { installationProgressPage = true; Render(); }));
        var message = Label(designPreview ? "Design preview · No folders were searched." : installationMessage, 15);
        message.Foreground = Muted; content.Children.Add(message);
        var rows = new StackPanel { Spacing = 18 };
        void Populate()
        {
            rows.Children.Clear();
            if (discovery is null) { rows.Children.Add(Surface(Label("Search this computer or choose an existing server folder.", 17))); return; }
            var candidates = discovery.Candidates.Where(c => (c.Edition + " " + c.Platform + " " + c.Directory)
                .Contains(installationFilter, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var candidate in candidates)
            {
                var row = new WrapPanel();
                var art = MapPicture(candidate.Edition == ArkEdition.ASA ? "TheIsland" : "Ragnarok", 240, 150);
                art.Margin = new Thickness(0, 0, 26, 12); row.Children.Add(art);
                var details = new StackPanel { Spacing = 12, Width = 770, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 20, 12) };
                details.Children.Add(Label(candidate.Edition == ArkEdition.ASA ? "ARK: Survival Ascended (ASA)" : "ARK: Survival Evolved (ASE)", 24, true));
                details.Children.Add(Label(candidate.Directory, 16));
                var state = Label(candidate.Platform + " · Detected files · Validation required", 15); state.Foreground = Amber;
                details.Children.Add(state); row.Children.Add(details);
                var inspect = ActionButton("Inspect  ›", () => { ResetInstallationSettingsReview(); inspectedInstallation = candidate; Render(); });
                inspect.VerticalAlignment = VerticalAlignment.Center; inspect.Margin = new Thickness(0, 0, 0, 12); row.Children.Add(inspect);
                rows.Children.Add(Surface(row, new Thickness(18)));
            }
            if (candidates.Length == 0) rows.Children.Add(Surface(Label(discovery.Candidates.Count == 0
                ? "No recognized installation found. Choose its folder manually." : "No installations match your search.", 17)));
        }
        search.TextChanged += (_, _) => { installationFilter = search.Text ?? ""; Populate(); };
        Populate(); content.Children.Add(rows);
        if (discovery?.Findings.Count > 0)
        {
            var findings = new StackPanel { Spacing = 8 };
            foreach (var finding in discovery.Findings) findings.Children.Add(Label(finding, 14));
            content.Children.Add(new Expander { Header = "Search details", Content = Surface(findings) });
        }
    }

    private void RenderInstallationInspection(InstallationCandidate candidate)
    {
        content.Children.Add(ActionButton("← Back to Installations", () => { ResetInstallationSettingsReview(); inspectedInstallation = null; Render(); }));
        content.Children.Add(Label("Inspect installation", 32, true));
        var summary = new StackPanel { Spacing = 16 };
        Fact(summary, "Edition", candidate.Edition.ToString()); Fact(summary, "Platform", candidate.Platform);
        Fact(summary, "Folder", candidate.Directory);
        Fact(summary, "Contents found", candidate.ConfigurationFiles.Count + " configuration files · " + candidate.WorldSaves.Count
            + " world saves · " + candidate.ModDirectories.Count + " mod folders");
        var warning = Label("Detected files require compatibility and startup validation before use.", 16); warning.Foreground = Amber;
        summary.Children.Add(warning); content.Children.Add(Surface(summary));
        content.Children.Add(ActionButton("Review existing settings", () => { ResetInstallationSettingsReview(open: true); Render(); }));
        content.Children.Add(ActionButton(installationStopChecking ? "Checking running state…" : "Check running state",
            client is null || designPreview || installationStopChecking ? null : () => { _ = CheckInstallationRunningAsync(candidate); }));
        if (installationStopCheck is { } stopped && string.Equals(stopped.Directory, Path.TrimEndingDirectorySeparator(candidate.Directory),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            var state = Label(stopped.State switch
            {
                InstallationStopState.Running => "A server is running. Stopping it requires your approval before transfer.",
                InstallationStopState.Stopped => "No matching running server was found at the last check. Transfer will check again.",
                _ => "Stopped state could not be verified. Transfer must wait until it can be verified."
            }, 16);
            state.Foreground = Amber; content.Children.Add(state);
            var details = new StackPanel { Spacing = 8 };
            details.Children.Add(Label("Checked: " + stopped.MeasuredAt.ToLocalTime().ToString("g"), 14));
            foreach (var finding in stopped.Findings) details.Children.Add(Label(finding, 14));
            content.Children.Add(new Expander { Header = "Running-state details", Content = details });
        }
        if (installationStopNotice.Length > 0) content.Children.Add(Label(installationStopNotice, 15));
        var findings = new StackPanel { Spacing = 10 };
        foreach (var finding in candidate.Findings) findings.Children.Add(Label(finding, 15));
        content.Children.Add(new Expander { Header = "Inspection details", Content = Surface(findings) });
        var choices = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        choices.Children.Add(ActionButton("Choose how to use these files", () =>
        { installationImportRevision++; installationImportPage = true; installationImportChoice = null; installationImportDestination = "";
            installationDestinationPage = false; installationImportFolderName = ""; installationImportNotice = ""; Render(); }, true));
        content.Children.Add(choices);
        var note = Label(designPreview ? "Design preview · Import does not change files."
            : "Review your import choice before changes are made. A running installation must be stopped first.", 14);
        note.Foreground = Muted; content.Children.Add(note);
    }

    private async Task CheckInstallationRunningAsync(InstallationCandidate candidate)
    {
        if (client is null || designPreview || installationStopChecking || lifetime.IsCancellationRequested) return;
        installationStopChecking = true; installationStopNotice = ""; installationStopCheck = null; Render();
        try
        {
            var result = await client.CallAsync<InstallationStopCheck>("installation-stop-check",
                new InstallationEvaluationRequest(candidate.Directory), TimeSpan.FromSeconds(8), lifetime.Token);
            if (inspectedInstallation != candidate) return;
            if (!Enum.IsDefined(result.State) || result.Processes is null || result.Findings is null || result.MeasuredAt == default
                || !string.Equals(result.Directory, Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate.Directory)),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException("Unexpected running-state response.");
            installationStopCheck = result;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or System.Text.Json.JsonException)
        { if (inspectedInstallation == candidate) installationStopNotice = "Running state could not be confirmed. Try again after reconnecting."; }
        finally
        {
            installationStopChecking = false;
            if (!lifetime.IsCancellationRequested && installationsPage) Render();
        }
    }

    private async Task SearchInstallationsAsync()
    {
        if (client is null || installationBusy || lifetime.IsCancellationRequested) return;
        installationBusy = true; installationSearchStarted = true;
        refresh.IsEnabled = false;
        installationMessage = "Searching for installed ARK servers…";
        installationProgressPage = true;
        installationConnectionLost = false;
        if (installationsPage) Render();
        try
        {
            // Preserve the identity after a lost response; retrying cannot submit a duplicate search.
            if (discoveryId is null) { discoveryId = Guid.NewGuid(); installationOperation = null; }
            var job = await client.CallAsync<BackgroundOperation>("discover-start", null, TimeSpan.FromSeconds(8), lifetime.Token, discoveryId);
            UpdateInstallationOperation(job);
            while (job.State is BackgroundOperationState.Queued or BackgroundOperationState.Running)
            {
                await Task.Delay(750, lifetime.Token);
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(discoveryId.Value),
                    TimeSpan.FromSeconds(8), lifetime.Token);
                UpdateInstallationOperation(job);
            }
            if (job.State is BackgroundOperationState.Failed or BackgroundOperationState.Canceled) discoveryId = null;
            if (job.State != BackgroundOperationState.Succeeded || job.Result is null)
                throw new InvalidOperationException("Search did not complete.");
            discovery = job.Result.Value.Deserialize<DiscoveryResult>() ?? throw new InvalidDataException("Missing search result.");
            discoveryId = null;
            installationMessage = "Search complete. Select inspection details to see what was found.";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or JsonException)
        {
            installationConnectionLost = discoveryId is not null;
            installationMessage = discoveryId is null
                ? "Search did not finish. Refresh to try again."
                : "Search could not be confirmed. Refresh to reconnect to the same search; existing files were not changed.";
        }
        finally
        {
            installationBusy = false;
            if (!lifetime.IsCancellationRequested) { refresh.IsEnabled = true; if (installationsPage) Render(); }
        }
    }

    private void UpdateInstallationOperation(BackgroundOperation job)
    {
        if (job.Id != discoveryId || job.Kind != "discovery" || !Enum.IsDefined(job.State)
            || job.Progress?.Percent is < 0 or > 100)
            throw new InvalidDataException("Unexpected search status.");
        installationOperation = job;
        if (installationsPage && installationProgressPage) Render();
    }

    private void RenderInstallationProgress()
    {
        content.Children.Add(ActionButton("← Back to Installations", () => { installationProgressPage = false; Render(); }));
        var panel = new StackPanel { Spacing = 22, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(Label("Finding ARK installations", 30, true));
        if (designPreview)
        {
            var previewNote = Label("Design preview · No search is running.", 15);
            previewNote.Foreground = Amber; panel.Children.Add(previewNote);
        }
        var state = installationOperation?.State;
        var pending = state is null or BackgroundOperationState.Queued or BackgroundOperationState.Running;
        var title = installationConnectionLost ? "Connection interrupted"
            : state == BackgroundOperationState.Succeeded ? "Search complete"
            : state == BackgroundOperationState.Failed ? "Search failed"
            : state == BackgroundOperationState.Canceled ? "Search canceled"
            : installationOperation?.Progress?.Message ?? (designPreview ? "Searching known server locations…" : "Waiting for search status…");
        panel.Children.Add(Label(title, 18));
        if (pending && !installationConnectionLost)
        {
            var percent = installationOperation?.Progress?.Percent;
            panel.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = percent ?? 0,
                IsIndeterminate = percent is null, Height = 10 });
            if (percent is not null) panel.Children.Add(Label($"{percent}%", 15));
        }
        var note = Label(installationConnectionLost
            ? "The search status could not be confirmed. Reconnect to check the same search."
            : pending ? designPreview ? "A running search will continue when you return to the app."
                : "You can return to the app while the search continues in the background."
            : installationMessage, 16);
        note.Foreground = Muted; panel.Children.Add(note);
        if (installationOperation?.Error is { Length: > 0 } error)
            panel.Children.Add(new Expander { Header = "Search details", Content = Label(error, 14) });
        if (installationConnectionLost)
            panel.Children.Add(ActionButton("Reconnect", installationBusy ? null : () => { _ = SearchInstallationsAsync(); }, true));
        else panel.Children.Add(ActionButton(pending ? "Return to app" : "View installations",
            () => { installationProgressPage = false; Render(); }, true));
        var card = Surface(panel); card.HorizontalAlignment = HorizontalAlignment.Center;
        card.Margin = new Thickness(0, 50, 0, 0); content.Children.Add(card);
    }

    private async Task ChooseInstallationAsync()
    {
        if (client is null || installationBusy || !StorageProvider.CanPickFolder) return;
        IReadOnlyList<IStorageFolder> folders;
        try
        {
            folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            { Title = "Choose an ARK server installation", AllowMultiple = false });
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or NotSupportedException)
        { installationMessage = "The folder chooser is unavailable. Please try again."; if (installationsPage) Render(); return; }
        if (lifetime.IsCancellationRequested || folders.Count == 0) return;
        var path = folders[0].TryGetLocalPath();
        if (path is null) { installationMessage = "Choose a folder on this computer."; Render(); return; }
        installationBusy = true; refresh.IsEnabled = false;
        installationMessage = "Inspecting the selected folder…"; Render();
        try
        {
            discovery = await client.CallAsync<DiscoveryResult>("installation-evaluate", new InstallationEvaluationRequest(path),
                TimeSpan.FromSeconds(8), lifetime.Token);
            installationMessage = "Folder inspected. Copy, move and import actions will require a separate choice.";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or JsonException)
        { installationMessage = "The selected folder could not be inspected. Check the manager connection and folder access."; }
        finally
        {
            installationBusy = false;
            if (!lifetime.IsCancellationRequested) { refresh.IsEnabled = true; if (installationsPage) Render(); }
        }
    }
}
