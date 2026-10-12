using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ArkServerManager.Core;
using System.Security.Cryptography;
using System.Text.Json;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private sealed class AccessEditor
    {
        public ServerAccessReview? Review;
        public string Admin = "", Join = "", Notice = "Load current access settings to edit this map.";
        public bool RemoveJoin, Reveal, Busy, Attempted;
        public object? Pending;
        public string Kind = "";
        public Guid RequestId;
    }
    private readonly Dictionary<Guid, AccessEditor> accessEditors = [];

    private void AddAccessSettings(StackPanel body, ServerDefinition server)
    {
        if (!accessEditors.TryGetValue(server.Id, out var editor)) accessEditors[server.Id] = editor = new();
        body.Children.Add(Label(editor.Notice, 15));
        if (editor.Review is { } review)
        {
            body.Children.Add(Label("Administrator password: " + (review.AdministratorConfigured ? "configured" : "required"), 15));
            body.Children.Add(Label("Join password: " + (review.JoinPasswordConfigured ? "configured" : "none"), 15));
            if (review.PendingId is not null)
            {
                body.Children.Add(Label(review.Applying ? "A password change needs recovery. Starting this map retries the saved change."
                    : "Password changes are saved for the next start.", 15));
                if (!review.Applying)
                {
                    creationFooter.IsVisible = true;
                    creationFooter.Children.Add(ActionButton("Cancel saved password changes…",
                        editor.Busy || editor.Pending is not null || client is null ? null : () => _ = RunAccessRequestAsync(server, editor, true)));
                }
            }
            else
            {
                var enabled = !editor.Busy && editor.Pending is null;
                var admin = new TextBox { Text = editor.Admin, PasswordChar = editor.Reveal ? '\0' : '●', MaxLength = 1024, IsEnabled = enabled };
                var join = new TextBox { Text = editor.Join, PasswordChar = editor.Reveal ? '\0' : '●', MaxLength = 1024, IsEnabled = enabled && !editor.RemoveJoin };
                admin.TextChanged += (_, _) => editor.Admin = admin.Text ?? "";
                join.TextChanged += (_, _) => editor.Join = join.Text ?? "";
                body.Children.Add(DraftField("New administrator password", admin, "Leave blank to keep the current password."));
                body.Children.Add(ActionButton("Generate administrator password", enabled ? () =>
                    { editor.Admin = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)); Render(); } : null));
                body.Children.Add(DraftField("New join password", join, "Leave blank to keep the current password."));
                var clear = new CheckBox { Content = "Remove the join password", IsChecked = editor.RemoveJoin, IsEnabled = enabled };
                clear.IsCheckedChanged += (_, _) => { editor.RemoveJoin = clear.IsChecked == true; join.IsEnabled = enabled && !editor.RemoveJoin; };
                body.Children.Add(clear);
                var reveal = new CheckBox { Content = "Show entered passwords", IsChecked = editor.Reveal };
                reveal.IsCheckedChanged += (_, _) => { editor.Reveal = reveal.IsChecked == true; admin.PasswordChar = join.PasswordChar = editor.Reveal ? '\0' : '●'; };
                body.Children.Add(reveal);
                Avalonia.Automation.AutomationProperties.SetName(admin, "New administrator password");
                Avalonia.Automation.AutomationProperties.SetName(join, "New join password");
                creationFooter.IsVisible = true;
                creationFooter.Children.Add(ActionButton("Save access changes…", client is null || !enabled ? null : () => _ = RunAccessRequestAsync(server, editor, false), true));
            }
        }
        if (editor.Pending is not null)
        {
            body.Children.Add(ActionButton("Check existing action", client is null || editor.Busy ? null : () => _ = RunAccessRequestAsync(server, editor, false), true));
            if (!editor.Busy) body.Children.Add(ActionButton("Dismiss unknown result", () =>
            {
                editor.Pending = null; editor.Review = null; editor.Admin = editor.Join = "";
                editor.Notice = "The action may still complete. Load current access settings before another change.";
                Render();
            }));
        }
        else body.Children.Add(ActionButton("Load current access settings", client is null || editor.Busy ? null : () => _ = LoadAccessSettingsAsync(server, editor)));
    }

    private async Task LoadAccessSettingsAsync(ServerDefinition server, AccessEditor editor)
    {
        if (client is null || editor.Busy || editor.Pending is not null) return;
        editor.Busy = true; Render();
        try
        {
            var review = await client.CallAsync<ServerAccessReview>("server-access-read", server.Id, TimeSpan.FromSeconds(8), lifetime.Token);
            if (review is null || review.ServerId != server.Id || review.Edition != server.Edition || review.Directory != server.Directory
                || review.SourceSHA256 is not { Length: 64 } || review.SourceSHA256.Any(c => !char.IsAsciiHexDigit(c))) throw new InvalidDataException();
            editor.Review = review; editor.Admin = editor.Join = ""; editor.RemoveJoin = false;
            editor.Notice = "Changes apply only to this map. Existing passwords are kept unless you replace or remove them.";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsBasicSaveError(error))
        { editor.Review = null; editor.Notice = "Access settings could not be loaded. Refresh and try again."; }
        finally { editor.Busy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }

    private async Task RunAccessRequestAsync(ServerDefinition server, AccessEditor editor, bool discard)
    {
        if (client is null || editor.Busy || editor.Review is null) return;
        editor.Busy = true; Render();
        try
        {
            if (editor.Pending is null)
            {
                var dialog = new Window { Title = discard ? "Cancel password changes?" : "Save access changes?", Width = 570, Height = discard ? 310 : 430,
                    CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush.Parse("#0F1921"), FontFamily = FontFamily };
                var panel = new StackPanel { Margin = new Thickness(28), Spacing = 16 };
                panel.Children.Add(Label(dialog.Title!, 26, true));
                if (discard)
                {
                    if (editor.Review.PendingId is not Guid pendingId || editor.Review.Applying) return;
                    panel.Children.Add(Label("Remove the queued password changes. Current server access stays as it is.", 16));
                    panel.Children.Add(ActionButton("Cancel saved changes", () => dialog.Close(SaveChoice.SaveForNextStart), true));
                    panel.Children.Add(ActionButton("Keep them", () => dialog.Close(SaveChoice.Cancel)));
                    dialog.Content = new ScrollViewer { Content = panel };
                    if (await dialog.ShowDialog<SaveChoice>(this) == SaveChoice.Cancel) return;
                    editor.Pending = new ServerAccessDiscard(server.Id, pendingId, true); editor.Kind = "server-access-discard";
                }
                else
                {
                    var change = new ServerAccessChange(editor.Review, editor.Admin.Length == 0 ? null : editor.Admin,
                        editor.RemoveJoin ? "" : editor.Join.Length == 0 ? null : editor.Join);
                    if (change.AdministratorPassword is null && change.JoinPassword is null)
                    { editor.Notice = "Enter a new password or choose to remove the join password."; return; }
                    if (change.AdministratorPassword is not null) ServerAccessConfiguration.ValidatePasswords(change.AdministratorPassword, change.JoinPassword ?? "");
                    panel.Children.Add(Label("If stopped, changes apply now. If running, choose when to apply them. Restart affects only this map.", 16));
                    panel.Children.Add(ActionButton("Save · apply on next start if running", () => dialog.Close(SaveChoice.SaveForNextStart), true));
                    panel.Children.Add(ActionButton("Save · restart this map if running", () => dialog.Close(SaveChoice.SaveAndRestart)));
                    panel.Children.Add(ActionButton("Cancel", () => dialog.Close(SaveChoice.Cancel)));
                    dialog.Content = new ScrollViewer { Content = panel };
                    var choice = await dialog.ShowDialog<SaveChoice>(this); if (choice == SaveChoice.Cancel) return;
                    editor.Pending = new ServerAccessSave(change, choice, true); editor.Kind = "server-access-save";
                }
                editor.RequestId = Guid.NewGuid(); editor.Attempted = false;
            }
            BackgroundOperation job;
            if (!editor.Attempted)
            {
                editor.Attempted = true;
                job = await client.CallAsync<BackgroundOperation>(editor.Kind + "-start", editor.Pending, TimeSpan.FromSeconds(8), lifetime.Token, editor.RequestId);
            }
            else job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(editor.RequestId), TimeSpan.FromSeconds(8), lifetime.Token);
            while (true)
            {
                if (job.Id != editor.RequestId || job.Kind != editor.Kind || !Enum.IsDefined(job.State)) throw new InvalidDataException();
                if (job.State == BackgroundOperationState.Succeeded)
                {
                    string notice;
                    if (editor.Kind == "server-access-save")
                    {
                        var result = job.Result?.Deserialize<ServerAccessSaveResult>();
                        if (result is null || result.ServerId != server.Id || result.Save is null) throw new InvalidDataException();
                        notice = result.Save.Message;
                    }
                    else
                    {
                        var result = job.Result?.Deserialize<ServerAccessDiscardResult>();
                        if (result is null || result.ServerId != server.Id || !result.Discarded) throw new InvalidDataException();
                        notice = "Saved password changes canceled.";
                    }
                    editor.Pending = null; editor.Review = null; editor.Admin = editor.Join = ""; editor.Notice = notice;
                    await RefreshAsync(); break;
                }
                if (job.State is BackgroundOperationState.Failed or BackgroundOperationState.Canceled)
                {
                    editor.Pending = null; editor.Review = null; editor.Admin = editor.Join = "";
                    editor.Notice = "The action did not complete. Load current access settings to review any saved change."; await RefreshAsync(); break;
                }
                editor.Notice = job.Progress?.Message ?? "Saving access settings…"; Render();
                await Task.Delay(1000, lifetime.Token);
                job = await client.CallAsync<BackgroundOperation>("operation-status", new OperationStatusRequest(editor.RequestId), TimeSpan.FromSeconds(8), lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsBasicSaveError(error))
        { editor.Notice = editor.Pending is null ? "Review the entered passwords before saving." : "Outcome unknown. Check the existing action before trying again."; }
        finally { editor.Busy = false; if (!lifetime.IsCancellationRequested) Render(); }
    }
}
