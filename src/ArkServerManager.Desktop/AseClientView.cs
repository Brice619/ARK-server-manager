using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Text.Json;
namespace ArkServerManager.Desktop;
public sealed partial class MainWindow
{
    private bool aseClientBusy;
    private AseClientEdit? pendingAseClients;
    private Guid aseClientRequestId;
    private async Task EditAseClientsAsync(ServerDefinition server)
    {
        if (client is null || aseClientBusy) return;
        aseClientBusy = true;
        try
        {
            var reconnect = pendingAseClients is not null;
            if (!reconnect)
            {
                var dialog = new Window { Title = "Save player client access?", Width = 580, Height = 390,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush.Parse("#0F1921"), FontFamily = FontFamily };
                var panel = new StackPanel { Margin = new Thickness(28), Spacing = 18 };
                panel.Children.Add(Label(server.Settings.Name + " · ASE", 25, true));
                var picker = new ComboBox { ItemsSource = new[] { "Steam", "Steam and Epic Games", "Epic Games" }, SelectedIndex = (int)server.AseClients,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
                panel.Children.Add(picker);
                panel.Children.Add(Label("Changes apply on this map’s next start or restart. ASE Workshop mods require Steam clients. This does not restart the server.", 16));
                panel.Children.Add(ActionButton("Save for next start", () => dialog.Close(true), true));
                panel.Children.Add(ActionButton("Cancel", () => dialog.Close(false))); dialog.Content = new ScrollViewer { Content = panel };
                if (!await dialog.ShowDialog<bool>(this) || picker.SelectedIndex < 0) return;
                var desired = (AseClientAccess)picker.SelectedIndex;
                if (desired == server.AseClients) return;
                pendingAseClients = new(server.Id, server.Directory, server.AseClients, desired, true); aseClientRequestId = Guid.NewGuid();
            }
            Render();
            var job = reconnect
                ? await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(aseClientRequestId), TimeSpan.FromSeconds(8), lifetime.Token)
                : await client.CallAsync<BackgroundOperation>("ase-client-access-save-start", pendingAseClients, TimeSpan.FromSeconds(8), lifetime.Token, aseClientRequestId);
            while (true)
            {
                if (job.Id != aseClientRequestId || job.Kind != "ase-client-access-save" || !Enum.IsDefined(job.State)) throw new InvalidDataException();
                if (job.State == BackgroundOperationState.Succeeded)
                {
                    var result = job.Result?.Deserialize<AseClientEditResult>();
                    if (result is null || result.ServerId != pendingAseClients!.ServerId || result.Clients != pendingAseClients.Desired || !result.AppliesOnNextStart)
                        throw new InvalidDataException();
                    pendingAseClients = null; lifecycleNotice = "Player client access saved for the next start or restart."; await RefreshAsync(); return;
                }
                if (job.State is BackgroundOperationState.Failed or BackgroundOperationState.Canceled)
                { pendingAseClients = null; lifecycleNotice = job.Error ?? "Client access was not saved. Reload before trying again."; return; }
                await Task.Delay(500, lifetime.Token);
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(aseClientRequestId), TimeSpan.FromSeconds(8), lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsBasicSaveError(error))
        { lifecycleNotice = pendingAseClients is null ? "Client access could not be reviewed." : "Save outcome unknown. Reopen Player client access to check the original request."; }
        finally { aseClientBusy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }
}
