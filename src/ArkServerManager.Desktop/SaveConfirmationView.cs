using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private bool previewSaveConfirmationOpen;

    private async Task<SaveChoice> ShowPreviewSaveConfirmationAsync(ServerDefinition server, string subject)
    {
        if (!designPreview || previewSaveConfirmationOpen || lifetime.IsCancellationRequested) return SaveChoice.Cancel;
        previewSaveConfirmationOpen = true;
        try
        {
            var dialog = new Window { Title = "Save configuration?", Width = 620, Height = 510,
                CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Brush.Parse("#0F1921"), FontFamily = FontFamily, ShowInTaskbar = false };
            var body = new StackPanel { Spacing = 20, Margin = new Thickness(34) };
            var heading = Label("Apply these changes?", 28, true); heading.TextAlignment = TextAlignment.Center;
            body.Children.Add(heading);
            var scope = Label(server.Settings.Name + " · " + subject, 17);
            scope.TextAlignment = TextAlignment.Center; scope.Foreground = Muted; body.Children.Add(scope);
            var explanation = Label("Design preview · Choose the save flow to review. No server files or running processes will change.", 16);
            explanation.TextAlignment = TextAlignment.Center; body.Children.Add(explanation);
            foreach (var choice in new[] { SaveChoice.SaveAndRestart, SaveChoice.SaveForNextStart, SaveChoice.Cancel })
            {
                var label = choice switch { SaveChoice.SaveAndRestart => "Save and restart", SaveChoice.SaveForNextStart => "Save for next start", _ => "Cancel" };
                var action = ActionButton(label, () => dialog.Close(choice), choice == SaveChoice.SaveAndRestart);
                action.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
                body.Children.Add(action);
            }
            if (server.ClusterId is not null)
            {
                var clusterNote = Label("Restarting affects every server in this cluster.", 14);
                clusterNote.Foreground = Muted; body.Children.Add(clusterNote);
            }
            dialog.Content = body;
            return await dialog.ShowDialog<SaveChoice>(this);
        }
        finally { previewSaveConfirmationOpen = false; }
    }
}
