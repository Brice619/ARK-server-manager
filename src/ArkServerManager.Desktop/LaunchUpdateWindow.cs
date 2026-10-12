using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using ArkServerManager.Core;
using static ArkServerManager.Desktop.MainWindow;

namespace ArkServerManager.Desktop;

public sealed class LaunchUpdateWindow : Window
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly TextBlock status = Label("Checking for the latest version…", 18);
    private readonly IClassicDesktopStyleApplicationLifetime? desktop;
    private readonly string[]? arguments;

    public LaunchUpdateWindow(IClassicDesktopStyleApplicationLifetime? desktop, string[]? arguments, bool designReview = false)
    {
        if (desktop is null && !designReview) throw new ArgumentNullException(nameof(desktop));
        this.desktop = desktop; this.arguments = arguments;
        Title = "ARK Server Manager"; Width = 1100; Height = 760; MinWidth = 700; MinHeight = 560;
        FontFamily = new FontFamily("Segoe UI, Inter, sans-serif"); Background = Brush.Parse("#0F1921");
        var body = new StackPanel { Spacing = 24, MaxWidth = 700, Margin = new Thickness(32),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var mark = VectorIcon("M2,32 L18,2 L34,32 M9,22 L27,22", Brush.Parse("#8DD5E4"), 94);
        mark.HorizontalAlignment = HorizontalAlignment.Center; body.Children.Add(mark);
        foreach (var label in new[] { Label("ARK Server Manager", 27, true), Label("Updating your app", 38, true), status })
        { label.TextAlignment = TextAlignment.Center; label.HorizontalAlignment = HorizontalAlignment.Center; body.Children.Add(label); }
        body.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 12 });
        Content = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        if (!designReview) Opened += async (_, _) => await UpdateAsync();
        Closed += (_, _) => lifetime.Cancel();
    }

    private async Task UpdateAsync()
    {
        try
        {
            var application = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
            if (InstalledAppSettingsFile.Read(application) is null) { OpenInstalled(null); return; }
            var runtime = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var checkDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            checkDeadline.CancelAfter(TimeSpan.FromSeconds(15));
            var version = typeof(App).Assembly.GetName().Version ?? new Version(1, 0, 0);
            var release = await new AppReleaseClient(http).FindUpdateAsync(version, runtime, checkDeadline.Token);
            if (release is null) { OpenInstalled(null); return; }
            status.Text = "Downloading the latest version…";
            var work = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ARKServerManager", "Updates", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var archive = Path.Combine(work, release.Asset.Name);
            await AppReleaseDownload.DownloadAsync(http, release, archive, lifetime.Token);
            status.Text = "Checking the update files…";
            var staged = await AppUpdateStaging.ExtractAsync(archive, work, runtime, lifetime.Token);
            var installed = await VerifiedPayloadInstaller.VerifyInstalledAsync(application, runtime, lifetime.Token);
            var tool = Path.Combine(work, "tool"); Directory.CreateDirectory(tool);
            var updaterFiles = installed.Files.Where(f => f.Path.StartsWith("updater/", StringComparison.Ordinal)).ToArray();
            if (updaterFiles.Length == 0) throw new IOException("The installed updater is unavailable.");
            foreach (var file in updaterFiles)
            {
                var source = Path.Combine(application, file.Path);
                var target = Path.Combine(tool, file.Path[8..]);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await input.CopyToAsync(output, lifetime.Token);
                await using var copied = File.OpenRead(target);
                if (copied.Length != file.Bytes || !Convert.ToHexString(await SHA256.HashDataAsync(copied, lifetime.Token)).Equals(file.SHA256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The updater tool could not be verified.");
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(source));
            }
            var resultPath = Path.Combine(work, "result.json");
            var executable = Path.Combine(tool, OperatingSystem.IsWindows() ? "ArkServerManager.Updater.exe" : "ArkServerManager.Updater");
            using var parent = Process.GetCurrentProcess();
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = tool };
            foreach (var argument in new[] { "--handoff", application, staged.Directory, work, runtime,
                parent.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), parent.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), resultPath })
                start.ArgumentList.Add(argument);
            using var updater = Process.Start(start) ?? throw new IOException("The updater could not start.");
            status.Text = "Preparing to restart the app…";
            using var handoffDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            handoffDeadline.CancelAfter(TimeSpan.FromSeconds(20));
            while (!File.Exists(resultPath + ".ready"))
            {
                if (updater.HasExited) throw new IOException("The updater could not prepare the update.");
                await Task.Delay(100, handoffDeadline.Token);
            }
            desktop!.Shutdown();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or HttpRequestException or InvalidOperationException or ArgumentException
            or UnauthorizedAccessException or JsonException or OperationCanceledException or System.ComponentModel.Win32Exception or KeyNotFoundException)
        { OpenInstalled("The app update could not complete. You are using the installed version."); }
    }

    private void OpenInstalled(string? notice)
    {
        if (lifetime.IsCancellationRequested) return;
        var main = new MainWindow(arguments, notice);
        desktop!.MainWindow = main; main.Show(); Close();
    }
}
