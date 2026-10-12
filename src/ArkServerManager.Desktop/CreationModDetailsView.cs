using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private async Task ReviewCreationModAsync()
    {
        if (designPreview || draftEdition is null) return;
        var edition = draftEdition.Value; var serverId = creationServerId;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var dialog = new Window { Title = "Find a mod", Width = 620, Height = 680, MinWidth = 520, MinHeight = 540,
            Background = Brush.Parse("#0F1921"), FontFamily = FontFamily, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var body = new StackPanel { Margin = new Thickness(28), Spacing = 18 };
        body.Children.Add(Label("Find a mod", 27, true));
        body.Children.Add(Label(edition == ArkEdition.ASA ? "Look up a public CurseForge project ID." : "Look up a Steam Workshop ID.", 16));
        var id = new TextBox { PlaceholderText = "Mod ID", MaxLength = 20 }; body.Children.Add(id);
        var notice = Label("", 15); var details = new StackPanel { Spacing = 14 };
        Bitmap? artwork = null;
        var find = ActionButton("Find details", null, true);
        find.IsEnabled = true;
        find.Click += async (_, _) =>
        {
            if (cancellation.IsCancellationRequested) return;
            find.IsEnabled = false; id.IsEnabled = false; details.Children.Clear(); artwork?.Dispose(); artwork = null;
            notice.Text = "Finding mod details…";
            try
            {
                using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
                var mod = edition == ArkEdition.ASA ? await new PublicAsaModMetadata(http).GetAsync(id.Text?.Trim() ?? "", cancellation.Token)
                    : await new SteamWorkshopMetadata(http).GetAsync(id.Text?.Trim() ?? "", cancellation.Token);
                if (cancellation.IsCancellationRequested) return;
                modDetails[(mod.Edition, mod.Id)] = mod;
                var (panel, image) = CreationModDetailsPanel(mod, () =>
                {
                    if (creationServerId != serverId || draftEdition != edition) { notice.Text = "The server draft changed. Close this window and review again."; return; }
                    try
                    {
                        var selected = CreationModIds();
                        if (selected.Any(value => ulong.Parse(value) == ulong.Parse(mod.Id))) { notice.Text = "This mod is already selected."; return; }
                        selected.Add(mod.Id); creationModDraft = string.Join(", ", selected); dialog.Close();
                    }
                    catch (ArgumentException error) { notice.Text = error.Message + " Close this window to correct the selected list first."; }
                });
                details.Children.Add(panel);
                notice.Text = edition == ArkEdition.ASA ? "Public details provided by CFWidget. Installation uses the selected ID." : "Steam Workshop details. Installation uses the selected ID.";
                if (mod.PreviewImage is { } picture)
                {
                    var loadedArtwork = await ReadModArtworkAsync(picture, cancellation.Token);
                    if (cancellation.IsCancellationRequested) loadedArtwork?.Dispose();
                    else if (loadedArtwork is not null) { artwork = loadedArtwork; image.Source = artwork; image.IsVisible = true; }
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException or ArgumentException or ModLookupException
                or System.Text.Json.JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
            { if (!cancellation.IsCancellationRequested) notice.Text = error is ArgumentException or ModLookupException ? error.Message : "Details could not be retrieved. You can still enter the ID directly in setup."; }
            finally { if (!cancellation.IsCancellationRequested) { find.IsEnabled = true; id.IsEnabled = true; } }
        };
        body.Children.Add(find); body.Children.Add(notice); body.Children.Add(details);
        body.Children.Add(ActionButton("Close", () => dialog.Close()));
        dialog.Content = new ScrollViewer { Content = body };
        dialog.Closed += (_, _) => { cancellation.Cancel(); artwork?.Dispose(); artwork = null; };
        await dialog.ShowDialog(this);
        if (!lifetime.IsCancellationRequested) Render();
    }

    private static (StackPanel Panel, Image Image) CreationModDetailsPanel(ModMetadata mod, Action? add)
    {
        var panel = new StackPanel { Spacing = 14 };
        panel.Children.Add(Label(mod.Name, 23, true));
        panel.Children.Add(Label("ID " + mod.Id + " · " + mod.Edition, 15));
        var image = new Image { Width = 128, Height = 128, Stretch = Stretch.Uniform,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, IsVisible = false };
        panel.Children.Add(image);
        panel.Children.Add(Label(mod.Description.Length > 1200 ? mod.Description[..1200] + "…" : mod.Description, 16));
        panel.Children.Add(new Expander { Header = "More details", Content = Label("Updated: " + mod.UpdatedAt.ToLocalTime().ToString("g")
            + "\n" + string.Join(", ", mod.Tags), 15) });
        panel.Children.Add(ActionButton("Add to this server’s selection", add, true));
        return (panel, image);
    }

    public static Window CreateCreationModDetailsDesignReview()
    {
        var dialog = new Window { Title = "Find a mod", Width = 620, Height = 680, Background = Brush.Parse("#0F1921"),
            FontFamily = new FontFamily("Segoe UI, Inter, sans-serif") };
        var body = new StackPanel { Margin = new Thickness(28), Spacing = 18 };
        body.Children.Add(Label("Find a mod", 27, true));
        body.Children.Add(Label("Design preview · Illustrative details · No online lookup or selection changes.", 16));
        body.Children.Add(new TextBox { PlaceholderText = "Mod ID", Text = "928708", IsEnabled = false });
        body.Children.Add(ActionButton("Find details", null, true));
        var (panel, _) = CreationModDetailsPanel(new("928708", ArkEdition.ASA, "Illustrative building mod",
            "Additional building pieces and crafting options. Review the author's requirements and supported player platforms before selecting a mod.",
            null, new Uri("https://www.curseforge.com/ark-survival-ascended"), DateTimeOffset.Parse("2026-10-12T00:00:00Z"), ["Building", "Crafting"]), null);
        body.Children.Add(panel); body.Children.Add(ActionButton("Close", null));
        dialog.Content = new ScrollViewer { Content = body }; return dialog;
    }

    private static async Task<Bitmap?> ReadModArtworkAsync(Uri picture, CancellationToken ct)
    {
        var host = picture.Host;
        if (picture.Scheme != "https" || picture.UserInfo.Length > 0 || picture.Port != 443
            || !(host == "forgecdn.net" || host.EndsWith(".forgecdn.net", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".steamusercontent.com", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase))) return null;
        try
        {
            using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) };
            using var response = await http.GetAsync(picture, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return null;
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            using var bytes = new MemoryStream(); var chunk = new byte[16384];
            int count;
            while ((count = await input.ReadAsync(chunk, ct)) > 0)
            {
                if (bytes.Length + count > 2 * 1024 * 1024) return null;
                bytes.Write(chunk, 0, count);
            }
            bytes.Position = 0; return Bitmap.DecodeToWidth(bytes, 128);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or ArgumentException) { return null; }
    }
}
