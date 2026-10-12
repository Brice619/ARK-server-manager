using System.Reflection;
using System.Runtime.InteropServices;
using ArkServerManager.Core;

namespace ArkServerManager.Core.Tests;

public sealed class WindowsSessionShutdownTests
{
    [Fact]
    public async Task OwnedWindowRequestsStopOnlyOnceAndWaitsForCompletion()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var requested = new ManualResetEventSlim();
        var count = 0;
        using var listener = new WindowsSessionShutdown(() => { Interlocked.Increment(ref count); requested.Set(); });
        var window = (nint)typeof(WindowsSessionShutdown).GetField("window", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(listener)!;
        Assert.NotEqual(0, window);
        var query = Task.Run(() => SendMessageW(window, 0x11, 0, 0));
        Assert.True(requested.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(query.IsCompleted);
        listener.Complete();
        Assert.Equal(1, await query.WaitAsync(TimeSpan.FromSeconds(5)));
        SendMessageW(window, 0x16, 1, 0);
        Assert.Equal(1, count);
    }

    [Fact]
    public void CanceledEndSessionDoesNotRequestStop()
    {
        if (!OperatingSystem.IsWindows()) return;
        var count = 0;
        using var listener = new WindowsSessionShutdown(() => count++);
        var window = (nint)typeof(WindowsSessionShutdown).GetField("window", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(listener)!;
        SendMessageW(window, 0x16, 0, 0);
        Assert.Equal(0, count);
        SendMessageW(window, 0x16, 1, 0);
        Assert.Equal(1, count);
        listener.Complete();
    }

    [Fact]
    public void DisposingIdleListenerDoesNotStopServers()
    {
        if (!OperatingSystem.IsWindows()) return;
        var count = 0;
        var listener = new WindowsSessionShutdown(() => count++);
        listener.Dispose();
        listener.Dispose();
        Assert.Equal(0, count);
    }

    [DllImport("user32.dll")] private static extern nint SendMessageW(nint window, uint message, nint wParam, nint lParam);
}
