using Avalonia;
using Avalonia.Controls;
using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private bool installationSettingsReviewPage;
    private bool installationSettingsLoading;
    private InstallationConfigurationSummary? installationSettingsSummary;
    private InstallationCandidate? installationSettingsCandidate;
    private string installationSettingsNotice = "";
    private int installationSettingsRevision;

    private void ResetInstallationSettingsReview(bool open = false)
    {
        installationSettingsRevision++;
        installationSettingsReviewPage = open;
        installationSettingsLoading = false;
        installationSettingsSummary = null;
        installationSettingsCandidate = null;
        installationSettingsNotice = "";
    }

    private void RenderInstallationSettingsReview(InstallationCandidate candidate)
    {
        content.Children.Add(ActionButton("← Back to inspection", () => { ResetInstallationSettingsReview(); Render(); }));
        content.Children.Add(Label("Existing server settings", 32, true));
        var scope = Label(candidate.Edition + " · " + candidate.Directory, 16); scope.Foreground = Muted;
        content.Children.Add(scope);
        content.Children.Add(ActionButton(installationSettingsLoading ? "Reading settings…" : "Read existing settings",
            client is null || designPreview || installationSettingsLoading ? null : () => { _ = LoadInstallationSettingsAsync(candidate); }));
        if (installationSettingsCandidate == candidate && installationSettingsSummary is { } summary)
        {
            var values = new StackPanel { Spacing = 20, MaxWidth = 900 };
            void ExistingValues(string title, IReadOnlyList<string> entries)
            {
                Fact(values, title, entries.Count == 0 ? "Not found in the configuration" : string.Join("\n", entries));
            }
            ExistingValues("Session name", summary.SessionNames);
            ExistingValues("Maximum players", summary.MaximumPlayers);
            Fact(values, "Join password", summary.JoinPasswordConfigured ? "Configured · Value hidden" : "Not found or empty");
            Fact(values, "Admin password", summary.AdminPasswordConfigured ? "Configured · Value hidden" : "Not found or empty");
            Fact(values, "Configuration files", "GameUserSettings.ini: " + (summary.HasGameUserSettings ? "Found" : "Missing")
                + "\nGame.ini: " + (summary.HasGame ? "Found" : "Missing"));
            content.Children.Add(Surface(values));
            if (summary.Findings.Count > 0)
            {
                var findings = new StackPanel { Spacing = 10 };
                foreach (var finding in summary.Findings) findings.Children.Add(Label(finding, 15));
                content.Children.Add(new Expander { Header = "Review details", Content = findings });
            }
        }
        if (installationSettingsNotice.Length > 0) content.Children.Add(Label(installationSettingsNotice, 16));
        var note = Label(designPreview ? "Design preview · No configuration files were read."
            : "Read-only review. Launch arguments can override file settings; files are unchanged.", 14);
        note.Foreground = Muted; content.Children.Add(note);
    }

    private async Task LoadInstallationSettingsAsync(InstallationCandidate candidate)
    {
        if (client is null || designPreview || installationSettingsLoading || lifetime.IsCancellationRequested) return;
        var revision = ++installationSettingsRevision;
        bool StillCurrent() => revision == installationSettingsRevision && !lifetime.IsCancellationRequested
            && installationsPage && installationSettingsReviewPage && inspectedInstallation == candidate;
        installationSettingsLoading = true; installationSettingsNotice = "";
        installationSettingsSummary = null; installationSettingsCandidate = null; Render();
        try
        {
            var result = await client.CallAsync<InstallationConfigurationSummary>("installation-configuration-summary",
                new InstallationConfigurationRequest(candidate.Directory, candidate.Edition, candidate.Platform),
                TimeSpan.FromSeconds(8), lifetime.Token);
            if (!StillCurrent()) return;
            if (!string.Equals(result.Directory, Path.GetFullPath(candidate.Directory), OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || result.Platform != candidate.Platform || result.SessionNames is null || result.MaximumPlayers is null
                || result.Findings is null) throw new InvalidDataException("Unexpected configuration summary.");
            installationSettingsSummary = result; installationSettingsCandidate = candidate;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or System.Text.Json.JsonException)
        {
            if (StillCurrent())
                installationSettingsNotice = "Existing settings could not be read. Check the connection and folder access, then retry.";
        }
        finally
        {
            if (revision == installationSettingsRevision) installationSettingsLoading = false;
            if (StillCurrent()) Render();
        }
    }
}
