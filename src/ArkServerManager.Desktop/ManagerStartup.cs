using System.Diagnostics;
using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

internal static class ManagerStartup
{
    private sealed record Ready(string Status, int ProtocolVersion);

    public static async Task EnsureReadyAsync(LocalManagerClient client, string root, CancellationToken ct, string? applicationDirectory = null)
    {
        async Task<bool> Probe()
        {
            try
            {
                var response = await client.CallAsync<Ready>("ping", null, TimeSpan.FromMilliseconds(500), ct);
                if (response.Status != "ready" || response.ProtocolVersion != LocalManagerProtocol.Version)
                    throw new InvalidDataException("The background manager is incompatible with this app.");
                return true;
            }
            catch (TimeoutException) { return false; }
        }

        if (await Probe()) return;
        ct.ThrowIfCancellationRequested();
        var executable = Path.Combine(applicationDirectory ?? AppContext.BaseDirectory, "manager",
            OperatingSystem.IsWindows() ? "ArkServerManager.Host.exe" : "ArkServerManager.Host");
        if (!File.Exists(executable)) throw new FileNotFoundException("The installed background manager is missing.", executable);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        start.ArgumentList.Add("--data-root");
        start.ArgumentList.Add(root);
        // The host owns its lifetime. Disposing this handle must not stop managed servers.
        Process? launched;
        try { launched = Process.Start(start); }
        catch (System.ComponentModel.Win32Exception error)
        { throw new IOException("The installed background manager could not be launched.", error); }
        using var process = launched ?? throw new IOException("The background manager could not start.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                // Another app window may win host ownership; probe that owner even if our process exits.
                if (await Probe()) return;
                await Task.Delay(200, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("The background manager could not become ready. Check the installed files and server folder."); }
    }
}
