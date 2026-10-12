using Avalonia.Controls;
using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private List<ExistingServerImportStatus> unfinishedImports = [];
    private bool importRecoveryBusy;
    private string importRecoveryNotice = "";

    private async Task ReadImportRecoveryAsync()
    {
        if (client is null || designPreview) return;
        try
        {
            unfinishedImports = await client.CallAsync<List<ExistingServerImportStatus>>("existing-import-pending", null,
                TimeSpan.FromSeconds(8), lifetime.Token);
        }
        catch (Exception error) when (IsBasicSaveError(error))
        { importRecoveryNotice = "Unfinished imports could not be checked. Refresh to try again."; }
    }

    private void AddImportRecovery()
    {
        if (unfinishedImports.Count == 0 && importRecoveryNotice.Length == 0) return;
        var items = new StackPanel { Spacing = 16 };
        if (importRecoveryNotice.Length > 0) items.Children.Add(Label(importRecoveryNotice, 15));
        foreach (var item in unfinishedImports)
        {
            var row = new StackPanel { Spacing = 10 };
            row.Children.Add(Label(item.Name, 20, true));
            row.Children.Add(Label(item.Mode + " · " + item.Destination, 15));
            row.Children.Add(ActionButton("Finish import", importRecoveryBusy ? null : () => _ = FinishImportAsync(item), true));
            items.Children.Add(Surface(row));
        }
        content.Children.Add(new Expander { Header = "Unfinished imports (" + unfinishedImports.Count + ")", Content = items });
    }

    private async Task FinishImportAsync(ExistingServerImportStatus item)
    {
        if (client is null || importRecoveryBusy || designPreview) return;
        if (!await ConfirmImportRecoveryAsync("The app will check the recorded files and register " + item.Name
            + ". If the transfer was not published, it will retry from the unchanged original."
            + (item.Mode == InstallationImportMode.Move ? " A verified original or cleanup folder will be removed to finish the move." : " The original files will remain."))) return;
        importRecoveryBusy = true;
        try
        {
            var id = Guid.NewGuid();
            var job = await client.CallAsync<BackgroundOperation>("existing-import-recover-start", new ExistingServerImportRecoveryRequest(item.ServerId, true),
                TimeSpan.FromSeconds(8), lifetime.Token, id);
            while (job.State is BackgroundOperationState.Queued or BackgroundOperationState.Running)
            {
                importRecoveryNotice = job.Progress?.Message ?? "Finishing import…"; Render();
                await Task.Delay(500, lifetime.Token);
                job = (await client.CallAsync<OperationLookup>("operation-status-lookup", new OperationStatusRequest(id),
                    TimeSpan.FromSeconds(8), lifetime.Token)).Operation ?? throw new InvalidOperationException("The job result is unavailable. Refresh unfinished imports before trying again.");
            }
            if (job.State != BackgroundOperationState.Succeeded) throw new InvalidOperationException(job.Error ?? "Import recovery did not finish.");
            var result = job.Result?.Deserialize<ExistingServerTransferRegistrationResult>();
            if (result?.ServerId != item.ServerId || result.Directory != item.Destination) throw new InvalidDataException("Unexpected import recovery result.");
            importRecoveryNotice = result.Message; await RefreshAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsBasicSaveError(error)) { importRecoveryNotice = error.Message; await ReadImportRecoveryAsync(); }
        finally { importRecoveryBusy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }

    private Task<bool> ConfirmImportRecoveryAsync(string explanation)
    {
        var dialog = new Window { Title = "Finish import", Width = 540, Height = 390,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Avalonia.Media.Brush.Parse("#0F1921"), FontFamily = FontFamily };
        var body = new StackPanel { Margin = new Avalonia.Thickness(28), Spacing = 18 };
        body.Children.Add(Label("Finish this import?", 26, true)); body.Children.Add(Label(explanation, 16));
        body.Children.Add(ActionButton("Finish import", () => dialog.Close(true), true));
        body.Children.Add(ActionButton("Cancel", () => dialog.Close(false)));
        dialog.Content = new ScrollViewer { Content = body };
        return dialog.ShowDialog<bool>(this);
    }
}
