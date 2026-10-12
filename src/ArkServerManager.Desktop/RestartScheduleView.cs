using Avalonia.Controls;
using ArkServerManager.Core;
using System.Globalization;
using System.Text.Json;

namespace ArkServerManager.Desktop;
public sealed partial class MainWindow
{
    private Guid? scheduleServer;
    private sealed class ScheduleEditor
    {
        public bool Loaded, Enabled, Busy, Attempted;
        public string Time = "04:00", Notice = "Load the current schedule to edit it.";
        public DailyRestartSave? Pending;
        public Guid RequestId;
    }
    private readonly Dictionary<Guid, ScheduleEditor> scheduleEditors = [];
    private void RenderRestartSchedule(ServerDefinition server)
    {
        if (!scheduleEditors.TryGetValue(server.Id, out var view)) scheduleEditors[server.Id] = view = new();
        content.Children.Add(ActionButton("← Back to settings", () => { scheduleServer = null; Render(); }));
        content.Children.Add(Label("Restart schedule", 28, true));
        content.Children.Add(Label(server.Settings.Name + " · Only this map", 17));
        var body = new StackPanel { Spacing = 16 };
        body.Children.Add(Label("Daily, using this computer's local time. Stopped maps stay stopped. Missed times are skipped.", 16));
        body.Children.Add(Label(view.Notice, 15));
        if (view.Loaded)
        {
            var enabled = new CheckBox { Content = "Enable daily restart", IsChecked = view.Enabled, IsEnabled = !view.Busy && view.Pending is null };
            enabled.IsCheckedChanged += (_, _) => view.Enabled = enabled.IsChecked == true; body.Children.Add(enabled);
            var time = new TextBox { Text = view.Time, PlaceholderText = "HH:mm", MaxLength = 5, Width = 140, IsEnabled = !view.Busy && view.Pending is null };
            time.TextChanged += (_, _) => view.Time = time.Text ?? "";
            body.Children.Add(DraftField("Restart time · 24-hour clock", time, "Example: 04:00"));
            creationFooter.IsVisible = true;
            creationFooter.Children.Add(ActionButton(view.Pending is null ? "Save schedule…" : "Check existing save", client is null || view.Busy ? null : () => _ = SaveRestartScheduleAsync(server, view), true));
        }
        if (view.Pending is null) body.Children.Add(ActionButton("Load current schedule", client is null || view.Busy ? null : () => _ = LoadRestartScheduleAsync(server, view)));
        else if (!view.Busy) body.Children.Add(ActionButton("Dismiss unknown result", () =>
        { view.Pending = null; view.Loaded = false; view.Notice = "The save may still finish. Reload the current schedule before editing."; Render(); }));
        content.Children.Add(Surface(body));
    }
    private async Task LoadRestartScheduleAsync(ServerDefinition server, ScheduleEditor view)
    {
        if (client is null || view.Busy || view.Pending is not null) return;
        view.Busy = true; Render();
        try
        {
            var result = await client.CallAsync<DailyRestartSchedule>("server-restart-schedule-read", server.Id, TimeSpan.FromSeconds(8), lifetime.Token);
            if (result.ServerId != server.Id || result.Directory != server.Directory || result.Edition != server.Edition) throw new InvalidDataException();
            view.Enabled = result.Enabled; view.Time = result.Time.ToString("HH:mm", CultureInfo.InvariantCulture); view.Loaded = true;
            view.Notice = "Saving changes the schedule immediately; it does not restart the map now.";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsBasicSaveError(error)) { view.Notice = "The schedule could not be loaded. Try again."; }
        finally { view.Busy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }
    private async Task SaveRestartScheduleAsync(ServerDefinition server, ScheduleEditor view)
    {
        if (client is null || view.Busy || !view.Loaded) return;
        view.Busy = true;
        try
        {
            if (view.Pending is null)
            {
                if (!TimeOnly.TryParseExact(view.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                { view.Notice = "Enter a time such as 04:00."; return; }
                var dialog = new Window { Title = "Save restart schedule?", Width = 500, Height = 280, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                var panel = new StackPanel { Spacing = 16, Margin = new Avalonia.Thickness(24) };
                panel.Children.Add(Label(view.Enabled ? "Restart this running map daily at " + view.Time + "?" : "Disable this map's daily restart?", 18));
                panel.Children.Add(ActionButton("Save schedule", () => dialog.Close(true), true)); panel.Children.Add(ActionButton("Cancel", () => dialog.Close(false)));
                dialog.Content = panel; if (!await dialog.ShowDialog<bool>(this)) return;
                view.Pending = new(server.Id, time, view.Enabled, true); view.RequestId = Guid.NewGuid(); view.Attempted = false;
            }
            Render();
            BackgroundOperation job;
            if (!view.Attempted)
            {
                view.Attempted = true;
                job = await client.CallAsync<BackgroundOperation>("server-restart-schedule-save-start", view.Pending, TimeSpan.FromSeconds(8), lifetime.Token, view.RequestId);
            }
            else job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(view.RequestId), TimeSpan.FromSeconds(8), lifetime.Token);
            while (true)
            {
                if (job.Id != view.RequestId || job.Kind != "server-restart-schedule") throw new InvalidDataException();
                if (job.State == BackgroundOperationState.Succeeded)
                {
                    var result = job.Result?.Deserialize<DailyRestartSchedule>();
                    if (result is null || result.ServerId != server.Id) throw new InvalidDataException();
                    view.Pending = null; view.Enabled = result.Enabled; view.Time = result.Time.ToString("HH:mm", CultureInfo.InvariantCulture); view.Notice = "Restart schedule saved."; break;
                }
                if (job.State is BackgroundOperationState.Failed or BackgroundOperationState.Canceled)
                { view.Pending = null; view.Loaded = false; view.Notice = "Save failed. Reload the schedule before editing."; break; }
                await Task.Delay(500, lifetime.Token);
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(view.RequestId), TimeSpan.FromSeconds(8), lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsBasicSaveError(error)) { view.Notice = "Save outcome unknown. Check the existing save before trying again."; }
        finally { view.Busy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }
}
