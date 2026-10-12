using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private sealed class BasicEditor
    {
        public BasicSettingsReview? Review;
        public BasicSettingsValues? Draft;
        public BasicSettingsEdit? Pending;
        public Guid RequestId;
        public bool Busy, Attempted;
        public string Notice = "Load current settings to edit this map.";
    }
    private readonly Dictionary<Guid, BasicEditor> basicEditors = [];

    private void AddBasicSettings(StackPanel body, ServerDefinition server, bool network)
    {
        if (!basicEditors.TryGetValue(server.Id, out var editor)) basicEditors[server.Id] = editor = new();
        var notice = Label(editor.Notice, 15); notice.TextWrapping = TextWrapping.Wrap; body.Children.Add(notice);
        if (editor.Review?.QueuedSettings is { } queued && editor.Pending is null)
        {
            var summary = Label(editor.Review.QueuedConfigurationChanged
                ? "Queued changes need review: the configuration changed since they were saved."
                : "Changes are queued for the next start.", 15);
            summary.TextWrapping = TextWrapping.Wrap; body.Children.Add(summary);
            var queueDetails = new StackPanel { Spacing = 10 };
            queueDetails.Children.Add(Label($"{queued.Name}\n{queued.MaxPlayers} players · Ports {queued.GamePort}, {queued.QueryPort}, {queued.RconPort}", 15));
            queueDetails.Children.Add(ActionButton("Use queued changes as draft", editor.Busy ? null : () =>
            {
                editor.Draft = queued;
                editor.Notice = "Queued changes loaded into the draft. Review General and Network, then save to confirm against the current configuration.";
                Render();
            }));
            body.Children.Add(new Expander { Header = "Review queued changes", Content = queueDetails });
        }
        if (editor.Draft is { } draft)
        {
            var enabled = !editor.Busy && editor.Pending is null;
            if (!network)
            {
                var name = new TextBox { Text = draft.Name, FontSize = 16, IsEnabled = enabled, MaxLength = 256 };
                name.TextChanged += (_, _) => editor.Draft = editor.Draft! with { Name = name.Text ?? "" };
                AddField("Server name", name);
                AddNumber("Maximum players", draft.MaxPlayers, int.MaxValue, value => editor.Draft = editor.Draft! with { MaxPlayers = value });
            }
            else
            {
                AddNumber("Game port", draft.GamePort, 65534, value => editor.Draft = editor.Draft! with { GamePort = value });
                AddNumber("Query port", draft.QueryPort, 65535, value => editor.Draft = editor.Draft! with { QueryPort = value });
                AddNumber("RCON port", draft.RconPort, 65535, value => editor.Draft = editor.Draft! with { RconPort = value });
            }
            void AddNumber(string label, int value, int maximum, Action<int> changed)
            {
                var field = new NumericUpDown { Value = value, Minimum = 1, Maximum = maximum, Increment = 1,
                    FormatString = "0", IsEnabled = enabled };
                field.ValueChanged += (_, _) =>
                {
                    if (field.Value is decimal number && number == decimal.Truncate(number)) changed((int)number);
                };
                AddField(label, field);
            }
            void AddField(string label, Control field)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("190,*"), MaxWidth = 880 };
                var title = Label(label, 16); title.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
                row.Children.Add(title); Grid.SetColumn(field, 1); row.Children.Add(field);
                Avalonia.Automation.AutomationProperties.SetName(field, label); body.Children.Add(row);
            }
            creationFooter.IsVisible = true;
            var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
            actions.Children.Add(ActionButton(editor.Pending is null ? "Save changes…" : "Check save result",
                client is null || editor.Busy ? null : () => _ = SaveBasicSettingsAsync(server, editor), true));
            if (editor.Pending is null) actions.Children.Add(ActionButton("Revert draft", editor.Busy ? null : () =>
            { editor.Draft = editor.Review!.Settings; editor.Notice = "Draft reverted to the loaded settings."; Render(); }));
            creationFooter.Children.Add(actions);
        }
        if (editor.Pending is null) body.Children.Add(ActionButton("Load current settings", client is null || editor.Busy ? null :
            () => _ = LoadBasicSettingsAsync(server, editor)));
        else if (!editor.Busy) body.Children.Add(ActionButton("Dismiss unknown save result", () =>
        {
            editor.Pending = null; editor.Review = null; editor.Draft = null;
            editor.Notice = "The save may still complete. Refresh server status and reload settings before another change."; Render();
        }));
    }

    private async Task LoadBasicSettingsAsync(ServerDefinition server, BasicEditor editor)
    {
        if (client is null || editor.Busy || editor.Pending is not null) return;
        editor.Busy = true; editor.Notice = "Loading current settings…"; Render();
        try
        {
            var review = await client.CallAsync<BasicSettingsReview>("server-basic-settings-read", server.Id, TimeSpan.FromSeconds(8), lifetime.Token);
            if (review.ServerId != server.Id || review.Settings is null) throw new InvalidDataException();
            BasicServerConfiguration.Apply("", review.Settings.ApplyTo(new()));
            editor.Review = review; editor.Draft = review.Settings; editor.Notice = "Changes apply only to this map. General and Network share this draft.";
        }
        catch (Exception error) when (IsBasicSaveError(error))
        { editor.Review = null; editor.Draft = null; editor.Notice = "Settings could not be loaded. Refresh and try again."; }
        finally { editor.Busy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }

    private async Task SaveBasicSettingsAsync(ServerDefinition server, BasicEditor editor)
    {
        if (client is null || editor.Busy || editor.Review is null || editor.Draft is null) return;
        editor.Busy = true;
        try
        {
            if (editor.Pending is null)
            {
                BasicServerConfiguration.Apply("", editor.Draft.ApplyTo(new()));
                var dialog = new Window { Title = "Save server settings?", Width = 570, Height = 480, CanResize = false,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush.Parse("#0F1921"), FontFamily = FontFamily };
                var panel = new StackPanel { Margin = new Thickness(28), Spacing = 16 };
                panel.Children.Add(Label("Save server settings?", 26, true));
                var details = Label($"{editor.Draft.Name}\n{editor.Draft.MaxPlayers} players\nPorts: {editor.Draft.GamePort}, {editor.Draft.QueryPort}, {editor.Draft.RconPort}\n\nIf stopped, changes are saved now. If running, choose when to apply them. Restart affects only this map.", 16);
                details.TextWrapping = TextWrapping.Wrap; panel.Children.Add(details);
                panel.Children.Add(ActionButton("Save · apply on next start if running", () => dialog.Close(SaveChoice.SaveForNextStart), true));
                panel.Children.Add(ActionButton("Save · restart this map if running", () => dialog.Close(SaveChoice.SaveAndRestart)));
                panel.Children.Add(ActionButton("Cancel", () => dialog.Close(SaveChoice.Cancel)));
                dialog.Content = new ScrollViewer { Content = panel };
                var choice = await dialog.ShowDialog<SaveChoice>(this);
                if (choice == SaveChoice.Cancel) return;
                editor.Pending = new(editor.Review, editor.Draft, choice, true);
                editor.RequestId = Guid.NewGuid(); editor.Attempted = false;
            }
            BackgroundOperation job;
            if (!editor.Attempted)
            {
                editor.Attempted = true;
                job = await client.CallAsync<BackgroundOperation>("server-basic-settings-save-start", editor.Pending,
                    TimeSpan.FromSeconds(8), lifetime.Token, editor.RequestId);
            }
            else job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(editor.RequestId), TimeSpan.FromSeconds(8), lifetime.Token);
            while (true)
            {
                if (job.Id != editor.RequestId || job.Kind != "server-basic-settings-save" || !Enum.IsDefined(job.State)) throw new InvalidDataException();
                if (job.State == BackgroundOperationState.Succeeded)
                {
                    var result = job.Result?.Deserialize<BasicSettingsEditResult>();
                    if (result is null || result.ServerId != server.Id || result.Save is null) throw new InvalidDataException();
                    editor.Notice = result.Save.Message; editor.Pending = null; editor.Review = null; editor.Draft = null;
                    await RefreshAsync(); break;
                }
                if (job.State is BackgroundOperationState.Failed or BackgroundOperationState.Canceled)
                {
                    editor.Notice = "Save did not complete. Refresh server status and reload settings before retrying.";
                    editor.Pending = null; editor.Review = null; editor.Draft = null; await RefreshAsync(); break;
                }
                editor.Notice = job.Progress?.Message ?? "Saving settings…"; Render();
                await Task.Delay(1000, lifetime.Token);
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(editor.RequestId), TimeSpan.FromSeconds(8), lifetime.Token);
            }
        }
        catch (Exception error) when (IsBasicSaveError(error))
        { editor.Notice = editor.Pending is null ? "Enter a name, whole-number player limit and distinct valid ports." : "Save outcome is unknown. Check the existing request before trying again."; }
        finally { editor.Busy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }

    private static bool IsBasicSaveError(Exception error) => error is IOException or TimeoutException or ArgumentException
        or InvalidOperationException or JsonException or OperationCanceledException;
}
