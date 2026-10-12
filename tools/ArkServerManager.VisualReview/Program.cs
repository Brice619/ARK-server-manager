using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.VisualTree;
using ArkServerManager.Desktop;

if (args is not [var screen, var destination, var widthText, var heightText]
    || !int.TryParse(widthText, out var width) || !int.TryParse(heightText, out var height)
    || width < (screen == "cluster-joining-confirmation" ? 500 : screen is "existing-server-registration" or "startup-selection" or "creation-mod-details" or "saved-configuration" or "configuration-resolution" or "configuration-resolution-missing" || screen.StartsWith("cluster-shared-settings", StringComparison.Ordinal) ? 600 : 900)
    || width > 2560 || height < (screen == "cluster-joining-confirmation" ? 400 : 640) || height > 1600)
{
    Console.Error.WriteLine("Choose a review screen, PNG path, width and height."); return 2;
}
AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions
    { UseHeadlessDrawing = false }).SetupWithoutStarting();
Window window = screen.StartsWith("cluster-shared-settings", StringComparison.Ordinal) ? MainWindow.CreateSharedSettingsDesignReview()
    : screen == "existing-server-registration" ? MainWindow.CreateExistingRegistrationDesignReview()
    : screen == "cluster-joining-confirmation" ? MainWindow.CreateJoiningConfirmationDesignReview()
    : screen == "mod-configuration-files" ? MainWindow.CreateModConfigurationFilesDesignReview()
    : screen == "startup-selection" ? MainWindow.CreateServerStartupDesignReview()
    : screen == "creation-mod-details" ? MainWindow.CreateCreationModDetailsDesignReview()
    : screen == "saved-configuration" ? MainWindow.CreateSavedConfigurationDesignReview()
    : screen is "configuration-resolution" or "configuration-resolution-missing" ? MainWindow.CreateConfigurationResolutionDesignReview(screen == "configuration-resolution-missing")
    : screen == "installer-components" ? new InstallerPreviewWindow(componentsDesignPreview: true)
    : screen == "app-launch-update" ? new LaunchUpdateWindow(null, null, designReview: true) : MainWindow.CreateDesignReview(screen);
window.Width = width; window.Height = height; window.Show();
Dispatcher.UIThread.RunJobs();
if (screen == "window-controls")
{
    Button Caption(string name) => window.GetVisualDescendants().OfType<Button>().Single(button => Avalonia.Automation.AutomationProperties.GetName(button) == name);
    var maximize = Caption("Maximize or restore window");
    maximize.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
    if (window.WindowState != WindowState.Maximized) throw new InvalidOperationException("Maximize action did not change window state.");
    maximize.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
    if (window.WindowState != WindowState.Normal) throw new InvalidOperationException("Restore action did not change window state.");
    Caption("Minimize window").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
    if (window.WindowState != WindowState.Minimized) throw new InvalidOperationException("Minimize action did not change window state.");
    window.WindowState = WindowState.Normal; Dispatcher.UIThread.RunJobs();
}
if (screen is "creation-mods-invalid" or "creation-mods-review")
{
    var editor = window.GetVisualDescendants().OfType<TextBox>().Single(box => box.PlaceholderText == "Mod IDs");
    editor.Text = screen == "creation-mods-invalid" ? "not-a-mod-id" : "2182894352, 731604991";
    Dispatcher.UIThread.RunJobs();
    var next = window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Continue  ›"));
    next.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    Dispatcher.UIThread.RunJobs();
    var text = window.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToArray();
    var expected = screen == "creation-mods-invalid" ? "Enter distinct positive numeric mod IDs in load order." : "2 mods";
    if (!text.Contains(expected)) throw new InvalidOperationException("Creation mod review did not reach the expected production state.");
}
if (screen == "cluster-shared-settings-advanced")
{
    window.GetVisualDescendants().OfType<Expander>().Single().IsExpanded = true;
    Dispatcher.UIThread.RunJobs();
}
if (screen == "network-connections")
{
    window.GetVisualDescendants().OfType<Expander>().Single(item => Equals(item.Header, "Local network connections")).IsExpanded = true;
    Dispatcher.UIThread.RunJobs();
    var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().First();
    scroll.Offset = new Vector(0, scroll.Extent.Height); Dispatcher.UIThread.RunJobs();
}
if (screen is "asa-mod-details" or "preset-edit" or "server-welcome" or "shared-cluster-history" or "coordinated-history" or "coordinated-point" or "restore-recovery" or "shared-restore-recovery" or "cluster-restore-recovery" or "coordinated-unavailable")
{
    var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().First();
    scroll.Offset = new Vector(0, scroll.Extent.Height);
    Dispatcher.UIThread.RunJobs();
}
AvaloniaHeadlessPlatform.ForceRenderTimerTick();
using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No offscreen frame was rendered.");
var path = Path.GetFullPath(destination); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
using (var output = File.Create(path)) frame.Save(output, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
if (screen == "mod-configuration-files")
{
    foreach (var file in new[] { "GameUserSettings.ini", "Game.ini" })
    {
        window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Edit " + file))
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        var expectedSection = file == "Game.ini" ? "[/Script/ShooterGame.ShooterGameMode]" : "[ServerSettings]";
        if (!window.GetVisualDescendants().OfType<TextBox>().Any(editor => editor.Text?.Contains(expectedSection, StringComparison.Ordinal) == true))
            throw new InvalidOperationException("Mod configuration did not open the selected file editor.");
        window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "← Back to mod configuration"))
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        if (!window.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text == "Mod ID: 928708"))
            throw new InvalidOperationException("Returning from configuration lost the mod context.");
    }
    Console.WriteLine("Both mod configuration file routes and return navigation checked offscreen.");
}
if (screen == "window-controls")
{
    window.GetVisualDescendants().OfType<Button>().Single(button => Avalonia.Automation.AutomationProperties.GetName(button) == "Close window")
        .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
    var confirmation = window.OwnedWindows.Single(dialog => dialog.Title == "Close ARK Server Manager?");
    confirmation.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Close preview"))
        .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
    if (window.IsVisible) throw new InvalidOperationException("Close action did not close the preview window.");
    Console.WriteLine("Production minimize, maximize, restore and close actions checked offscreen.");
}
Console.WriteLine($"Rendered {screen} at {width} x {height}: {path}");
return 0;
