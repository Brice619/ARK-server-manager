using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using static ArkServerManager.Desktop.MainWindow;

namespace ArkServerManager.Desktop;

// Artwork-based launch states only; no update checks, downloads or package writes.
public sealed class LaunchUpdatePreviewWindow : Window
{
    private readonly StackPanel body = new() { Spacing = 24, MaxWidth = 700,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(32) };

    public LaunchUpdatePreviewWindow()
    {
        Title = "ARK Server Manager — Update Design Preview";
        Width = 1100; Height = 760; MinWidth = 700; MinHeight = 560;
        if (Screens.Primary is { } screen)
        {
            Width = Math.Min(Width, screen.WorkingArea.Width / screen.Scaling);
            Height = Math.Min(Height, screen.WorkingArea.Height / screen.Scaling - 32);
        }
        FontFamily = new FontFamily("Segoe UI, Inter, sans-serif");
        Background = Brush.Parse("#0F1921");
        Content = new ScrollViewer { Content = body,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        RenderState(false);
    }

    private void RenderState(bool failed)
    {
        body.Children.Clear();
        var mark = VectorIcon("M2,32 L18,2 L34,32 M9,22 L27,22", Brush.Parse("#8DD5E4"), 94);
        mark.HorizontalAlignment = HorizontalAlignment.Center; body.Children.Add(mark);
        AddCentered("ARK Server Manager", 27, true);
        AddCentered(failed ? "Update unavailable" : "Updating your app", 38, true);
        AddCentered(failed ? "The installed version will open with a notice." : "Checking for the latest version…", 18);
        if (!failed) body.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 12 });
        else body.Children.Add(Surface(Label("The update could not be completed. You can continue using the installed version.", 16)));
        AddCentered("Design preview · No update check or download is running.", 14);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        actions.Children.Add(ActionButton(failed ? "Preview checking state" : "Preview update failure",
            () => RenderState(!failed)));
        body.Children.Add(actions);
        // The real launch flow must open automatically on failure; this preview never substitutes for it.
    }

    private void AddCentered(string text, double size, bool bold = false)
    {
        var label = Label(text, size, bold); label.TextAlignment = TextAlignment.Center;
        label.HorizontalAlignment = HorizontalAlignment.Center; body.Children.Add(label);
    }
}
