using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private int applicationCategory;
    private bool preparingWindowsBackups;
    private string windowsBackupNotice = "";
    private bool preparingLinuxBackupTools;
    private Guid? linuxBackupToolRequest;
    private string linuxBackupToolNotice = "";
    private string startupPreferenceNotice = "";

    private void RenderApplicationPreferences()
    {
        if (applicationCategory == 5) { RenderPresets(); return; }
        content.Children.Add(Label("App Settings", 36, true));
        var subtitle = Label("How the manager works on this computer.", 18); subtitle.Foreground = Muted;
        content.Children.Add(subtitle);
        var names = new[] { "General", "Startup", "Notifications", "Updates", "Storage", "Presets" };
        var tabs = new WrapPanel { Margin = new Thickness(0, 24, 0, 22) };
        for (var i = 0; i < names.Length; i++)
        {
            var index = i;
            var button = new Button { Content = names[i], Padding = new Thickness(20, 14), FontSize = 17,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Foreground = applicationCategory == i ? Amber : Muted };
            button.Click += (_, _) => { applicationCategory = index; Render(); };
            tabs.Children.Add(new Border { BorderThickness = new Thickness(0, 0, 0, 3),
                BorderBrush = applicationCategory == i ? Amber : Brushes.Transparent, Child = button });
        }
        content.Children.Add(new Border { BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = Brush.Parse("#2B3A46"), Child = tabs });
        if (applicationCategory == 0)
        {
            var panels = new WrapPanel();
            var preferences = new StackPanel { Spacing = 22 };
            preferences.Children.Add(Label("Application Settings", 24, true));
            Fact(preferences, "App theme", "Dark");
            Fact(preferences, "Preferred language", "System default");
            Fact(preferences, "Management folder", designPreview ? "Design preview · No folder selected" : "Chosen during installation");
            Fact(preferences, "Hosting status", "Hosted on this computer");
            var panel = Surface(preferences); panel.Width = 642; panel.MinHeight = 410;
            panel.Margin = new Thickness(0, 0, 22, 18); panels.Children.Add(panel);
            var information = new StackPanel { Spacing = 22 };
            information.Children.Add(Label("Application Information", 24, true));
            information.Children.Add(Label("Information about this installation.", 16));
            var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24 };
            brand.Children.Add(VectorIcon("M2,32 L18,2 L34,32 M9,22 L27,22", Brush.Parse("#8DD5E4"), 64));
            var description = new StackPanel { Spacing = 8 };
            description.Children.Add(Label("ARK Server Manager", 24, true));
            description.Children.Add(Label("Manage your ARK servers and clusters\non this computer.", 17));
            brand.Children.Add(description); information.Children.Add(Surface(brand));
            var infoPanel = Surface(information); infoPanel.Width = 642; infoPanel.MinHeight = 410;
            infoPanel.Margin = new Thickness(0, 0, 22, 18); panels.Children.Add(infoPanel);
            content.Children.Add(panels);
        }
        else
        {
            var body = new StackPanel { Spacing = 20 };
            body.Children.Add(Label(names[applicationCategory], 26, true));
            body.Children.Add(Label(applicationCategory switch
            {
                1 => "Choose whether the manager opens when you sign in to this computer.",
                2 => "Choose where alerts appear and which new events you hear about.",
                3 => "The app checks for updates before opening. If the app update fails, the installed version opens with a notice. Running servers ask before being stopped for server updates.",
                _ => "The installer selects the management folder. Each server keeps its associated saves, downloaded mods and backups."
            }, 17));
            if (applicationCategory == 2) AddNotificationPreferences(body);
            if (applicationCategory == 1 && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
            {
                var installed = !designPreview && InstalledAppSettingsFile.Read(AppContext.BaseDirectory) is not null;
                var enabled = false;
                try { if (installed) enabled = OperatingSystem.IsWindows() ? WindowsAppStartup.Read(AppContext.BaseDirectory) : LinuxStartup().Read(AppContext.BaseDirectory.TrimEnd('/')); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { startupPreferenceNotice = error.Message; }
                var choice = new CheckBox { Content = "Open ARK Server Manager when I sign in", IsChecked = enabled, IsEnabled = installed };
                body.Children.Add(choice);
                body.Children.Add(ActionButton("Save startup setting…", installed ? () => _ = SaveStartupAsync(choice.IsChecked == true) : null));
                if (!installed) body.Children.Add(Label("Startup can be configured after installing the app.", 16));
                if (startupPreferenceNotice.Length > 0) body.Children.Add(Label(startupPreferenceNotice, 16));
                body.Children.Add(Label("Automatic server startup", 23, true));
                var startupDescription = Label("Choose which servers and clusters start when the app opens, including when you sign in. Nothing starts automatically until you save a selection.", 16);
                startupDescription.TextWrapping = TextWrapping.Wrap; body.Children.Add(startupDescription);
                body.Children.Add(ActionButton("Choose servers…", designPreview || client is null ? null : () => _ = ChooseStartupServersAsync()));
            }
            if (applicationCategory == 4 && OperatingSystem.IsWindows())
            {
                var explanation = Label("Running backups need Windows permission. Enable the helper once for this manager session; Windows will ask you to approve it.", 16);
                explanation.TextWrapping = TextWrapping.Wrap; body.Children.Add(explanation);
                body.Children.Add(ActionButton(preparingWindowsBackups ? "Enabling…" : "Enable running backups",
                    designPreview || client is null || preparingWindowsBackups ? null : () => _ = PrepareWindowsBackupsAsync()));
                if (windowsBackupNotice.Length > 0)
                {
                    var notice = Label(windowsBackupNotice, 16); notice.TextWrapping = TextWrapping.Wrap;
                    body.Children.Add(notice);
                }
            }
            if (applicationCategory == 4 && OperatingSystem.IsLinux())
            {
                var explanation = Label("Check existing snapshot tools and install them if missing. Linux may ask for permission. Filesystem support and capture permissions are checked separately.", 16);
                explanation.TextWrapping = TextWrapping.Wrap; body.Children.Add(explanation);
                body.Children.Add(ActionButton(preparingLinuxBackupTools ? "Checking tools…" : linuxBackupToolRequest is null ? "Set up backup tools" : "Check tool setup",
                    designPreview || client is null || preparingLinuxBackupTools ? null : () => _ = PrepareLinuxBackupToolsAsync()));
                if (linuxBackupToolNotice.Length > 0)
                { var notice = Label(linuxBackupToolNotice, 16); notice.TextWrapping = TextWrapping.Wrap; body.Children.Add(notice); }
            }
            content.Children.Add(Surface(body));
        }
        if (designPreview)
        {
            var note = Label("Design preview · Settings are not saved.", 13);
            note.Foreground = Muted; content.Children.Add(note);
        }
    }
    private async Task SaveStartupAsync(bool enabled)
    {
        if ((!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) || designPreview) return;
        var dialog = new Window { Title = "Save startup setting?", Width = 520, Height = 280, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush.Parse("#0F1921"), FontFamily = FontFamily };
        var body = new StackPanel { Margin = new Thickness(28), Spacing = 18 };
        body.Children.Add(Label(enabled ? "Open the app when you sign in?" : "Stop opening the app at sign-in?", 23, true));
        body.Children.Add(Label("Startup uses the recovery launcher. The app checks for updates before opening.", 16));
        body.Children.Add(ActionButton("Save", () => dialog.Close(true), true)); body.Children.Add(ActionButton("Cancel", () => dialog.Close(false))); dialog.Content = body;
        if (!await dialog.ShowDialog<bool>(this)) return;
        try
        {
            if (OperatingSystem.IsWindows()) WindowsAppStartup.Save(AppContext.BaseDirectory, enabled);
            else
            {
                if (!File.Exists(AppBootstrapLayout.ExecutableFor(AppContext.BaseDirectory, "linux-x64"))) throw new IOException("The recovery launcher is missing.");
                LinuxStartup().Save(AppContext.BaseDirectory.TrimEnd('/'), enabled);
            }
            startupPreferenceNotice = "Startup setting saved.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { startupPreferenceNotice = "Startup could not be saved: " + error.Message; }
        Render();
    }
    private static LinuxAppStartup LinuxStartup()
    {
        var configuration = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configuration)) configuration = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return new(configuration);
    }

    private async Task PrepareWindowsBackupsAsync()
    {
        if (client is null || designPreview || preparingWindowsBackups) return;
        preparingWindowsBackups = true; windowsBackupNotice = ""; Render();
        try
        {
            var capability = await client.CallAsync<WindowsSnapshotCapability>("windows-backup-prepare", null,
                TimeSpan.FromMinutes(2), lifetime.Token);
            if (!capability.Windows || !capability.Elevated || !capability.ProviderLoaded) throw new InvalidDataException();
            windowsBackupNotice = "Backup helper ready for this manager session. No server backup has been created yet.";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or JsonException or OperationCanceledException)
        { windowsBackupNotice = "Backup setup could not be confirmed. Enable it again to check or retry."; }
        finally { preparingWindowsBackups = false; if (!lifetime.IsCancellationRequested) Render(); }
    }

    private async Task PrepareLinuxBackupToolsAsync()
    {
        if (client is null || designPreview || preparingLinuxBackupTools) return;
        preparingLinuxBackupTools = true; linuxBackupToolNotice = ""; Render();
        try
        {
            BackgroundOperation job;
            if (linuxBackupToolRequest is Guid request)
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(request), TimeSpan.FromSeconds(8), lifetime.Token);
            else
            {
                linuxBackupToolRequest = Guid.NewGuid();
                job = await client.CallAsync<BackgroundOperation>("linux-backup-tools-start", true, TimeSpan.FromSeconds(8), lifetime.Token, linuxBackupToolRequest);
            }
            while (true)
            {
                if (job.Id != linuxBackupToolRequest || job.Kind != "linux-backup-tools" || !Enum.IsDefined(job.State)) throw new InvalidDataException();
                if (job.State is not (BackgroundOperationState.Queued or BackgroundOperationState.Running)) break;
                linuxBackupToolNotice = job.Progress?.Message ?? "Checking existing Linux backup tools…"; Render();
                await Task.Delay(1000, lifetime.Token);
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(linuxBackupToolRequest.Value), TimeSpan.FromSeconds(8), lifetime.Token);
            }
            if (job.State == BackgroundOperationState.Succeeded)
            {
                var result = job.Result?.Deserialize<LinuxBackupToolSetupResult>() ?? throw new InvalidDataException();
                if (result.Ready && result.Executable is not ("/usr/bin/btrfs" or "/usr/sbin/btrfs")) throw new InvalidDataException();
                linuxBackupToolNotice = result.Notice;
            }
            else linuxBackupToolNotice = "Tool setup did not complete. Check Linux permissions and package availability, then retry.";
            linuxBackupToolRequest = null;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or JsonException or OperationCanceledException)
        { linuxBackupToolNotice = "Tool setup result is unknown. Check the existing request before another attempt."; }
        finally { preparingLinuxBackupTools = false; if (!lifetime.IsCancellationRequested) Render(); }
    }
}


