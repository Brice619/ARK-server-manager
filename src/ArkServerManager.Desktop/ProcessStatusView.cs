using Avalonia.Controls;
using Avalonia.Media;
using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private readonly List<(StackPanel Line, Func<Control> Resolve)> overviewStatusLabels = [];

    private Control ObservedProcessStatus(Guid? serverId = null, Guid? clusterId = null)
    {
        var value = BuildObservedProcessStatus(serverId, clusterId);
        if (value is StackPanel line) overviewStatusLabels.Add((line, () => BuildObservedProcessStatus(serverId, clusterId)));
        return value;
    }

    private async Task ObserveOverviewEntryAsync()
    {
        if (client is null || selectedServer == ReviewServerId || managementTab != 0 || lifetime.IsCancellationRequested) return;
        var server = selectedServer; var cluster = selectedCluster;
        if (server is null && cluster is null) return;
        try
        {
            var latest = await client.CallAsync<RegisteredProcessStatus>("server-process-status", null, TimeSpan.FromSeconds(2), lifetime.Token);
            if (latest.MeasuredAt == default || latest.Servers is null || latest.Servers.Any(s => !Enum.IsDefined(s.State) || s.ProcessCount < 0)
                || latest.Servers.Select(s => s.ServerId).Distinct().Count() != latest.Servers.Count) return;
            processStatus = latest;
            await RefreshLifecycleDisplayAsync();
            if (selectedServer != server || selectedCluster != cluster || managementTab != 0 || lifetime.IsCancellationRequested) return;
            foreach (var binding in overviewStatusLabels.ToArray())
            {
                if (binding.Resolve() is not StackPanel fresh || fresh.Children.LastOrDefault() is not TextBlock source
                    || binding.Line.Children.LastOrDefault() is not TextBlock destination) continue;
                destination.Text = source.Text; destination.Foreground = source.Foreground;
                if (binding.Line.Children.FirstOrDefault() is Border dot && fresh.Children.FirstOrDefault() is Border freshDot) dot.Background = freshDot.Background;
                ToolTip.SetTip(binding.Line, ToolTip.GetTip(fresh));
                if (binding.Line.Parent is Border badge && source.Foreground is ISolidColorBrush brush)
                {
                    var color = brush.Color;
                    badge.Background = new SolidColorBrush(Color.FromArgb(35, color.R, color.G, color.B));
                    badge.BorderBrush = new SolidColorBrush(Color.FromArgb(115, color.R, color.G, color.B));
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or System.Text.Json.JsonException) { }
    }

    private Control BuildObservedProcessStatus(Guid? serverId = null, Guid? clusterId = null)
    {
        var ids = serverId is Guid id ? new[] { id }
            : catalog?.Servers.Where(s => s.ClusterId == clusterId).Select(s => s.Id).ToArray() ?? [];
        var states = ids.Select(id => processStatus?.Servers.FirstOrDefault(s => s.ServerId == id)?.State
            ?? RegisteredProcessState.Unknown).ToArray();
        var managed = ids.Select(id => managedRuntimeStatus.FirstOrDefault(s => s.ServerId == id && s.Managed)).ToArray();
        if (managed.Length > 0 && managed.All(s => s is not null)
            && !managed.Select((s, index) => s!.State is ServerState.Stopped or ServerState.Failed
                && states[index] == RegisteredProcessState.Detected).Any(conflict => conflict))
        {
            var runtimeStates = managed.Select(s => s!.State).ToArray();
            var runtimeText = runtimeStates.All(s => s == ServerState.Running) ? "Running"
                : runtimeStates.All(s => s == ServerState.Stopped) ? "Stopped"
                : runtimeStates.All(s => s == ServerState.Failed) ? "Failed"
                : runtimeStates.Length == 1 ? runtimeStates[0].ToString() : "Partial";
            return Status(runtimeText, runtimeText == "Running" ? "#64E581" : runtimeText is "Failed" or "Stopped" ? "#FF6062" : "#FFBC43");
        }
        var fresh = processStatus is not null && DateTimeOffset.UtcNow - processStatus.MeasuredAt < TimeSpan.FromMinutes(1);
        var detected = states.Count(s => s == RegisteredProcessState.Detected);
        var (text, color) = !fresh || states.Length == 0 || states.Contains(RegisteredProcessState.Unknown)
            ? ("Status unavailable", "#94A7B7")
            : detected == states.Length ? ("Process active", "#64E581")
            : detected > 0 ? ("Partial", "#FFBC43") : ("Stopped", "#FF5549");
        var control = Status(text, color);
        ToolTip.SetTip(control, fresh ? $"Process check: {processStatus!.MeasuredAt.LocalDateTime:t}. Process detection does not confirm server readiness or player count. Refresh to check again."
            : "Refresh to check server processes. Status is unavailable when detection is incomplete or the last check is old.");
        return control;
    }
}
