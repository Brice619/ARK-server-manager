using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private sealed class InstallationJobView(ServerInstallationRequest selection, string name, Guid? requestId = null)
    {
        public readonly ServerInstallationRequest Selection = selection;
        public readonly string Name = name;
        public readonly Guid RequestId = requestId ?? Guid.NewGuid();
        public bool Busy, Accepted, Finished, Failed;
        public string Message = "Installation has not started.";
        public ServerInstallationRecord? Result;
    }
    private readonly Dictionary<Guid, InstallationJobView> installationJobs = [];
    private bool installationConsentOpen;
    private string installationInventoryMessage = "";

    private async Task LoadInstallationInventoryAsync()
    {
        if (client is null || lifetime.IsCancellationRequested) return;
        try
        {
            var inventory = await client.CallAsync<ServerInstallationInventory>("server-installation-inventory", null,
                TimeSpan.FromSeconds(8), lifetime.Token);
            if (inventory.Entries is null || inventory.Entries.Count > 1152 || inventory.UnreadableRecords < 0)
                throw new InvalidDataException("Invalid installation inventory.");
            foreach (var entry in inventory.Entries)
            {
                if (entry is null || entry.ServerId == Guid.Empty || !Enum.IsDefined(entry.Edition) || !Enum.IsDefined(entry.Platform)
                    || entry.Job is { } invalid && (invalid.Id == Guid.Empty || invalid.Kind != "server-installation" || !Enum.IsDefined(invalid.State)))
                    throw new InvalidDataException("Invalid installation entry.");
                if (entry.Record is { } saved && (saved.SchemaVersion != 1 || saved.ServerId != entry.ServerId
                    || saved.Edition != entry.Edition || saved.Platform != entry.Platform || !Enum.IsDefined(saved.Phase)
                    || saved.StagingDirectory != Path.Combine(managerRoot!, "servers", ".ark-create-" + entry.ServerId.ToString("N"))))
                    throw new InvalidDataException("Invalid recorded installation.");
                if (installationJobs.ContainsKey(entry.ServerId)) continue;
                var view = new InstallationJobView(new(entry.ServerId, entry.Edition, entry.Platform, true, entry.Record is not null),
                    entry.Edition + " setup", entry.Job?.Id);
                if (entry.Job is { } job)
                {
                    view.Accepted = true;
                    view.Message = job.Progress?.Message ?? "Reconnecting to recorded installation…";
                    installationJobs[entry.ServerId] = view;
                    _ = TrackInstallationAsync(view);
                }
                else
                {
                    view.Finished = true;
                    view.Failed = entry.Record?.Phase != ServerInstallationPhase.FilesReady;
                    view.Result = view.Failed ? null : entry.Record;
                    view.Message = view.Failed ? "An unfinished installation is recorded. Review it before resuming."
                        : "Installed files are recorded. Configuration and final server creation remain next.";
                    installationJobs[entry.ServerId] = view;
                }
            }
            installationInventoryMessage = inventory.UnreadableRecords > 0
                ? $"{inventory.UnreadableRecords} installation records could not be read. Their files remain untouched." : "";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or JsonException)
        { installationInventoryMessage = "Installation history could not be refreshed. Previously loaded details may be out of date."; }
    }

    private void AddInstallationInventory()
    {
        if (installationInventoryMessage.Length > 0) content.Children.Add(Label(installationInventoryMessage, 15));
        if (installationJobs.Count == 0) return;
        var body = new StackPanel { Spacing = 14 };
        foreach (var view in installationJobs.Values) body.Children.Add(InstallationJobCard(view));
        content.Children.Add(new Expander { Header = $"Server-file setups ({installationJobs.Count})", Content = body });
    }

    private Control InstallationJobCard(InstallationJobView view)
    {
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(Label("Server files · " + view.Name, 19, true));
        body.Children.Add(Label(view.Message, 15));
        if (view.Busy) body.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 4 });
        else if (!view.Finished) body.Children.Add(ActionButton("Reconnect to installation", () => _ = TrackInstallationAsync(view)));
        else if (view.Failed) body.Children.Add(ActionButton("Review installation", () => _ = ReviewFailedInstallationAsync(view)));
        if (view.Result is { } result)
        {
            body.Children.Add(ActionButton("Configure server", () =>
            {
                ResetPage(); ResetCreationDraft();
                creationServerId = result.ServerId; draftEdition = result.Edition;
                creationPage = true; Render();
            }, true));
            body.Children.Add(new Expander { Header = "Installed files", Content = Label(result.StagingDirectory, 14) });
        }
        return Surface(body);
    }

    private async Task ReviewFailedInstallationAsync(InstallationJobView view)
    {
        if (client is null || view.Busy || installationConsentOpen) return;
        view.Busy = true; Render();
        try
        {
            var status = await client.CallAsync<ServerInstallationStatus>("server-installation-record", view.Selection.ServerId,
                TimeSpan.FromSeconds(8), lifetime.Token);
            if (status.Record is { } record && (record.ServerId != view.Selection.ServerId || record.Edition != view.Selection.Edition
                || record.Platform != view.Selection.Platform)) throw new InvalidDataException("Recorded setup changed.");
            await ReviewInstallationAsync(view.Selection with { Resume = status.Record is not null }, view.Name);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or JsonException)
        { view.Message = "The recorded installation could not be checked. Reconnect and try again."; }
        finally { view.Busy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }

    private async Task ReviewInstallationAsync(ServerInstallationRequest selection, string name)
    {
        if (installationConsentOpen || client is null || lifetime.IsCancellationRequested) return;
        installationConsentOpen = true;
        try
        {
            var dialog = new Window { Title = selection.Resume ? "Resume server-file installation?" : "Install server files?",
                Width = 680, Height = 480, CanResize = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, FontFamily = FontFamily, Background = Brush.Parse("#0F1921") };
            var body = new StackPanel { Margin = new Thickness(32), Spacing = 22 };
            body.Children.Add(Label(dialog.Title!, 27, true));
            body.Children.Add(Label(name + " · " + selection.Edition, 19, true));
            body.Children.Add(Label("These server files are needed to run your server. Missing SteamCMD tools will be installed in Dependencies. The server package will be downloaded and checked.", 17));
            body.Children.Add(Label("You can install later. Once the files are ready, continue with configuration and create your server before starting it.", 16));
            if (selection.Resume) body.Children.Add(Label("The recorded partial download will be reused after the staged server and installer are verified as stopped.", 15));
            var actions = new WrapPanel();
            actions.Children.Add(ActionButton("Later", () => dialog.Close(false)));
            var install = ActionButton(selection.Resume ? "Resume installation" : "Install server files", () => dialog.Close(true), true);
            install.Margin = new Thickness(14, 0, 0, 0); actions.Children.Add(install); body.Children.Add(actions);
            dialog.Content = new ScrollViewer { Content = body };
            if (await dialog.ShowDialog<bool>(this) && !lifetime.IsCancellationRequested)
            {
                var view = new InstallationJobView(selection with { InstallationConsent = true }, name);
                installationJobs[selection.ServerId] = view;
                await TrackInstallationAsync(view);
            }
        }
        catch (InvalidOperationException) { connection.Text = "Installation review could not be opened. Please try again."; Render(); }
        finally { installationConsentOpen = false; }
    }

    private async Task TrackInstallationAsync(InstallationJobView view)
    {
        if (client is null || view.Busy || view.Finished || lifetime.IsCancellationRequested) return;
        view.Busy = true; view.Message = "Connecting to the background installer…"; Render();
        try
        {
            var job = await client.CallAsync<BackgroundOperation>(view.Accepted ? "operation-status" : "server-installation-start",
                view.Accepted ? new OperationStatusRequest(view.RequestId) : (object)view.Selection,
                TimeSpan.FromSeconds(8), lifetime.Token, view.Accepted ? null : view.RequestId);
            while (true)
            {
                if (job.Id != view.RequestId || job.Kind != "server-installation" || !Enum.IsDefined(job.State))
                    throw new InvalidDataException("Unexpected installation job.");
                view.Accepted = true;
                var message = job.Progress?.Message ?? "Installation is queued.";
                if (job.State == BackgroundOperationState.Succeeded)
                {
                    var result = job.Result?.Deserialize<ServerInstallationRecord>();
                    var expected = Path.Combine(managerRoot!, "servers", ".ark-create-" + view.Selection.ServerId.ToString("N"));
                    if (result is null || result.SchemaVersion != 1 || result.ServerId != view.Selection.ServerId
                        || result.Edition != view.Selection.Edition || result.Platform != view.Selection.Platform
                        || result.Phase != ServerInstallationPhase.FilesReady || result.StagingDirectory != expected)
                        throw new InvalidDataException("Installed-file result could not be verified.");
                    view.Result = result;
                    message = "Server files installed and checked. Configuration and server creation remain next.";
                    view.Finished = true;
                }
                else if (job.State is BackgroundOperationState.Failed or BackgroundOperationState.Canceled)
                {
                    view.Finished = view.Failed = true;
                    message = job.Error ?? "Installation was interrupted. Review the recorded setup before resuming.";
                }
                if (view.Message != message) { view.Message = message; Render(); }
                if (view.Finished) break;
                await Task.Delay(1000, lifetime.Token);
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(view.RequestId),
                    TimeSpan.FromSeconds(8), lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException
            or OperationCanceledException or System.Text.Json.JsonException)
        { view.Message = "Connection lost. Installation may still be running. Reconnect to check its result."; }
        finally { view.Busy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }
}
