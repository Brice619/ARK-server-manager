using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Globalization;
using System.Text.Json;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private sealed class WelcomeEditor
    {
        public string? Hash;
        public string Message = "", Notice = "Load the server's welcome settings to edit them.";
        public decimal? Duration;
        public bool Busy, Accepted, Expanded;
        public Guid RequestId;
        public ServerWelcomeEdit? Pending;
    }
    private readonly Dictionary<Guid, WelcomeEditor> welcomeEditors = [];

    private void AddWelcomeSettings(StackPanel body, ServerDefinition server)
    {
        if (!welcomeEditors.TryGetValue(server.Id, out var editor)) welcomeEditors[server.Id] = editor = new();
        var panel = new StackPanel { Spacing = 14 };
        panel.Children.Add(Label(editor.Notice, 15));
        if (editor.Hash is not null)
        {
            var message = new TextBox { Text = editor.Message, FontSize = 16, IsEnabled = !editor.Busy && editor.Pending is null };
            message.TextChanged += (_, _) => editor.Message = message.Text ?? "";
            panel.Children.Add(DraftField("Welcome message", message));
            var duration = new NumericUpDown { Value = editor.Duration, Minimum = 1, Maximum = int.MaxValue,
                Increment = 1, FormatString = "0", PlaceholderText = "ARK default", IsEnabled = !editor.Busy && editor.Pending is null };
            duration.ValueChanged += (_, _) => editor.Duration = duration.Value;
            panel.Children.Add(DraftField("Display duration (seconds)", duration, "Leave empty to use ARK’s default."));
            panel.Children.Add(ActionButton(editor.Pending is null ? "Save welcome settings…" : "Reconnect to save",
                client is null || editor.Busy ? null : () => _ = SaveWelcomeAsync(server, editor), true));
        }
        if (editor.Pending is null) panel.Children.Add(ActionButton("Load current welcome settings",
            client is null || editor.Busy ? null : () => _ = LoadWelcomeAsync(server, editor)));
        var section = new Expander { Header = "Welcome message", Content = panel, IsExpanded = editor.Expanded,
            Background = Brush.Parse("#101C26"), BorderBrush = Brush.Parse("#2B3A46"), Foreground = Brushes.White };
        section.PropertyChanged += (_, change) =>
        { if (change.Property == Expander.IsExpandedProperty) editor.Expanded = section.IsExpanded; };
        body.Children.Add(section);
    }

    private async Task LoadWelcomeAsync(ServerDefinition server, WelcomeEditor editor)
    {
        if (client is null || editor.Busy || editor.Pending is not null) return;
        editor.Busy = true; editor.Notice = "Loading welcome settings…"; Render();
        try
        {
            var file = await client.CallAsync<ServerConfigurationFile>("server-configuration-read",
                new ServerConfigurationReadRequest(server.Id, "GameUserSettings.ini"), TimeSpan.FromSeconds(8), lifetime.Token);
            if (file.ServerId != server.Id || file.Text is null || file.SourceSHA256 is null) throw new InvalidDataException();
            var ini = new IniDocument(file.Text);
            var messages = ini.GetValues("MessageOfTheDay", "Message"); var durations = ini.GetValues("MessageOfTheDay", "Duration");
            if (messages.Count > 1 || durations.Count > 1) throw new InvalidDataException();
            int? seconds = null;
            if (durations.Count == 1)
            {
                if (!int.TryParse(durations[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
                    throw new InvalidDataException();
                seconds = parsed;
            }
            editor.Hash = file.SourceSHA256; editor.Message = messages.SingleOrDefault() ?? ""; editor.Duration = seconds;
            editor.Notice = "Changes apply to this map. Stop it before saving.";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or ArgumentException or InvalidOperationException or JsonException or OperationCanceledException)
        { editor.Hash = null; editor.Notice = "Welcome settings could not be loaded. Review the configuration and retry."; }
        finally { editor.Busy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }

    private async Task SaveWelcomeAsync(ServerDefinition server, WelcomeEditor editor)
    {
        if (client is null || editor.Busy || editor.Hash is null) return;
        editor.Busy = true;
        try
        {
            if (editor.Pending is null)
            {
                if (editor.Duration is decimal seconds && (seconds < 1 || seconds > int.MaxValue || seconds != decimal.Truncate(seconds)))
                    throw new ArgumentException();
                var duration = editor.Duration is decimal value ? (int?)value : null;
                ServerWelcomeConfiguration.Apply("", editor.Message, duration);
                var dialog = new Window { Title = "Save welcome settings?", Width = 560, Height = 320, CanResize = false,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush.Parse("#0F1921"), FontFamily = FontFamily };
                var content = new StackPanel { Spacing = 20, Margin = new Thickness(30) };
                content.Children.Add(Label("Save welcome settings?", 26, true));
                content.Children.Add(Label($"{server.Settings.Name}\nChanges apply to this map on its next start. It must be stopped before saving.", 16));
                content.Children.Add(ActionButton("Save changes", () => dialog.Close(true), true));
                content.Children.Add(ActionButton("Cancel", () => dialog.Close(false))); dialog.Content = content;
                if (!await dialog.ShowDialog<bool>(this)) return;
                editor.Pending = new(server.Id, editor.Hash, editor.Message, duration, true); editor.RequestId = Guid.NewGuid();
            }
            var job = await client.CallAsync<BackgroundOperation>(editor.Accepted ? "operation-status" : "server-welcome-save-start",
                editor.Accepted ? new OperationStatusRequest(editor.RequestId) : (object)editor.Pending,
                TimeSpan.FromSeconds(8), lifetime.Token, editor.Accepted ? null : editor.RequestId);
            while (true)
            {
                if (job.Id != editor.RequestId || job.Kind != "server-welcome-save" || !Enum.IsDefined(job.State)) throw new InvalidDataException();
                editor.Accepted = true;
                if (job.State == BackgroundOperationState.Succeeded)
                {
                    var result = job.Result?.Deserialize<ServerWelcomeEditResult>();
                    if (result is null || result.ServerId != server.Id || !result.Saved) throw new InvalidDataException();
                    editor.Pending = null; editor.Accepted = false; editor.Hash = null; editor.Notice = "Welcome settings saved. Load them again to make further changes."; break;
                }
                if (job.State is BackgroundOperationState.Failed or BackgroundOperationState.Canceled)
                { editor.Pending = null; editor.Accepted = false; editor.Hash = null; editor.Notice = "Save did not complete. Stop the map and reload its settings before retrying."; break; }
                editor.Notice = "Saving welcome settings…"; Render(); await Task.Delay(1000, lifetime.Token);
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(editor.RequestId), TimeSpan.FromSeconds(8), lifetime.Token);
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or ArgumentException or InvalidOperationException or JsonException or OperationCanceledException)
        { editor.Notice = editor.Pending is null ? "Choose a valid message and whole-number duration." : "Connection lost. Reconnect to check whether your settings were saved."; }
        finally { editor.Busy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }
}
