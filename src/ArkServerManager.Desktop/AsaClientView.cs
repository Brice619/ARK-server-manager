using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Text.Json;
namespace ArkServerManager.Desktop;
public sealed partial class MainWindow
{
    private bool asaClientBusy;
    private AsaClientEdit? pendingAsaClients;
    private Guid asaClientRequestId;
    private async Task EditAsaClientsAsync(ServerDefinition server)
    {
        if (client is null || asaClientBusy) return;
        asaClientBusy = true;
        try
        {
            var reconnect = pendingAsaClients is not null;
            if (!reconnect)
            {
                var dialog = new Window { Title = "Save player client access?", Width = 580, Height = 390,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush.Parse("#0F1921"), FontFamily = FontFamily };
                var panel = new StackPanel { Margin = new Thickness(28), Spacing = 18 };
                panel.Children.Add(Label(server.Settings.Name + " · ASA", 25, true));
                var choices = new List<(AsaClientPlatforms Platform, CheckBox Choice)>();
                foreach (var (platform, title) in new[] { (AsaClientPlatforms.PC, "PC clients"), (AsaClientPlatforms.WindowsStore, "Windows Store"),
                    (AsaClientPlatforms.XboxSeries, "Xbox Series X/S"), (AsaClientPlatforms.PlayStation5, "PlayStation 5") })
                {
                    var choice = new CheckBox { Content = title, IsChecked = server.AsaClients.HasFlag(platform) };
                    choices.Add((platform, choice)); panel.Children.Add(choice);
                }
                panel.Children.Add(Label("Changes apply on this map’s next start or restart. Selected mods must support your chosen platforms. This does not restart the server.", 16));
                panel.Children.Add(ActionButton("Save for next start", () => dialog.Close(true), true));
                panel.Children.Add(ActionButton("Cancel", () => dialog.Close(false))); dialog.Content = new ScrollViewer { Content = panel };
                if (!await dialog.ShowDialog<bool>(this)) return;
                var desired = choices.Where(item => item.Choice.IsChecked == true).Aggregate((AsaClientPlatforms)0, (selected, item) => selected | item.Platform);
                if (desired == 0) { lifecycleNotice = "Choose at least one player platform."; return; }
                if (desired == server.AsaClients) return;
                pendingAsaClients = new(server.Id, server.Directory, server.AsaClients, desired, true); asaClientRequestId = Guid.NewGuid();
            }
            Render();
            var job = reconnect
                ? await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(asaClientRequestId), TimeSpan.FromSeconds(8), lifetime.Token)
                : await client.CallAsync<BackgroundOperation>("asa-client-access-save-start", pendingAsaClients, TimeSpan.FromSeconds(8), lifetime.Token, asaClientRequestId);
            while (true)
            {
                if (job.Id != asaClientRequestId || job.Kind != "asa-client-access-save" || !Enum.IsDefined(job.State)) throw new InvalidDataException();
                if (job.State == BackgroundOperationState.Succeeded)
                {
                    var result = job.Result?.Deserialize<AsaClientEditResult>();
                    if (result is null || result.ServerId != pendingAsaClients!.ServerId || result.Clients != pendingAsaClients.Desired || !result.AppliesOnNextStart)
                        throw new InvalidDataException();
                    pendingAsaClients = null; lifecycleNotice = "Player client access saved for the next start or restart."; await RefreshAsync(); return;
                }
                if (job.State is BackgroundOperationState.Failed or BackgroundOperationState.Canceled)
                { pendingAsaClients = null; lifecycleNotice = job.Error ?? "Client access was not saved. Reload before trying again."; return; }
                await Task.Delay(500, lifetime.Token);
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(asaClientRequestId), TimeSpan.FromSeconds(8), lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsBasicSaveError(error))
        { lifecycleNotice = pendingAsaClients is null ? "Client access could not be reviewed." : "Save outcome unknown. Reopen Player client access to check the original request."; }
        finally { asaClientBusy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }
}

