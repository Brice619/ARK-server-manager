using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private sealed class AnnouncementViewState
    {
        public Guid RequestId;
        public bool Busy, Submitted;
        public ServerAnnouncementRequest? Pending;
        public string Notice = "";
    }
    private readonly Dictionary<Guid, AnnouncementViewState> announcements = [];

    private void RenderAnnouncementAction(ServerDefinition server)
    {
        if (!announcements.TryGetValue(server.Id, out var view)) announcements[server.Id] = view = new();
        content.Children.Add(ActionButton(view.Busy ? "Sending announcement…" : view.Pending is null ? "Send announcement" : "Check announcement result",
            client is null || view.Busy ? null : () => { _ = SendAnnouncementAsync(server, view); }));
        if (view.Pending is not null && view.Submitted && !view.Busy)
            content.Children.Add(ActionButton("Dismiss unknown result", () =>
            {
                view.Pending = null; view.Submitted = false;
                view.Notice = "Previous result dismissed. That announcement may already have reached the server. No message was resent.";
                Render();
            }));
        if (view.Notice.Length > 0)
        {
            var notice = Label(view.Notice, 14); notice.TextWrapping = TextWrapping.Wrap; notice.Foreground = Muted;
            content.Children.Add(notice);
        }
    }

    private async Task SendAnnouncementAsync(ServerDefinition server, AnnouncementViewState view)
    {
        if (client is null || view.Busy || lifetime.IsCancellationRequested) return;
        view.Busy = true;
        try
        {
            if (view.Pending is null)
            {
                var dialog = new Window { Title = "Send announcement", Width = 600, Height = 420, CanResize = false,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush.Parse("#0F1921"), FontFamily = FontFamily };
                var body = new StackPanel { Margin = new Thickness(30), Spacing = 18 };
                body.Children.Add(Label("Send an announcement", 27, true));
                var destination = Label("To players on " + server.Settings.Name, 16); destination.TextWrapping = TextWrapping.Wrap;
                body.Children.Add(destination);
                var message = new TextBox { PlaceholderText = "Announcement message", MaxLength = 1024,
                    FontSize = 16, Padding = new Thickness(12), AcceptsReturn = false };
                Avalonia.Automation.AutomationProperties.SetName(message, "Announcement message"); body.Children.Add(message);
                var validation = Label("Review your message before sending.", 14); validation.TextWrapping = TextWrapping.Wrap; body.Children.Add(validation);
                body.Children.Add(ActionButton("Send to this server", () =>
                {
                    try { AnnouncementManagerOperations.ValidateMessage(message.Text ?? ""); dialog.Close(true); }
                    catch (ArgumentException) { validation.Text = "Enter a single-line message of up to 1024 characters."; }
                }, true));
                body.Children.Add(ActionButton("Cancel", () => dialog.Close(false))); dialog.Content = body;
                if (!await dialog.ShowDialog<bool>(this)) return;
                view.Pending = new(server.Id, message.Text!, true); view.RequestId = Guid.NewGuid(); view.Submitted = false;
            }
            BackgroundOperation job;
            if (!view.Submitted)
            {
                // Once a send is attempted, reconnection only queries the job; it never resends automatically.
                view.Submitted = true;
                job = await client.CallAsync<BackgroundOperation>("server-announcement-start", view.Pending,
                    TimeSpan.FromSeconds(8), lifetime.Token, view.RequestId);
            }
            else job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(view.RequestId),
                TimeSpan.FromSeconds(8), lifetime.Token);
            while (true)
            {
                if (job.Id != view.RequestId || job.Kind != "server-announcement" || !Enum.IsDefined(job.State)) throw new InvalidDataException();
                if (job.State == BackgroundOperationState.Succeeded)
                {
                    var result = job.Result?.Deserialize<ServerAnnouncementResult>();
                    if (result is null || result.ServerId != server.Id || result.ResponseReceivedAt == default) throw new InvalidDataException();
                    view.Pending = null; view.Notice = "Announcement request sent; the server responded. Player delivery is not confirmed."; break;
                }
                if (job.State is BackgroundOperationState.Failed or BackgroundOperationState.Canceled)
                { view.Pending = null; view.Notice = "Announcement did not complete. It may have reached the server; check before sending again."; break; }
                view.Notice = "Waiting for the server's response…"; Render();
                await Task.Delay(500, lifetime.Token);
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(view.RequestId), TimeSpan.FromSeconds(8), lifetime.Token);
            }
        }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or ArgumentException
            or JsonException or OperationCanceledException)
        { view.Notice = "The announcement result is unknown. Check its result before sending another message; it will not be resent automatically."; }
        finally { view.Busy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }
}
