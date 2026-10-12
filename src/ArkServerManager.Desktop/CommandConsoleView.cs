using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private bool reviewCommandConsole;
    private sealed class CommandConsoleState
    {
        public string Draft = "", Notice = "Enter a command to query or administer this server.";
        public bool Busy, Attempted;
        public Guid RequestId;
        public ServerConsoleCommand? Pending;
        public List<ServerConsoleResponse> Responses = [];
        public IReadOnlyList<string> LogLines = [];
        public bool ReadingLogs;
        public string LogNotice = "Waiting for server logs…";
        public Action? RefreshOutput;
    }
    private readonly Dictionary<Guid, CommandConsoleState> commandConsoles = [];
    private Avalonia.Threading.DispatcherTimer? consoleLogTimer;

    private void RenderCommandConsole(ServerDefinition server)
    {
        if (!commandConsoles.TryGetValue(server.Id, out var view)) commandConsoles[server.Id] = view = new();
        if (client is not null && consoleLogTimer is null)
        {
            consoleLogTimer = new() { Interval = TimeSpan.FromSeconds(3) };
            consoleLogTimer.Tick += (_, _) =>
            {
                if (lifetime.IsCancellationRequested) { consoleLogTimer.Stop(); return; }
                if (!consolePaused && managementTab == 5 && selectedServer is Guid id && catalog?.Servers.SingleOrDefault(s => s.Id == id) is { } selected
                    && commandConsoles.TryGetValue(id, out var state)) _ = RefreshConsoleLogsAsync(selected, state);
            };
            consoleLogTimer.Start();
            _ = RefreshConsoleLogsAsync(server, view);
        }
        if (Height >= 760)
        {
            content.Children.Add(Label("Console", 32, true));
            content.Children.Add(Label("Administrator commands and server responses.", 17));
        }
        var compact = Height < 760;
        var body = new StackPanel { Spacing = compact ? 10 : 16 };
        var toolbar = new WrapPanel();
        var clear = ActionButton("Clear view", () => { view.Responses.Clear(); Render(); });
        if (compact) clear.Padding = new Thickness(12, 8);
        toolbar.Children.Add(clear);
        toolbar.Children.Add(ActionButton(consolePaused ? "Resume logs" : "Pause logs", () => { consolePaused = !consolePaused; Render(); }));
        toolbar.Children.Add(ActionButton("Refresh logs", client is null ? null : () => _ = RefreshConsoleLogsAsync(server, view)));
        var search = new TextBox { Text = consoleFilter, PlaceholderText = "Find response text…", Width = 360,
            FontSize = 16, Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(12, compact ? 6 : 10) };
        toolbar.Children.Add(search); body.Children.Add(toolbar);
        var output = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas, monospace"), FontSize = 15, MinHeight = Height < 760 ? 150 : 250,
            MaxHeight = 400, Background = Brush.Parse("#0C1720"), Padding = new Thickness(compact ? 10 : 18), Foreground = Brush.Parse("#D4E2ED") };
        Avalonia.Automation.AutomationProperties.SetName(output, "Administrator command responses");
        void Filter() => output.Text = string.Join(Environment.NewLine, view.LogLines.Where(line => line.Contains(consoleFilter, StringComparison.OrdinalIgnoreCase)))
            + Environment.NewLine + view.LogNotice + Environment.NewLine
            + (view.Responses.Count == 0 ? "No command responses yet."
            : string.Join(Environment.NewLine + Environment.NewLine, view.Responses
                .Where(r => r.Response.Contains(consoleFilter, StringComparison.OrdinalIgnoreCase))
                .Select(r => $"[{r.ReceivedAt.LocalDateTime:T}] " + (r.Response.Length == 0 ? "Server returned an empty response." : r.Response))));
        view.RefreshOutput = Filter;
        search.TextChanged += (_, _) => { consoleFilter = search.Text ?? ""; Filter(); }; Filter(); body.Children.Add(output);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var command = new TextBox { Text = view.Draft, PlaceholderText = "Enter an admin command…", MaxLength = 2048,
            IsEnabled = !view.Busy && view.Pending is null, FontSize = 16, Padding = new Thickness(12, 10) };
        Avalonia.Automation.AutomationProperties.SetName(command, "Administrator command");
        command.TextChanged += (_, _) => view.Draft = command.Text ?? "";
        row.Children.Add(command);
        var send = ActionButton(view.Busy ? "Sending…" : view.Pending is null ? "Send" : "Check result",
            client is null || view.Busy ? null : () => { _ = SendConsoleCommandAsync(server, view); });
        send.Margin = new Thickness(12, 0, 0, 0); Grid.SetColumn(send, 1); row.Children.Add(send);
        creationFooter.IsVisible = true; creationFooter.Children.Add(row);
        if (view.Pending is not null && !view.Busy && view.Attempted)
            creationFooter.Children.Add(ActionButton("Dismiss unknown result", () =>
            { view.Pending = null; view.Attempted = false; view.Notice = "Result dismissed. The command may already have run. Nothing was resent."; Render(); }));
        var note = Label(view.Notice, 14); note.TextWrapping = TextWrapping.Wrap; note.Foreground = Muted; creationFooter.Children.Add(note);
        var scope = Label("Recent logs refresh every three seconds while this tab is open. Clear view removes command responses; log files are kept.", 13);
        scope.TextWrapping = TextWrapping.Wrap; scope.Foreground = Muted; creationFooter.Children.Add(scope);
        content.Children.Add(Surface(body, compact ? new Thickness(12) : null));
    }

    private async Task RefreshConsoleLogsAsync(ServerDefinition server, CommandConsoleState view)
    {
        if (client is null || view.ReadingLogs || lifetime.IsCancellationRequested) return;
        view.ReadingLogs = true;
        try
        {
            var result = await client.CallAsync<ServerLogTailResult>("server-log-tail", server.Id, TimeSpan.FromSeconds(8), lifetime.Token);
            if (result.ServerId != server.Id || result.Lines is null || result.Lines.Count > 500) throw new InvalidDataException();
            view.LogLines = result.Lines; view.LogNotice = result.Lines.Count == 0 ? "No server log output yet." : "";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsBasicSaveError(error)) { view.LogNotice = "Server logs could not be refreshed. Previously read lines are retained."; }
        finally { view.ReadingLogs = false; if (!lifetime.IsCancellationRequested) view.RefreshOutput?.Invoke(); }
    }

    private async Task SendConsoleCommandAsync(ServerDefinition server, CommandConsoleState view)
    {
        if (client is null || view.Busy || lifetime.IsCancellationRequested) return;
        view.Busy = true;
        try
        {
            if (view.Pending is null)
            {
                ConsoleCommandManagerOperations.ValidateCommand(view.Draft);
                view.Pending = new(server.Id, view.Draft); view.RequestId = Guid.NewGuid(); view.Attempted = false;
            }
            BackgroundOperation job;
            if (!view.Attempted)
            {
                view.Attempted = true;
                job = await client.CallAsync<BackgroundOperation>("server-console-command-start", view.Pending,
                    TimeSpan.FromSeconds(8), lifetime.Token, view.RequestId);
            }
            else job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(view.RequestId), TimeSpan.FromSeconds(8), lifetime.Token);
            while (true)
            {
                if (job.Id != view.RequestId || job.Kind != "server-console-command" || !Enum.IsDefined(job.State)) throw new InvalidDataException();
                if (job.State == BackgroundOperationState.Succeeded)
                {
                    var response = job.Result?.Deserialize<ServerConsoleResponse>();
                    if (response is null || response.ServerId != server.Id || response.ReceivedAt == default || response.Response is null
                        || response.Response.Length > RconProtocol.MaximumPacketSize) throw new InvalidDataException();
                    view.Responses.Add(response); if (view.Responses.Count > 100) view.Responses.RemoveAt(0);
                    view.Pending = null; view.Draft = ""; view.Notice = "Server response received. A response does not prove the command succeeded."; break;
                }
                if (job.State is BackgroundOperationState.Failed or BackgroundOperationState.Canceled)
                { view.Pending = null; view.Notice = "Command did not complete. It may have run; check the server before trying again."; break; }
                view.Notice = "Waiting for the server response…"; Render();
                await Task.Delay(500, lifetime.Token);
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(view.RequestId), TimeSpan.FromSeconds(8), lifetime.Token);
            }
        }
        catch (ArgumentException) when (view.Pending is null)
        { view.Notice = "Enter one command without line breaks, within the server's command size limit."; }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or ArgumentException
            or JsonException or OperationCanceledException)
        { view.Notice = "Command result unknown. Check the result before sending another command; it will not be resent automatically."; }
        finally { view.Busy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }
}
