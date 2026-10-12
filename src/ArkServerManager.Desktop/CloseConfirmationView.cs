using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private bool closeApproved;
    private bool closeConfirmationOpen;
    private Guid? closeShutdownId;
    private string closeShutdownNotice = "";

    private async void ConfirmWindowClose(object? sender, WindowClosingEventArgs e)
    {
        if (closeApproved || e.CloseReason is WindowCloseReason.OSShutdown or WindowCloseReason.ApplicationShutdown
            or WindowCloseReason.OwnerWindowClosing) return;
        e.Cancel = true;
        if (closeConfirmationOpen) return;
        closeConfirmationOpen = true;
        try
        {
            var dialog = new Window { Title = "Close ARK Server Manager?", Width = 620, Height = 460,
                MinWidth = 540, MinHeight = 340, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Brush.Parse("#0F1921"), FontFamily = FontFamily, ShowInTaskbar = false };
            var body = new StackPanel { Spacing = 16, Margin = new Thickness(26), VerticalAlignment = VerticalAlignment.Center };
            var title = Label("Keep your servers running?", 28, true); title.TextAlignment = TextAlignment.Center;
            body.Children.Add(title);
            var explanation = Label(designPreview ? "Design preview · No servers are running. Closing ends this preview session."
                : "Keep running leaves your background manager active. Stop servers waits for confirmed shutdown before closing.", 17);
            explanation.TextWrapping = TextWrapping.Wrap;
            explanation.TextAlignment = TextAlignment.Center; explanation.Foreground = Muted; body.Children.Add(explanation);
            var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var keep = ActionButton(designPreview ? "Close preview" : closeShutdownId is not null ? "Close window" : "Keep running and close", () => dialog.Close(true), true);
            keep.Margin = new Thickness(0, 0, 16, 12); actions.Children.Add(keep);
            var stop = ActionButton(closeShutdownId is null ? "Stop servers and close" : "Check shutdown result");
            stop.IsEnabled = !designPreview && client is not null; actions.Children.Add(stop); body.Children.Add(actions);
            var pending = Label(closeShutdownNotice.Length > 0 ? closeShutdownNotice : "A failed or unconfirmed shutdown keeps this window open.", 14);
            pending.TextWrapping = TextWrapping.Wrap;
            pending.TextAlignment = TextAlignment.Center; pending.Foreground = Muted; body.Children.Add(pending);
            var cancel = ActionButton("Cancel", () => dialog.Close(false)); cancel.HorizontalAlignment = HorizontalAlignment.Center;
            var shutdownChecking = false;
            dialog.Closing += (_, closing) =>
            {
                if (shutdownChecking && closing.CloseReason == WindowCloseReason.WindowClosing) closing.Cancel = true;
            };
            stop.Click += async (_, _) =>
            {
                shutdownChecking = true;
                keep.IsEnabled = stop.IsEnabled = cancel.IsEnabled = false;
                try
                {
                    if (await StopServersForCloseAsync(message => pending.Text = message))
                    { shutdownChecking = false; dialog.Close(true); }
                    else
                    {
                        stop.Content = closeShutdownId is null ? "Stop servers and close" : "Check shutdown result";
                        keep.Content = closeShutdownId is null ? "Keep running and close" : "Close window";
                    }
                }
                finally { shutdownChecking = false; keep.IsEnabled = stop.IsEnabled = cancel.IsEnabled = true; }
            };
            body.Children.Add(cancel); dialog.Content = new ScrollViewer { Content = body,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            if (await dialog.ShowDialog<bool>(this)) { closeApproved = true; Close(); }
        }
        catch (InvalidOperationException)
        { connection.Text = "The close confirmation could not be shown. Please try closing the window again."; }
        finally { closeConfirmationOpen = false; }
    }

    private async Task<bool> StopServersForCloseAsync(Action<string> report)
    {
        if (client is null) return false;
        try
        {
            BackgroundOperation job;
            if (closeShutdownId is null)
            {
                closeShutdownId = Guid.NewGuid();
                job = await client.CallAsync<BackgroundOperation>("lifecycle-stop-all", null, TimeSpan.FromSeconds(8), lifetime.Token, closeShutdownId);
            }
            else job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(closeShutdownId.Value), TimeSpan.FromSeconds(8), lifetime.Token);
            while (true)
            {
                if (job.Id != closeShutdownId || job.Kind != "server-shutdown" || !Enum.IsDefined(job.State)) throw new InvalidDataException();
                if (job.State == BackgroundOperationState.Succeeded)
                {
                    var result = job.Result?.Deserialize<LifecycleOperationResult>();
                    if (result?.Members is null || result.Members.Any(m => m is null || !Enum.IsDefined(m.State))) throw new InvalidDataException();
                    closeShutdownId = null;
                    if (result.Success) return true;
                    var failures = result.Members.Where(m => !m.Success).ToArray();
                    closeShutdownNotice = $"{failures.Length} server(s) did not confirm shutdown. This window stays open. "
                        + string.Join(" ", failures.Take(3).Select(m => (m.Error ?? "Check server status.")[..Math.Min((m.Error ?? "Check server status.").Length, 256)]));
                    report(closeShutdownNotice); return false;
                }
                if (job.State is BackgroundOperationState.Failed or BackgroundOperationState.Canceled)
                {
                    closeShutdownId = null; closeShutdownNotice = "Shutdown did not complete. Check server status before trying again.";
                    report(closeShutdownNotice); return false;
                }
                report(job.Progress?.Message ?? "Saving and stopping servers…");
                await Task.Delay(1000, lifetime.Token);
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(closeShutdownId.Value), TimeSpan.FromSeconds(8), lifetime.Token);
            }
        }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or ArgumentException or JsonException or OperationCanceledException)
        {
            closeShutdownNotice = "Shutdown result unknown. The manager may still be stopping servers. Check this request to reconnect; it will not be sent again automatically.";
            report(closeShutdownNotice); return false;
        }
    }
}
