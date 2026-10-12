using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Globalization;
using System.Text.Json;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private ExistingServerRegistrationSelection? existingRegistrationSelection;
    private Guid? existingRegistrationJob;
    private ExistingServerTransferSelection? existingTransferSelection;
    private bool existingRegistrationBusy;

    private async Task ReviewExistingRegistrationAsync(InstallationCandidate candidate)
    {
        if (designPreview || client is null || existingRegistrationBusy) return;
        existingRegistrationBusy = true;
        try
        {
            if (existingRegistrationSelection is not null) { await TrackExistingRegistrationAsync(); return; }
            var mode = (InstallationImportMode)(installationImportChoice ?? 0);
            string? destination = null;
            if (mode != InstallationImportMode.ManageInPlace)
            {
                var name = installationImportFolderName.Trim();
                if (installationImportDestination.Length == 0 || name.Length == 0 || name is "." or ".."
                    || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
                    throw new ArgumentException("Choose a parent folder and a single valid new folder name.");
                destination = Path.Combine(installationImportDestination, name);
                var check = await client.CallAsync<InstallationDestinationCheck>("installation-destination-check",
                    new InstallationDestinationRequest(candidate.Directory, destination), TimeSpan.FromSeconds(8), lifetime.Token);
                if (!check.Valid || check.Destination != destination) throw new InvalidOperationException(check.Message);
            }
            var review = await client.CallAsync<ExistingServerImportReview>("existing-import-review",
                new ExistingServerImportReviewRequest(candidate.Directory, candidate.Edition, candidate.Platform), TimeSpan.FromSeconds(8), lifetime.Token);
            var (dialog, getSelection) = BuildExistingRegistrationReview(review, mode, destination);
            if (!await dialog.ShowDialog<bool>(this)) return;
            var selection = getSelection();
            if (selection is null) return;

            existingRegistrationSelection = selection; existingRegistrationJob = Guid.NewGuid();
            existingTransferSelection = destination is null ? null : new(selection, mode, destination, true);
            await TrackExistingRegistrationAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsBasicSaveError(error) || error is ArgumentException)
        { installationImportNotice = "Import could not be confirmed: " + error.Message; }
        finally { existingRegistrationBusy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }

    private async Task TrackExistingRegistrationAsync()
    {
        if (client is null || existingRegistrationSelection is not { } selection || existingRegistrationJob is not Guid id) return;
        BackgroundOperation? job;
        if (existingRegistrationSubmitted)
            job = (await client.CallAsync<OperationLookup>("operation-status-lookup", new OperationStatusRequest(id), TimeSpan.FromSeconds(8), lifetime.Token)).Operation;
        else
        {
            existingRegistrationSubmitted = true;
            job = existingTransferSelection is { } transfer
                ? await client.CallAsync<BackgroundOperation>("existing-import-transfer-start", transfer, TimeSpan.FromSeconds(8), lifetime.Token, id)
                : await client.CallAsync<BackgroundOperation>("existing-import-in-place-start", selection, TimeSpan.FromSeconds(8), lifetime.Token, id);
        }
        while (job is not null)
        {
            if (job.Id != id || job.Kind != (existingTransferSelection is null ? "existing-server-registration" : "existing-server-transfer") || !Enum.IsDefined(job.State)) throw new InvalidDataException();
            if (job.State == BackgroundOperationState.Succeeded)
            {
                string message;
                if (existingTransferSelection is { } transfer)
                {
                    var result = job.Result?.Deserialize<ExistingServerTransferRegistrationResult>();
                    if (result?.ServerId != selection.ServerId || result.Directory != transfer.Destination || result.Edition != selection.Expected.Edition)
                        throw new InvalidDataException();
                    message = result.Message + (result.CleanupRequired is null ? " Server registered." : " Cleanup required: " + result.CleanupRequired);
                }
                else
                {
                    var result = job.Result?.Deserialize<ClusterMemberReview>();
                    if (result?.ServerId != selection.ServerId || result.Directory != selection.Expected.Directory || result.Edition != selection.Expected.Edition)
                        throw new InvalidDataException();
                    message = "Existing server registered. Its files were preserved.";
                }
                existingRegistrationSelection = null; existingRegistrationJob = null; existingRegistrationSubmitted = false;
                existingTransferSelection = null;
                installationImportNotice = message; await RefreshAsync(); return;
            }
            if (job.State is BackgroundOperationState.Failed or BackgroundOperationState.Canceled)
            {
                existingRegistrationSelection = null; existingRegistrationJob = null; existingRegistrationSubmitted = false;
                existingTransferSelection = null;
                installationImportNotice = job.Error ?? "Import did not finish. Review the installation before trying again."; return;
            }
            installationImportNotice = job.Progress?.Message ?? "Registering existing server…"; Render();
            await Task.Delay(500, lifetime.Token);
            job = (await client.CallAsync<OperationLookup>("operation-status-lookup", new OperationStatusRequest(id), TimeSpan.FromSeconds(8), lifetime.Token)).Operation;
        }
        var current = await client.CallAsync<CatalogDocument>("catalog", null, TimeSpan.FromSeconds(8), lifetime.Token);
        var registered = current.Servers.SingleOrDefault(s => s.Id == selection.ServerId);
        if (registered is not null && registered.Directory == (existingTransferSelection?.Destination ?? selection.Expected.Directory) && registered.Map == selection.Map)
        {
            existingRegistrationSelection = null; existingRegistrationJob = null; existingRegistrationSubmitted = false;
            existingTransferSelection = null;
            installationImportNotice = "The requested server registration is present. The original job result is unavailable."; await RefreshAsync();
        }
        else installationImportNotice = "The import job result is unavailable. Nothing was resent; review current server registration before another request.";
    }
    private bool existingRegistrationSubmitted;

    private (Window Dialog, Func<ExistingServerRegistrationSelection?> Selection) BuildExistingRegistrationReview(ExistingServerImportReview review,
        InstallationImportMode mode = InstallationImportMode.ManageInPlace, string? destination = null)
    {
            var dialog = new Window { Title = "Review existing server", Width = 660, Height = 640, MinWidth = 560, MinHeight = 440,
                Background = Brush.Parse("#0F1921"), FontFamily = FontFamily, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var body = new StackPanel { Spacing = 18, Margin = new Thickness(28) };
            body.Children.Add(Label(mode == InstallationImportMode.ManageInPlace ? "Use this server in its current folder"
                : mode == InstallationImportMode.Copy ? "Copy and register this server" : "Move and register this server", 26, true));
            body.Children.Add(Label(mode == InstallationImportMode.ManageInPlace
                ? "Your configuration, mods and saves stay in place. Confirm the map package and launch settings before importing."
                : "The server must be stopped. Its configuration, mods and saves will be transferred together."
                    + (mode == InstallationImportMode.Copy ? " The original stays in place." : " The original folder will be removed after transfer."), 16));
            if (destination is not null) body.Children.Add(Label("Destination: " + destination, 14));
            var name = new TextBox { Text = review.Name, PlaceholderText = "Server name", MaxLength = 256 };
            var map = new TextBox { Text = review.SavedWorlds.Count == 1 ? review.SavedWorlds[0] : "", PlaceholderText = "Map package identifier", MaxLength = 256 };
            body.Children.Add(Label("Server name", 16)); body.Children.Add(name);
            body.Children.Add(Label("Map package", 16)); body.Children.Add(map);
            if (review.SavedWorlds.Count > 0) body.Children.Add(Label("Saved world candidates: " + string.Join(", ", review.SavedWorlds.Take(8)) + ". Confirm against your launch configuration.", 14));
            TextBox Number(int value) => new() { Text = value.ToString(CultureInfo.InvariantCulture), Width = 180 };
            var max = Number(review.MaxPlayers); var game = Number(review.GamePort); var query = Number(review.QueryPort); var rcon = Number(review.RconPort);
            var network = new StackPanel { Spacing = 12 };
            foreach (var field in new[] { ("Maximum players", max), ("Game port", game), ("Query port", query), ("Administration port", rcon) })
            { network.Children.Add(Label(field.Item1, 15)); network.Children.Add(field.Item2); }
            body.Children.Add(new Expander { Header = "Players and network settings", Content = network });
            var mods = new TextBox { Text = string.Join(", ", review.ConfiguredMods), AcceptsReturn = true, MinHeight = 90, PlaceholderText = "Mod IDs in load order" };
            body.Children.Add(new Expander { Header = "Active mod IDs", Content = mods });
            var findings = new StackPanel { Spacing = 10 };
            foreach (var finding in review.Findings) findings.Children.Add(Label(finding, 14));
            body.Children.Add(new Expander { Header = "Compatibility and values to confirm", Content = findings });
            var accepted = new CheckBox { Content = "I checked the map, ports and active mod IDs against this server's launch configuration." };
            body.Children.Add(accepted);
            var notice = Label("", 14); notice.Foreground = Amber; body.Children.Add(notice);
            ExistingServerRegistrationSelection? selection = null;
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(28, 16) };
            actions.Children.Add(ActionButton(mode == InstallationImportMode.ManageInPlace ? "Import server"
                : mode == InstallationImportMode.Copy ? "Copy and import" : "Move and import", () =>
            {
                if (accepted.IsChecked != true) { notice.Text = "Confirm the launch settings before importing."; return; }
                if (!int.TryParse(max.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var capacity)
                    || !int.TryParse(game.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var gamePort)
                    || !int.TryParse(query.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var queryPort)
                    || !int.TryParse(rcon.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var rconPort))
                { notice.Text = "Enter valid player capacity and port numbers."; return; }
                var ids = (mods.Text ?? "").Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                try
                {
                    new ServerSettings { Name = name.Text ?? "", MaxPlayers = capacity, GamePort = gamePort, QueryPort = queryPort, RconPort = rconPort }.Validate();
                    AsaModLaunchArguments.Build(ids);
                    if (string.IsNullOrWhiteSpace(map.Text)) throw new ArgumentException("Confirm the active map package identifier.");
                    ServerMapIdentifier.ForLaunch(review.Edition, map.Text.Trim());
                    selection = new(review, Guid.NewGuid(), map.Text.Trim(), name.Text!.Trim(), capacity, gamePort, queryPort, rconPort, ids, true);
                    dialog.Close(true);
                }
                catch (ArgumentException error) { notice.Text = error.Message; }
            }, true));
            actions.Children.Add(ActionButton("Cancel", () => dialog.Close(false)));
            var layout = new DockPanel();
            DockPanel.SetDock(actions, Dock.Bottom);
            layout.Children.Add(actions);
            layout.Children.Add(new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
            dialog.Content = layout;
        return (dialog, () => selection);
    }

    public static Window CreateExistingRegistrationDesignReview()
    {
        var owner = new MainWindow(["--design-preview"]);
        return owner.BuildExistingRegistrationReview(new("Design preview folder", ArkEdition.ASE, "Windows", "Island Friends", 70, 7777, 27015, 32330, ["TheIsland"], ["2182894352"], true, true, ["Confirm ports against your launch configuration."], new string('A', 64))).Dialog;
    }
}
