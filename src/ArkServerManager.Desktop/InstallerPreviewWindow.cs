using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ArkServerManager.Core;
using System.Text.Json;
using static ArkServerManager.Desktop.MainWindow;

namespace ArkServerManager.Desktop;

// Preview is read-only; an explicitly supplied payload enables initial app deployment.
public sealed class InstallerPreviewWindow : Window
{
    private readonly StackPanel body = new() { Spacing = 18, Margin = new Thickness(38, 28) };
    private int step;
    private string folder = "", message = "";
    private bool asa, ase;
    private bool pickingFolder, closed;
    private readonly string? payload;
    private readonly bool componentsDesignPreview;
    private string installationName = "";
    private bool installing, installed;
    private bool openAfterSetup = true;
    private bool createWindowsShortcuts = true, createDesktopShortcut;
    private bool createLinuxApplicationShortcut = true;
    private bool enableWindowsBackups = true, backupHelperReady;
    private bool enableLinuxBackupTools = true;
    private bool enableLinuxCompatibilityTools = true, compatibilityToolsReady;
    private sealed class SetupDownload
    {
        public Guid ServerId = Guid.NewGuid(), RequestId = Guid.NewGuid();
        public bool Attempted, Finished, Resume;
    }
    private readonly Dictionary<ArkEdition, SetupDownload> setupDownloads = [];

    public InstallerPreviewWindow(string? payload = null, bool componentsDesignPreview = false)
    {
        this.payload = payload;
        this.componentsDesignPreview = componentsDesignPreview;
        if (componentsDesignPreview) step = 1;
        Title = payload is null ? "ARK Server Manager Setup — Design Preview" : "ARK Server Manager Setup";
        Width = 1200; Height = 880; MinWidth = 800; MinHeight = 620;
        if (Screens.Primary is { } screen)
        {
            Width = Math.Min(Width, screen.WorkingArea.Width / screen.Scaling);
            Height = Math.Min(Height, screen.WorkingArea.Height / screen.Scaling - 32);
        }
        FontFamily = new FontFamily("Segoe UI, Inter, sans-serif"); Background = Brush.Parse("#0F1921");
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20, Margin = new Thickness(32, 18) };
        header.Children.Add(VectorIcon("M2,32 L18,2 L34,32 M9,22 L27,22", Brush.Parse("#8DD5E4"), 34));
        header.Children.Add(Label("ARK Server Manager", 23, true));
        var setup = Label("Setup", 17); setup.Foreground = Brush.Parse("#A9B8C7"); header.Children.Add(setup);
        layout.Children.Add(new Border { Background = Brush.Parse("#17222C"), Child = header });
        var scroll = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); layout.Children.Add(scroll); Content = layout; RenderSetup();
        Closed += (_, _) => closed = true;
        Closing += (_, e) => { if (installing && !installed) e.Cancel = true; };
    }

    private void RenderSetup()
    {
        body.Children.Clear();
        if (installed)
        {
            body.Children.Add(Label("ARK Server Manager is installed", 32, true));
            body.Children.Add(Label(Path.Combine(folder, installationName), 18));
            body.Children.Add(Label(asa || ase
                ? "Open Installations in the app to check your downloads and configure a server."
                : "Create a server in the app to download its required files.", 16));
            if (message.Length > 0) { var note = Label(message, 16); note.TextWrapping = TextWrapping.Wrap; body.Children.Add(note); }
            if (OperatingSystem.IsWindows() && enableWindowsBackups && !backupHelperReady)
                body.Children.Add(ActionButton(installing ? "Preparing backups…" : "Retry backup setup",
                    installing ? null : () => _ = PrepareInstalledBackupsAsync()));
            if (OperatingSystem.IsLinux() && enableLinuxBackupTools && !backupHelperReady)
                body.Children.Add(ActionButton(installing ? "Preparing backup tools…" : "Retry backup tool setup",
                    installing ? null : () => _ = PrepareInstalledBackupsAsync()));
            if (OperatingSystem.IsLinux() && enableLinuxCompatibilityTools && !compatibilityToolsReady)
                body.Children.Add(ActionButton(installing ? "Preparing ASA tools…" : "Retry ASA tool setup", installing ? null : () => _ = RetryCompatibilityToolsAsync()));
            if ((asa && setupDownloads.GetValueOrDefault(ArkEdition.ASA)?.Finished != true)
                || (ase && setupDownloads.GetValueOrDefault(ArkEdition.ASE)?.Finished != true))
                body.Children.Add(ActionButton("Check server downloads", installing ? null : () => _ = RetryServerDownloadsAsync()));
            if (installing) body.Children.Add(Label("The app is installed. You can close setup while prerequisite work continues in the background.", 16));
            var open = new CheckBox { Content = "Open ARK Server Manager", IsChecked = openAfterSetup };
            open.IsCheckedChanged += (_, _) => openAfterSetup = open.IsChecked == true;
            body.Children.Add(open);
            body.Children.Add(ActionButton("Finish", FinishSetup, primary: true));
            return;
        }
        var progress = new WrapPanel { Margin = new Thickness(0, 0, 0, 20) };
        var steps = new[] { "1  Location", "2  Components", "3  Review" };
        for (var i = 0; i < steps.Length; i++)
        {
            var label = Label(steps[i], 17, i == step); label.Margin = new Thickness(0, 0, 42, 12);
            label.Foreground = Brush.Parse(i == step ? "#FFBC43" : "#A9B8C7"); progress.Children.Add(label);
        }
        body.Children.Add(progress);
        if (step == 0) RenderLocation(); else if (step == 1) RenderComponents(); else RenderReview();
        if (message.Length > 0) { var note = Label(message, 16); note.Foreground = Brush.Parse("#FFBC43"); body.Children.Add(note); }
        var actions = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        if (step > 0 && !installing)
        { var back = ActionButton("Back", () => { step--; message = ""; RenderSetup(); }); back.Margin = new Thickness(0, 0, 18, 12); actions.Children.Add(back); }
        actions.Children.Add(ActionButton(installing ? "Installing…" : step == 2 ? "Install" : "Continue  →",
            componentsDesignPreview || installing || pickingFolder || (step == 2 && payload is null) ? null : () =>
        {
            if (step == 2) { _ = InstallAsync(); return; }
            if (step == 0 && folder.Length == 0) { message = "Choose your management folder before continuing."; RenderSetup(); return; }
            if (step == 0 && payload is not null && !ValidInstallationName())
            { message = "Enter a new folder name without path separators."; RenderSetup(); return; }
            step++; message = ""; RenderSetup();
        }, primary: true));
        body.Children.Add(actions);
        if (installing) body.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 6 });
        var preview = Label(payload is null ? "Design preview · Installation and downloads are unavailable."
            : "Selected game downloads continue through the installed background manager.", 13);
        preview.Foreground = Brush.Parse("#A9B8C7"); body.Children.Add(preview);
    }

    private void FinishSetup()
    {
        if (componentsDesignPreview) return;
        if (OperatingSystem.IsLinux() && createLinuxApplicationShortcut)
        {
            try
            {
                var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
                if (string.IsNullOrWhiteSpace(data)) data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
                var application = Path.Combine(folder, installationName);
                if (!File.Exists(AppBootstrapLayout.ExecutableFor(application, "linux-x64"))) throw new IOException("The recovery launcher is missing.");
                new LinuxAppStartup(data, "applications").Save(application, true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { message = "The app is installed, but its application-menu shortcut could not be created: " + error.Message; RenderSetup(); return; }
        }
        if (OperatingSystem.IsWindows() && createWindowsShortcuts)
        {
            try { WindowsAppShortcuts.Create(Path.Combine(folder, installationName), createDesktopShortcut); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { message = "The app is installed, but its shortcuts could not be created: " + error.Message; RenderSetup(); return; }
        }
        if (openAfterSetup)
        {
            try
            {
                var directory = Path.Combine(folder, installationName);
                var executable = AppBootstrapLayout.ExecutableFor(directory, OperatingSystem.IsWindows() ? "win-x64" : "linux-x64");
                if (!File.Exists(executable)) throw new IOException("The installed app could not be found.");
                var launch = new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = directory, CreateNoWindow = true };
                launch.ArgumentList.Add("--application"); launch.ArgumentList.Add(directory);
                _ = System.Diagnostics.Process.Start(launch) ?? throw new IOException("The installed app could not open.");
            }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
            { message = "The app is installed but could not open. Retry Finish or open it from its installation folder. " + error.Message; RenderSetup(); return; }
        }
        Close();
    }

    private void RenderLocation()
    {
        body.Children.Add(Label("Where should your servers live?", 32, true));
        body.Children.Add(Label("Choose the dedicated management folder for this computer.", 18));
        var panel = new StackPanel { Spacing = 20 };
        panel.Children.Add(Label(payload is null ? "Management folder" : "Parent folder", 22, true));
        var field = new TextBox { Text = folder, PlaceholderText = "Choose a folder…", IsReadOnly = true, FontSize = 17, Padding = new Thickness(12, 10) };
        Avalonia.Automation.AutomationProperties.SetName(field, "Management folder"); panel.Children.Add(field);
        panel.Children.Add(ActionButton(pickingFolder ? "Choosing folder…" : "Choose folder…",
            StorageProvider.CanPickFolder && !pickingFolder ? () => { _ = PickFolderAsync(); } : null));
        if (payload is not null || componentsDesignPreview)
        {
            panel.Children.Add(Label("New installation folder name", 18, true));
            var name = new TextBox { Text = installationName, PlaceholderText = "Enter a folder name", FontSize = 17 };
            name.TextChanged += (_, _) => installationName = name.Text ?? "";
            panel.Children.Add(name);
        }
        panel.Children.Add(Label("Each server keeps its own saves, downloaded mods and backups.", 16));
        body.Children.Add(Surface(panel));
    }

    private void RenderComponents()
    {
        if (payload is not null)
        {
            body.Children.Add(Label("Install ARK Server Manager", 32, true));
            body.Children.Add(Label("The application and its background manager are included in this package.", 18));
            var included = new StackPanel { Spacing = 12 };
            included.Children.Add(Label("Included runtime", 23, true));
            included.Children.Add(Label("You do not need to install .NET separately.", 16));
            included.Children.Add(Label("ARK server files can be downloaded when creating a server.", 16));
            if (OperatingSystem.IsWindows())
            {
                var shortcuts = new CheckBox { Content = "Create a Start menu shortcut", IsChecked = createWindowsShortcuts, FontSize = 18 };
                shortcuts.IsCheckedChanged += (_, _) => createWindowsShortcuts = shortcuts.IsChecked == true; included.Children.Add(shortcuts);
                var desktop = new CheckBox { Content = "Also create a desktop shortcut", IsChecked = createDesktopShortcut, FontSize = 18 };
                desktop.IsCheckedChanged += (_, _) => createDesktopShortcut = desktop.IsChecked == true; included.Children.Add(desktop);
                var backups = new CheckBox { Content = "Enable backups while servers are running", IsChecked = enableWindowsBackups, FontSize = 18 };
                backups.IsCheckedChanged += (_, _) => enableWindowsBackups = backups.IsChecked == true;
                included.Children.Add(backups);
                var explanation = Label("Windows asks for permission to prepare the backup helper. You can defer this; server startup prepares it later. This helper stays available for the current manager session.", 16);
                explanation.TextWrapping = TextWrapping.Wrap; included.Children.Add(explanation);
            }
            if (OperatingSystem.IsLinux())
            {
                var shortcut = new CheckBox { Content = "Add ARK Server Manager to the application menu", IsChecked = createLinuxApplicationShortcut, FontSize = 18 };
                shortcut.IsCheckedChanged += (_, _) => createLinuxApplicationShortcut = shortcut.IsChecked == true; included.Children.Add(shortcut);
                var tools = new CheckBox { Content = "Install missing Linux backup tools", IsChecked = enableLinuxBackupTools, FontSize = 18 };
                tools.IsCheckedChanged += (_, _) => enableLinuxBackupTools = tools.IsChecked == true;
                included.Children.Add(tools);
                var explanation = Label("Existing tools are checked first. Linux may ask for permission to install a missing system package. Capture permissions and storage support are checked separately.", 16);
                explanation.TextWrapping = TextWrapping.Wrap; included.Children.Add(explanation);
                var compatibility = new CheckBox { Content = "Prepare ASA compatibility tools", IsChecked = enableLinuxCompatibilityTools, FontSize = 18 };
                compatibility.IsCheckedChanged += (_, _) => enableLinuxCompatibilityTools = compatibility.IsChecked == true;
                included.Children.Add(compatibility);
                included.Children.Add(Label("Checks Python first and installs the managed launcher. Each ASA server's environment is prepared when it starts.", 16));
            }
            body.Children.Add(Surface(included));
            var downloads = new StackPanel { Spacing = 14 };
            downloads.Children.Add(Label("Choose server files now, or download them when creating a server later.", 16));
            var asaDownload = new CheckBox { Content = "ARK: Survival Ascended server files", IsChecked = asa, FontSize = 18 };
            asaDownload.IsCheckedChanged += (_, _) => asa = asaDownload.IsChecked == true;
            var aseDownload = new CheckBox { Content = "ARK: Survival Evolved server files", IsChecked = ase, FontSize = 18 };
            aseDownload.IsCheckedChanged += (_, _) => ase = aseDownload.IsChecked == true;
            downloads.Children.Add(asaDownload); downloads.Children.Add(aseDownload);
            downloads.Children.Add(Label("Existing tools are checked first. Selected downloads continue in the background manager after the app is installed.", 15));
            body.Children.Add(new Expander { Header = "Optional ARK server downloads", Content = Surface(downloads) });
            return;
        }
        body.Children.Add(Label("What would you like to install?", 32, true));
        body.Children.Add(Label("Select server files now, or add them when you create a server later.", 18));
        AddComponent("ARK: Survival Ascended server files", "TheIsland", asa, value => asa = value);
        AddComponent("ARK: Survival Evolved server files", "Ragnarok", ase, value => ase = value);
        var required = new StackPanel { Spacing = 10 };
        required.Children.Add(Label("Required tools", 23, true));
        required.Children.Add(Label("Existing components will be checked before anything is installed.", 16));
        body.Children.Add(Surface(required));
        body.Children.Add(Label("Server files are needed to run your server. You can install the app now and install missing server files later.", 16));
    }

    private void AddComponent(string title, string map, bool selected, Action<bool> changed)
    {
        var row = new WrapPanel(); var art = MapPicture(map, 190, 112); art.Margin = new Thickness(0, 0, 26, 10); row.Children.Add(art);
        var choice = new CheckBox { Content = title, IsChecked = selected, FontSize = 20,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) };
        choice.IsEnabled = payload is null;
        choice.IsCheckedChanged += (_, _) => changed(choice.IsChecked == true); row.Children.Add(choice);
        body.Children.Add(Surface(row, new Thickness(18)));
    }

    private void RenderReview()
    {
        body.Children.Add(Label("Review your installation", 32, true));
        var details = new StackPanel { Spacing = 18 };
        details.Children.Add(Label("Management folder", 20, true)); details.Children.Add(Label(payload is null ? folder : Path.Combine(folder, installationName), 17));
        details.Children.Add(Label("Selected components", 20, true)); details.Children.Add(Label("ARK Server Manager", 17));
        if (payload is not null && OperatingSystem.IsWindows())
            details.Children.Add(Label(enableWindowsBackups ? "Prepare Windows running backups" : "Running-backup setup deferred", 17));
        if (payload is not null && OperatingSystem.IsLinux())
            details.Children.Add(Label(enableLinuxBackupTools ? "Check and install missing Linux backup tools" : "Backup tool setup deferred", 17));
        if (asa) details.Children.Add(Label("ASA server files", 17)); if (ase) details.Children.Add(Label("ASE server files", 17));
        if (!asa && !ase) details.Children.Add(Label("Server files deferred until server creation", 17));
        details.Children.Add(Label(payload is null ? "Existing server files and dependencies will be checked first."
            : "Package files are checked before installation. Existing folders will not be overwritten.", 16));
        body.Children.Add(Surface(details));
    }

    private async Task PickFolderAsync()
    {
        if (pickingFolder || closed) return;
        pickingFolder = true; RenderSetup();
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose your management folder", AllowMultiple = false });
            try
            {
                if (closed || folders.Count == 0) return;
                var path = folders[0].TryGetLocalPath();
                if (path is null || !Path.IsPathFullyQualified(path)) message = "Choose a folder on this computer.";
                else { folder = Path.GetFullPath(path); message = ""; }
            }
            finally { foreach (var selected in folders) selected.Dispose(); }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or NotSupportedException or ArgumentException)
        { message = "The folder chooser is unavailable. Please try again."; }
        finally { pickingFolder = false; if (!closed) RenderSetup(); }
    }

    private bool ValidInstallationName() => !string.IsNullOrWhiteSpace(installationName)
        && installationName is not "." and not ".." && installationName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !installationName.Contains('/') && !installationName.Contains('\\');

    private async Task InstallAsync()
    {
        if (installing || closed || payload is null) return;
        if (!ValidInstallationName()) { message = "Choose a valid new folder name."; RenderSetup(); return; }
        installing = true; message = "Checking and installing application files…"; RenderSetup();
        try
        {
            await VerifiedPayloadInstaller.InstallAsync(payload, Path.Combine(folder, installationName),
                OperatingSystem.IsWindows() ? "win-x64" : "linux-x64");
            installed = true; message = "";
            if (OperatingSystem.IsLinux())
            {
                try
                {
                    var installation = Path.Combine(folder, installationName);
                    var notificationClient = new LocalManagerClient(installation);
                    await ManagerStartup.EnsureReadyAsync(notificationClient, installation, CancellationToken.None, installation);
                    await LinuxNotificationSetup.EnsureAsync(notificationClient, text => { message = text; if (!closed) RenderSetup(); }, CancellationToken.None);
                }
                catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or System.Text.Json.JsonException)
                { message = "The app is installed. Desktop alerts need setup; enabling them in App Settings will retry missing support."; }
            }
            if (OperatingSystem.IsWindows() && enableWindowsBackups) await EnableInstalledBackupHelperAsync();
            if (OperatingSystem.IsLinux() && enableLinuxBackupTools) await EnableInstalledBackupHelperAsync();
            if (OperatingSystem.IsLinux() && enableLinuxCompatibilityTools) await EnableInstalledCompatibilityToolsAsync();
            await DownloadSelectedServersAsync();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Text.Json.JsonException)
        { message = "Installation could not finish. Check the package and chosen folder, then try again."; }
        finally { installing = false; if (!closed) RenderSetup(); }
    }

    private async Task PrepareInstalledBackupsAsync()
    {
        if (installing || closed || !installed) return;
        installing = true; message = "Preparing running backups…"; RenderSetup();
        try { await EnableInstalledBackupHelperAsync(); }
        finally { installing = false; if (!closed) RenderSetup(); }
    }

    private async Task RetryServerDownloadsAsync()
    {
        if (installing || closed || !installed) return;
        installing = true; RenderSetup();
        try { await DownloadSelectedServersAsync(); }
        finally { installing = false; if (!closed) RenderSetup(); }
    }
    private async Task DownloadSelectedServersAsync()
    {
        if (!asa && !ase) return;
        var installation = Path.Combine(folder, installationName);
        var client = new LocalManagerClient(installation);
        try
        {
            await ManagerStartup.EnsureReadyAsync(client, installation, CancellationToken.None, installation);
            foreach (var edition in new[] { ArkEdition.ASA, ArkEdition.ASE }.Where(edition => edition == ArkEdition.ASA ? asa : ase))
            {
                if (!setupDownloads.TryGetValue(edition, out var view)) setupDownloads[edition] = view = new();
                if (view.Finished) continue;
                var platform = OperatingSystem.IsWindows() ? ServerPlatform.Windows : ServerPlatform.Linux;
                BackgroundOperation job;
                if (!view.Attempted)
                {
                    view.Attempted = true;
                    job = await client.CallAsync<BackgroundOperation>("server-installation-start",
                        new ServerInstallationRequest(view.ServerId, edition, platform, true, view.Resume), TimeSpan.FromSeconds(8), CancellationToken.None, view.RequestId);
                }
                else job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(view.RequestId), TimeSpan.FromSeconds(8), CancellationToken.None);
                while (true)
                {
                    if (job.Id != view.RequestId || job.Kind != "server-installation" || !Enum.IsDefined(job.State)) throw new InvalidDataException();
                    if (job.State == BackgroundOperationState.Succeeded)
                    {
                        var record = job.Result?.Deserialize<ServerInstallationRecord>();
                        if (record is null || record.ServerId != view.ServerId || record.Edition != edition || record.Platform != platform
                            || record.Phase != ServerInstallationPhase.FilesReady) throw new InvalidDataException();
                        view.Finished = true; break;
                    }
                    if (job.State is BackgroundOperationState.Failed or BackgroundOperationState.Canceled)
                    {
                        var inventory = await client.CallAsync<ServerInstallationInventory>("server-installation-inventory", null, TimeSpan.FromSeconds(8));
                        view.Resume = inventory.Entries.Any(entry => entry.ServerId == view.ServerId && entry.Record is not null);
                        view.Attempted = false; view.RequestId = Guid.NewGuid();
                        message = edition + " server download did not finish. Check downloads to retry; recorded files are retained.";
                        return;
                    }
                    message = edition + " · " + (job.Progress?.Message ?? "Downloading server files…");
                    if (!closed) RenderSetup();
                    await Task.Delay(1000);
                    job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(view.RequestId), TimeSpan.FromSeconds(8));
                }
            }
            message = "Selected server files are ready. Open Installations in the app to configure and create each server.";
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or System.Text.Json.JsonException or OperationCanceledException)
        { message = "Server download status is unknown. Check downloads here or review Installations in the app. Existing requests are not resent."; }
    }

    private async Task RetryCompatibilityToolsAsync()
    {
        if (installing || closed || !installed) return;
        installing = true; RenderSetup();
        try { await EnableInstalledCompatibilityToolsAsync(); }
        finally { installing = false; if (!closed) RenderSetup(); }
    }

    private async Task EnableInstalledCompatibilityToolsAsync()
    {
        try
        {
            var installation = Path.Combine(folder, installationName);
            var client = new LocalManagerClient(installation);
            await ManagerStartup.EnsureReadyAsync(client, installation, CancellationToken.None, installation);
            var request = Guid.NewGuid();
            var job = await client.CallAsync<BackgroundOperation>("linux-asa-tools-start", true, TimeSpan.FromSeconds(8), CancellationToken.None, request);
            while (true)
            {
                if (job.Id != request || job.Kind != "linux-asa-tools" || !Enum.IsDefined(job.State)) throw new InvalidDataException();
                if (job.State is not (BackgroundOperationState.Queued or BackgroundOperationState.Running)) break;
                message = job.Progress?.Message ?? "Preparing ASA compatibility tools…";
                if (!closed) RenderSetup();
                await Task.Delay(1000);
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(request), TimeSpan.FromSeconds(8), CancellationToken.None);
            }
            var result = job.Result?.Deserialize<LinuxAsaCompatibilityTools>();
            if (job.State != BackgroundOperationState.Succeeded || result is null || !Path.IsPathFullyQualified(result.Python)
                || result.Launcher != Path.Combine(installation, "Dependencies", "Compatibility", "UMU", "umu-run")) throw new InvalidDataException();
            compatibilityToolsReady = true; message = "ASA compatibility tools prepared.";
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or System.Text.Json.JsonException or OperationCanceledException)
        { message = "The application is installed. ASA tool preparation could not be confirmed; it can be retried when starting an ASA server."; }
    }

    private async Task EnableInstalledBackupHelperAsync()
    {
        try
        {
            var installation = Path.Combine(folder, installationName);
            var client = new LocalManagerClient(installation);
            await ManagerStartup.EnsureReadyAsync(client, installation, CancellationToken.None, installation);
            if (OperatingSystem.IsLinux())
            {
                var request = Guid.NewGuid();
                var job = await client.CallAsync<BackgroundOperation>("linux-backup-tools-start", true,
                    TimeSpan.FromSeconds(8), CancellationToken.None, request);
                while (true)
                {
                    if (job.Id != request || job.Kind != "linux-backup-tools" || !Enum.IsDefined(job.State)) throw new InvalidDataException();
                    if (job.State is not (BackgroundOperationState.Queued or BackgroundOperationState.Running)) break;
                    message = job.Progress?.Message ?? "Checking existing Linux backup tools…";
                    if (!closed) RenderSetup();
                    await Task.Delay(1000);
                    job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(request),
                        TimeSpan.FromSeconds(8), CancellationToken.None);
                }
                if (job.State != BackgroundOperationState.Succeeded) throw new IOException();
                var result = job.Result?.Deserialize<LinuxBackupToolSetupResult>() ?? throw new InvalidDataException();
                if (result.Ready && result.Executable is not (null or "/usr/bin/btrfs" or "/usr/sbin/btrfs")) throw new InvalidDataException();
                backupHelperReady = result.Ready; message = result.Notice;
                return;
            }
            var capability = await client.CallAsync<WindowsSnapshotCapability>("windows-backup-prepare", null,
                TimeSpan.FromMinutes(2), CancellationToken.None);
            if (!capability.Windows || !capability.Elevated || !capability.ProviderLoaded) throw new InvalidDataException();
            backupHelperReady = true;
            message = "Running-backup helper ready for this manager session. No server backup has been created yet.";
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException
            or System.Text.Json.JsonException or OperationCanceledException)
        { message = "The application is installed. Backup preparation could not be confirmed; retry here or prepare it when starting a server."; }
    }
}
