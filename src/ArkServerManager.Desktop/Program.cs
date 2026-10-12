using Avalonia;

namespace ArkServerManager.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using var applicationLease = ArkServerManager.Core.AppFileLease.OpenUsageIfInstalled(AppContext.BaseDirectory);
        AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
    }
}
