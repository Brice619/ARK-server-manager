using Avalonia.Controls;
using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private Control ObservedProcessStatus(Guid? serverId = null, Guid? clusterId = null)
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
            : detected > 0 ? ("Partial", "#FFBC43") : ("Not detected", "#94A7B7");
        var control = Status(text, color);
        ToolTip.SetTip(control, fresh ? $"Process check: {processStatus!.MeasuredAt.LocalDateTime:t}. Process detection does not confirm server readiness or player count. Refresh to check again."
            : "Refresh to check server processes. Status is unavailable when detection is incomplete or the last check is old.");
        return control;
    }
}
