using Avalonia;
using ArkServerManager.Desktop;
using ArkServerManager.Core;
using System.Text.Json;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var payload = Path.Combine(AppContext.BaseDirectory, "payload");
        string? extractionDirectory = null;
        using var embedded = typeof(Program).Assembly.GetManifestResourceStream("ArkServerManager.InstallerPayload.zip");
        if (embedded is not null)
        {
            var temporary = Path.Combine(Path.GetTempPath(), "ArkServerManager-Setup-" + Guid.NewGuid().ToString("N"));
            extractionDirectory = temporary;
            Directory.CreateDirectory(temporary);
            var package = Path.Combine(temporary, "payload.zip");
            using (var output = new FileStream(package, FileMode.CreateNew, FileAccess.Write, FileShare.None)) embedded.CopyTo(output);
            var runtime = OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsLinux() ? "linux-x64"
                : throw new PlatformNotSupportedException("Setup supports Windows and Linux.");
            // Use the same bounded extraction and complete manifest verification as application updates.
            payload = AppUpdateStaging.ExtractAsync(package, temporary, runtime).GetAwaiter().GetResult().Directory;
            File.Delete(package);
        }
        if (args is ["--verify-embedded-payload", var reportPath])
        {
            if (embedded is null || !Path.IsPathFullyQualified(reportPath))
                throw new ArgumentException("Verification requires an embedded payload and an absolute report path.");
            var runtime = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
            var manifest = VerifiedPayloadInstaller.VerifyAsync(payload, runtime).GetAwaiter().GetResult();
            using var report = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(report, new { Verified = true, Runtime = runtime, Files = manifest.Files.Count,
                ExtractedDirectory = payload, InstallerInteractionVerified = false });
            return;
        }
        try
        {
            AppBuilder.Configure(() => new App { InstallerPayloadDirectory = payload })
                .UsePlatformDetect().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            if (extractionDirectory is not null)
                try { InstallerExtractionCleanup.Remove(extractionDirectory); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { System.Diagnostics.Trace.WriteLine("Installer extraction retained: " + error.Message); }
        }
    }
}
