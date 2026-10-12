using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ArkServerManager.Core;

// A hidden top-level window receives session broadcasts even when the desktop UI is closed.
public sealed class WindowsSessionShutdown : IDisposable
{
    private readonly Action requestStop;
    private readonly ManualResetEventSlim stopped = new();
    private readonly TaskCompletionSource<nint> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread thread;
    private readonly WindowProcedure procedure;
    private readonly string className = "ArkManagerSession_" + Guid.NewGuid().ToString("N");
    private nint window;
    private int requested;
    private int disposed;

    public WindowsSessionShutdown(Action requestStop)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        this.requestStop = requestStop ?? throw new ArgumentNullException(nameof(requestStop));
        procedure = ProcessMessage;
        thread = new Thread(MessageLoop) { IsBackground = true, Name = "ARK session shutdown" };
        thread.Start();
        window = ready.Task.GetAwaiter().GetResult();
    }

    public void Complete() => stopped.Set();

    private nint ProcessMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == 0x11) // WM_QUERYENDSESSION: Windows owns any shutdown-delay prompt.
        {
            ShutdownBlockReasonCreate(hwnd, "ARK Server Manager is saving and stopping your servers.");
            try
            {
                if (Interlocked.Exchange(ref requested, 1) == 0) requestStop();
                stopped.Wait(TimeSpan.FromSeconds(20));
            }
            catch (Exception) { /* Never unwind a managed exception through a native callback. */ }
            finally { ShutdownBlockReasonDestroy(hwnd); }
            return 1;
        }
        if (message == 0x16 && wParam != 0) // WM_ENDSESSION fallback, including forced shutdown.
        {
            try { if (Interlocked.Exchange(ref requested, 1) == 0) requestStop(); }
            catch (Exception) { }
            return 0;
        }
        if (message == 0x10) { DestroyWindow(hwnd); return 0; }
        if (message == 0x2) { PostQuitMessage(0); return 0; }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void MessageLoop()
    {
        var instance = GetModuleHandleW(null);
        var registered = false;
        try
        {
            var definition = new WindowClass { Procedure = procedure, Instance = instance, ClassName = className };
            if (RegisterClassW(ref definition) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            registered = true;
            var handle = CreateWindowExW(0, className, "ARK Server Manager", 0, 0, 0, 0, 0, 0, 0, instance, 0);
            if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            ready.TrySetResult(handle);
            int result;
            while ((result = GetMessageW(out var message, 0, 0, 0)) > 0)
            { TranslateMessage(ref message); DispatchMessageW(ref message); }
            if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch (Exception error) { ready.TrySetException(error); }
        finally { if (registered) UnregisterClassW(className, instance); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Complete();
        PostMessageW(window, 0x10, 0, 0);
        thread.Join();
        stopped.Dispose();
        GC.KeepAlive(procedure);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint WindowProcedure(nint hwnd, uint message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Style; public WindowProcedure Procedure; public int ClassExtra; public int WindowExtra;
        public nint Instance; public nint Icon; public nint Cursor; public nint Background;
        public string? MenuName; public string ClassName;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMessage
    {
        public nint Window; public uint Message; public nuint WParam; public nint LParam;
        public uint Time; public int X; public int Y; public uint Private;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassW(ref WindowClass definition);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClassW(string name, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetMessageW(out NativeMessage message, nint window, uint minimum, uint maximum);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(ref NativeMessage message);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool ShutdownBlockReasonCreate(nint window, string reason);
    [DllImport("user32.dll")] private static extern bool ShutdownBlockReasonDestroy(nint window);
}
