using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Styling;
using Avalonia.Media;

namespace ArkServerManager.Desktop;

public sealed class App : Application
{
    public string? InstallerPayloadDirectory { get; init; }
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        var theme = new FluentTheme();
        theme.Palettes[ThemeVariant.Dark] = new ColorPaletteResources { Accent = Color.Parse("#FFBC43"), RegionColor = Color.Parse("#0F1921") };
        Styles.Add(theme);
        foreach (var (key, color) in new[]
        {
            ("ButtonBackground", "#1B2934"), ("ButtonBackgroundPointerOver", "#263B4A"),
            ("ButtonBackgroundPressed", "#13232E"), ("ButtonBackgroundDisabled", "#18232C"),
            ("ButtonBorderBrush", "#405363"), ("ButtonBorderBrushPointerOver", "#607888"),
            ("ButtonBorderBrushDisabled", "#2B3A46"), ("ButtonForegroundDisabled", "#7B8B97"),
            ("AccentButtonBackground", "#FFBC43"), ("AccentButtonBackgroundPointerOver", "#FFCA68"),
            ("AccentButtonBackgroundPressed", "#EAA62F"), ("AccentButtonForeground", "#181B1F"),
            ("AccentButtonForegroundPointerOver", "#181B1F"), ("AccentButtonForegroundPressed", "#181B1F"),
            ("AccentButtonBackgroundDisabled", "#18232C"), ("AccentButtonForegroundDisabled", "#7B8B97"),
            ("AccentButtonBorderBrushDisabled", "#2B3A46"),
            ("TextControlBackground", "#101C26"), ("TextControlBackgroundPointerOver", "#142430"),
            ("TextControlBackgroundFocused", "#101C26"), ("TextControlBorderBrush", "#405363"),
            ("TextControlBorderBrushPointerOver", "#607888"), ("TextControlBorderBrushFocused", "#FFBC43")
        }) Resources[key] = Brush.Parse(color);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = InstallerPayloadDirectory is not null ? new InstallerPreviewWindow(InstallerPayloadDirectory) : desktop.Args switch
            {
                ["--installer-preview"] => new InstallerPreviewWindow(),
                ["--install-payload", var payload] => new InstallerPreviewWindow(payload),
                ["--update-preview"] => new LaunchUpdatePreviewWindow(),
                ["--design-preview"] => new MainWindow(desktop.Args),
                ["--update-result", var outcomePath] => OpenUpdateResult(outcomePath),
                _ => new LaunchUpdateWindow(desktop, desktop.Args)
            };
        base.OnFrameworkInitializationCompleted();
    }

    private static MainWindow OpenUpdateResult(string path)
    {
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 16 * 1024) throw new InvalidDataException();
            var outcome = System.Text.Json.JsonSerializer.Deserialize<ArkServerManager.Core.AppUpdateLaunchOutcome>(input);
            if (outcome is null || string.IsNullOrWhiteSpace(outcome.Notice) || outcome.Notice.Length > 400) throw new InvalidDataException();
            return new MainWindow(null, outcome.Notice, outcome.Updated ? outcome.RecoveryJournal : null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        { return new MainWindow(null, "The update result could not be read. You are using the installed version."); }
    }
}
