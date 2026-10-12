using System.Diagnostics;
using System.Runtime.InteropServices;
using ArkServerManager.Core;

try
{
    if (args is not ["--application", var application] || !Path.IsPathFullyQualified(application))
        throw new ArgumentException("Choose an installed application using --application and its absolute folder.");
    application = Path.TrimEndingDirectorySeparator(Path.GetFullPath(application));
    if (!AppBootstrapLayout.IsProtectedLauncherDirectory(application, AppContext.BaseDirectory)
        && AppContext.BaseDirectory.StartsWith(application + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        throw new IOException("The recovery launcher must be installed outside the application files it protects.");
    var runtime = OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsLinux() ? "linux-x64"
        : throw new PlatformNotSupportedException("This launcher supports Windows and Linux.");
    var status = await AppUpdateLaunchRecovery.RecoverBeforeLaunchAsync(application);
    await VerifiedPayloadInstaller.VerifyInstalledAsync(application, runtime);
    var start = new ProcessStartInfo(Path.Combine(application, OperatingSystem.IsWindows() ? "ArkServerManager.Desktop.exe" : "ArkServerManager.Desktop"))
    { UseShellExecute = false, WorkingDirectory = application, CreateNoWindow = true };
    if (status?.Record.Phase == AppUpdatePhase.RolledBack && File.Exists(status.Pointer.ResultPath))
    { start.ArgumentList.Add("--update-result"); start.ArgumentList.Add(status.Pointer.ResultPath); }
    using var desktop = Process.Start(start) ?? throw new IOException("ARK Server Manager could not open.");
    return 0;
}
catch (Exception error)
{
    var message = "ARK Server Manager could not open safely.\n\n" + error.Message;
    if (OperatingSystem.IsWindows()) NativeNotice.MessageBox(IntPtr.Zero, message, "ARK Server Manager", 0x10);
    else Console.Error.WriteLine(message);
    return 1;
}

internal static class NativeNotice
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int MessageBox(IntPtr owner, string text, string title, uint flags);
}
