using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private string installationImportDestination = "";
    private string installationImportFolderName = "";
    private string installationImportNotice = "";
    private bool installationDestinationBusy;
    private bool installationDestinationChecking;
    private bool installationDestinationPage;
    private long installationImportRevision;
    private void RenderInstallationImport(InstallationCandidate candidate)
    {
        TextBlock? noticeLabel = null;
        content.Children.Add(ActionButton(installationDestinationPage ? "← Back to import choices" : "← Back to inspection", () =>
        {
            if (installationDestinationPage) installationDestinationPage = false;
            else installationImportPage = false;
            Render();
        }));
        content.Children.Add(Label("Use an existing installation", 32, true));
        var subtitle = Label("Choose how ARK Server Manager should use these server files.", 17);
        subtitle.Foreground = Muted; content.Children.Add(subtitle);
        var columns = new WrapPanel { Margin = new Thickness(0, 24, 0, 0) };
        var selected = new StackPanel { Spacing = 18, Width = 360 };
        selected.Children.Add(Label("Selected installation", 23, true));
        selected.Children.Add(MapPicture(candidate.Edition == ArkEdition.ASA ? "TheIsland" : "Ragnarok", 260, 155));
        Fact(selected, "Edition", candidate.Edition.ToString()); Fact(selected, "Folder", candidate.Directory);
        var findings = new StackPanel { Spacing = 10 };
        findings.Children.Add(Label("Compatibility and startup validation are required before use.", 15));
        foreach (var finding in candidate.Findings) findings.Children.Add(Label(finding, 14));
        selected.Children.Add(new Expander { Header = "Compatibility checks", Content = findings });
        var summary = Surface(selected); summary.Margin = new Thickness(0, 0, 24, 18);
        summary.VerticalAlignment = VerticalAlignment.Top; columns.Children.Add(summary);
        var options = new StackPanel { Spacing = 18, Width = 620 };
        options.Children.Add(Label(installationDestinationPage ? "Choose the new server folder" : "Where should these files live?", 25, true));
        var names = new[] { "Use current location", "Copy into managed folder", "Move into managed folder" };
        var descriptions = new[] { "Keep the files where they are and manage that location.",
            "Keep the original files and copy them into the management folder.",
            "Move the server files into the management folder." };
        if (installationDestinationPage && installationImportChoice is { } chosen)
        {
            var chosenLabel = Label(names[chosen], 18, true); chosenLabel.Foreground = Amber;
            options.Children.Add(chosenLabel);
        }
        for (var i = 0; !installationDestinationPage && i < names.Length; i++)
        {
            var choice = i;
            var label = new StackPanel { Spacing = 8 };
            var choiceTitle = Label((installationImportChoice == i ? "●  " : "○  ") + names[i], 18, true);
            if (installationImportChoice == i) choiceTitle.Foreground = Amber;
            label.Children.Add(choiceTitle);
            var description = Label(descriptions[i], 15); description.Foreground = Muted; label.Children.Add(description);
            var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(20),
                Background = Brush.Parse("#101C26"), BorderThickness = new Thickness(1),
                BorderBrush = installationImportChoice == i ? Amber : Brush.Parse("#405363") };
            Avalonia.Automation.AutomationProperties.SetName(button, names[i]);
            button.Click += (_, _) => { installationImportRevision++; installationImportChoice = choice; installationImportNotice = ""; Render(); };
            options.Children.Add(button);
        }
        if (installationDestinationPage && installationImportChoice is 1 or 2)
        {
            options.Children.Add(Label("Parent folder", 17, true));
            var destination = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            destination.Children.Add(new TextBox { Text = installationImportDestination, IsReadOnly = true, PlaceholderText = "Choose the parent folder",
                FontSize = 16, Padding = new Thickness(12), Margin = new Thickness(0, 0, 12, 0) });
            var browse = ActionButton("Browse…", installationDestinationBusy || !StorageProvider.CanPickFolder ? null
                : () => { _ = ChooseImportDestinationAsync(candidate); });
            Grid.SetColumn(browse, 1); destination.Children.Add(browse); options.Children.Add(destination);
            options.Children.Add(Label("New server folder name", 17, true));
            var folderName = new TextBox { Text = installationImportFolderName, PlaceholderText = "Enter a new folder name",
                FontSize = 16, Padding = new Thickness(12) };
            Avalonia.Automation.AutomationProperties.SetName(folderName, "New server folder name");
            options.Children.Add(folderName);
            var destinationPreview = Label("", 14); destinationPreview.Foreground = Muted;
            void UpdateDestinationPreview()
            {
                var name = installationImportFolderName.Trim();
                destinationPreview.Text = name.Length == 0 ? "Enter a folder name to choose the destination."
                    : name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\')
                    ? "Enter a single valid folder name."
                    : installationImportDestination.Length == 0 ? "Choose a parent folder to complete the destination."
                    : "Destination: " + Path.Combine(installationImportDestination, name);
            }
            folderName.TextChanged += (_, _) =>
            {
                installationImportRevision++; installationImportFolderName = folderName.Text ?? ""; installationImportNotice = "";
                if (noticeLabel is not null) noticeLabel.Text = "";
                UpdateDestinationPreview();
            };
            UpdateDestinationPreview(); options.Children.Add(destinationPreview);
            var validation = Label("The new destination must not already exist. It will be checked before any transfer.", 14);
            validation.Foreground = Muted; options.Children.Add(validation);
            options.Children.Add(ActionButton(installationDestinationChecking ? "Checking destination…" : "Check destination",
                designPreview || client is null || installationDestinationChecking || installationDestinationBusy ? null
                    : () => { _ = CheckImportDestinationAsync(candidate); }));
        }
        var note = Label(designPreview ? "Design preview · No files will be copied, moved or imported."
            : "Review compatibility and confirm your choice before using, copying or moving files. Running servers must be stopped first.", 15);
        note.Foreground = Muted; options.Children.Add(note);
        if (installationImportNotice.Length > 0)
        { noticeLabel = Label(installationImportNotice, 15); noticeLabel.Foreground = Amber; options.Children.Add(noticeLabel); }
        var actions = new WrapPanel();
        var cancel = ActionButton("Cancel", () => { installationImportPage = false; Render(); });
        cancel.Margin = new Thickness(0, 0, 18, 0); actions.Children.Add(cancel);
        actions.Children.Add(ActionButton(installationDestinationPage || installationImportChoice == 0 ? "Continue" : "Choose destination  →",
            !installationDestinationPage && installationImportChoice is 1 or 2
                ? () => { installationDestinationPage = true; installationImportNotice = ""; Render(); }
                : installationImportChoice is 0 or 1 or 2 && client is not null && !designPreview && !existingRegistrationBusy
                    ? () => _ = ReviewExistingRegistrationAsync(candidate) : null,
            primary: true)); options.Children.Add(actions);
        var form = Surface(options); form.Margin = new Thickness(0, 0, 0, 18); columns.Children.Add(form);
        content.Children.Add(columns);
    }

    private async Task CheckImportDestinationAsync(InstallationCandidate candidate)
    {
        if (client is null || designPreview || installationDestinationChecking || lifetime.IsCancellationRequested) return;
        var parent = installationImportDestination;
        var name = installationImportFolderName.Trim();
        var mode = installationImportChoice;
        var revision = installationImportRevision;
        if (parent.Length == 0 || name.Length == 0 || name is "." or ".."
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
        { installationImportNotice = "Choose a parent folder and enter a single valid new folder name."; Render(); return; }
        var target = Path.Combine(parent, name);
        installationDestinationChecking = true; installationImportNotice = ""; Render();
        try
        {
            var result = await client.CallAsync<InstallationDestinationCheck>("installation-destination-check",
                new InstallationDestinationRequest(candidate.Directory, target), TimeSpan.FromSeconds(8), lifetime.Token);
            if (revision != installationImportRevision || inspectedInstallation != candidate || installationImportDestination != parent
                || installationImportFolderName.Trim() != name || installationImportChoice != mode) return;
            if (result.Destination != target || string.IsNullOrWhiteSpace(result.Message))
                throw new InvalidDataException("Unexpected destination response.");
            installationImportNotice = result.Message;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or System.Text.Json.JsonException)
        {
            if (revision == installationImportRevision && inspectedInstallation == candidate)
                installationImportNotice = "The destination check could not be confirmed. Reconnect and try again.";
        }
        finally
        {
            installationDestinationChecking = false;
            if (!lifetime.IsCancellationRequested && installationsPage && installationImportPage) Render();
        }
    }

    private async Task ChooseImportDestinationAsync(InstallationCandidate candidate)
    {
        if (installationDestinationBusy || !StorageProvider.CanPickFolder || lifetime.IsCancellationRequested) return;
        installationDestinationBusy = true;
        var revision = installationImportRevision;
        installationImportNotice = "";
        Render();
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            { Title = "Choose the parent folder for the new server folder", AllowMultiple = false });
            try
            {
                if (lifetime.IsCancellationRequested || folders.Count == 0 || inspectedInstallation != candidate
                    || revision != installationImportRevision) return;
                var path = folders[0].TryGetLocalPath();
                if (path is null) installationImportNotice = "Choose a folder on this computer.";
                else { installationImportRevision++; installationImportDestination = Path.GetFullPath(path); }
            }
            finally { foreach (var folder in folders) folder.Dispose(); }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            if (revision == installationImportRevision && inspectedInstallation == candidate)
                installationImportNotice = "The destination could not be selected. Please try again.";
        }
        finally
        {
            installationDestinationBusy = false;
            if (!lifetime.IsCancellationRequested && installationsPage && installationImportPage) Render();
        }
    }
}
