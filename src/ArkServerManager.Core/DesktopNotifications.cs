using System.Runtime.InteropServices;

namespace ArkServerManager.Core;

public interface IDesktopNotifications : IDisposable
{
    void Show(string message, ActivitySeverity severity);
}

public sealed class WindowsDesktopNotifications : IDesktopNotifications
{
    public const uint CallbackMessage = 0x8000 + 173;
    private readonly IntPtr window;
    private bool added;
    public WindowsDesktopNotifications(IntPtr window)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (window == IntPtr.Zero) throw new ArgumentException("A desktop window is required.");
        this.window = window;
    }
    public void Show(string message, ActivitySeverity severity)
    {
        ValidateMessage(message, severity);
        var data = Data();
        if (!added)
        {
            data.Flags = 0x1 | 0x2 | 0x4;
            data.Icon = LoadIconW(IntPtr.Zero, new IntPtr(32512));
            if (data.Icon == IntPtr.Zero || !Shell_NotifyIconW(0, ref data)) throw new IOException("Windows notifications are unavailable.");
            added = true;
        }
        data.Flags = 0x10 | 0x40;
        data.Info = message;
        data.InfoTitle = "ARK Server Manager";
        data.InfoFlags = 0x80 | 0x10 | (severity switch { ActivitySeverity.Error => 3u, ActivitySeverity.Warning => 2u, _ => 1u });
        if (!Shell_NotifyIconW(1, ref data))
        {
            // Explorer may have restarted. Remove our stale registration and try
            // registering again on the next event; never replay the old alert.
            Dispose(); throw new IOException("Windows could not accept the notification.");
        }
    }
    public void Dispose()
    {
        if (!added) return;
        var data = Data(); Shell_NotifyIconW(2, ref data); added = false;
    }
    private NotifyIconData Data() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = window, Id = 173,
        Callback = CallbackMessage, Tip = "ARK Server Manager · Open activity", Info = "", InfoTitle = ""
    };
    internal static void ValidateMessage(string message, ActivitySeverity severity)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > 200 || message.Any(char.IsControl) || !Enum.IsDefined(severity))
            throw new ArgumentException("Choose a short notification summary.");
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size; public IntPtr Window; public uint Id; public uint Flags; public uint Callback; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State; public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadIconW(IntPtr instance, IntPtr name);
}

/// <summary>Uses the desktop notification service through libnotify; no shell commands.</summary>
public sealed class LinuxDesktopNotifications : IDesktopNotifications
{
    public LinuxDesktopNotifications()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        if (notify_init("ARK Server Manager") == 0) throw new IOException("The Linux desktop notification service is unavailable.");
    }
    public void Show(string message, ActivitySeverity severity)
    {
        WindowsDesktopNotifications.ValidateMessage(message, severity);
        var notification = notify_notification_new("ARK Server Manager", message, "dialog-information");
        if (notification == IntPtr.Zero) throw new IOException("A desktop notification could not be created.");
        IntPtr error = IntPtr.Zero;
        try
        {
            // Normal urgency respects desktop do-not-disturb behavior.
            notify_notification_set_urgency(notification, 1);
            notify_notification_set_timeout(notification, 8000);
            if (notify_notification_show(notification, out error) == 0) throw new IOException("The Linux desktop could not accept the notification.");
        }
        finally { if (error != IntPtr.Zero) g_error_free(error); g_object_unref(notification); }
    }
    public void Dispose() { }
    [DllImport("libnotify.so.4")] private static extern int notify_init([MarshalAs(UnmanagedType.LPUTF8Str)] string app);
    [DllImport("libnotify.so.4")] private static extern IntPtr notify_notification_new([MarshalAs(UnmanagedType.LPUTF8Str)] string summary,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string body, [MarshalAs(UnmanagedType.LPUTF8Str)] string icon);
    [DllImport("libnotify.so.4")] private static extern void notify_notification_set_urgency(IntPtr notification, int urgency);
    [DllImport("libnotify.so.4")] private static extern void notify_notification_set_timeout(IntPtr notification, int timeout);
    [DllImport("libnotify.so.4")] private static extern int notify_notification_show(IntPtr notification, out IntPtr error);
    [DllImport("libgobject-2.0.so.0")] private static extern void g_object_unref(IntPtr value);
    [DllImport("libglib-2.0.so.0")] private static extern void g_error_free(IntPtr error);
}
