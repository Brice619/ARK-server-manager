using ArkServerManager.Core;
using System.Text.Json;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
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
