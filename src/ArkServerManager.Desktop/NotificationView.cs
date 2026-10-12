using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Media;
using Avalonia.Threading;
using ArkServerManager.Core;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private NotificationPreferences notificationPreferences = new();
    private readonly ActivityNotificationTracker notificationTracker = new();
    private WindowNotificationManager? notificationManager;
    private IDesktopNotifications? desktopNotifications;
    private bool desktopNotificationSending;
    private DispatcherTimer? notificationTimer;
    private bool notificationPolling;
    private string notificationPreferenceNotice = "";
    private int newActivityCount;
    private Border? notificationBadge;
    private TextBlock? notificationBadgeText;
    private Button? notificationEntry;
    private void UpdateNotificationBadge()
    {
        if (notificationBadge is null || notificationBadgeText is null) return;
        notificationBadge.IsVisible = notificationPreferences.InApp && newActivityCount > 0;
        notificationBadgeText.Text = newActivityCount >= 100 ? "99+" : newActivityCount.ToString();
        if (notificationEntry is not null) Avalonia.Automation.AutomationProperties.SetName(notificationEntry,
            notificationBadge.IsVisible ? "Open activity and notifications · " + notificationBadgeText.Text + " new events" : "Open activity and notifications");
    }
    private AtomicJsonStore<NotificationPreferences> NotificationStore() => new(
        Path.Combine(managerRoot ?? throw new InvalidOperationException("Choose a management folder first."), "app-settings", "notifications.json"), true);

    private async Task StartNotificationsAsync()
    {
        if (designPreview || client is null) return;
        try
        {
            notificationPreferences = await NotificationStore().LoadAsync(lifetime.Token) ?? new();
            notificationPreferences.Validate();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            notificationPreferences = new() { InApp = false, OperatingSystem = false };
            notificationPreferenceNotice = "Notification preferences could not be loaded. Review and save them to enable alerts.";
        }
        notificationManager = new(this) { Position = NotificationPosition.BottomRight, MaxItems = 2 };
        if (OperatingSystem.IsWindows()) Win32Properties.AddWndProcHookCallback(this, NotificationWindowMessage);
        notificationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        notificationTimer.Tick += async (_, _) => await PollNotificationsAsync();
        notificationTimer.Start();
    }

    private IntPtr NotificationWindowMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WindowsDesktopNotifications.CallbackMessage || wParam.ToInt64() != 173) return IntPtr.Zero;
        if (lParam.ToInt64() is 0x405 or 0x202)
        {
            handled = true;
            Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate();
            ResetPage(); activityPage = true; Render();
        }
        return IntPtr.Zero;
    }

    private async Task PollNotificationsAsync()
    {
        if (client is null || notificationPolling || activityBusy || lifetime.IsCancellationRequested) return;
        notificationPolling = true;
        try
        {
            var latest = await client.CallAsync<ActivityHistory>("activity", null, TimeSpan.FromSeconds(8), lifetime.Token);
            NotifyNewActivity(latest);
            // Polling must not rebuild an open form or replace the user's draft.
            history = latest;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException or System.Text.Json.JsonException) { }
        finally { notificationPolling = false; }
    }

    private void NotifyNewActivity(ActivityHistory latest)
    {
        var events = notificationTracker.Observe(latest, notificationPreferences);
        if (events.Count == 0) return;
        if (notificationPreferences.InApp)
        {
            newActivityCount = Math.Min(100, newActivityCount + events.Count); UpdateNotificationBadge();
        }
        if (activityPage && IsActive) return;
        var errors = events.Count(e => e.Severity == ActivitySeverity.Error);
        var warnings = events.Count(e => e.Severity == ActivitySeverity.Warning);
        var message = events.Count == 1 ? events[0].Severity switch
        {
            ActivitySeverity.Error => "An operation or server needs attention. Open activity for details.",
            ActivitySeverity.Warning => "A warning was recorded. Open activity for details.",
            _ => "An operation finished. Open activity for details."
        } : $"{events.Count} new events · {errors} errors · {warnings} warnings. Open activity for details.";
        if (notificationPreferences.OperatingSystem) _ = SendDesktopNotificationAsync(message,
            errors > 0 ? ActivitySeverity.Error : warnings > 0 ? ActivitySeverity.Warning : ActivitySeverity.Information);
        if (notificationPreferences.InApp) notificationManager?.Show(new Notification("ARK Server Manager", message,
            errors > 0 ? NotificationType.Error : warnings > 0 ? NotificationType.Warning : NotificationType.Information,
            TimeSpan.FromSeconds(8), () => { ResetPage(); activityPage = true; Render(); }));
    }

    private async Task SendDesktopNotificationAsync(string message, ActivitySeverity severity)
    {
        if (designPreview || lifetime.IsCancellationRequested || desktopNotificationSending) return;
        desktopNotificationSending = true;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                desktopNotifications ??= new WindowsDesktopNotifications(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
                desktopNotifications.Show(message, severity);
            }
            else if (OperatingSystem.IsLinux())
            {
                await Task.Run(() =>
                {
                    desktopNotifications ??= new LinuxDesktopNotifications();
                    desktopNotifications.Show(message, severity);
                }, lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or ArgumentException or DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        { notificationPreferenceNotice = "Operating-system alerts are unavailable. New events remain in activity; in-app alerts follow your settings."; }
        finally { desktopNotificationSending = false; }
    }

    private void AddNotificationPreferences(StackPanel body)
    {
        var enabled = new CheckBox { Content = "Show alerts inside the app", IsChecked = notificationPreferences.InApp };
        var operatingSystem = new CheckBox { Content = "Show operating-system alerts", IsChecked = notificationPreferences.OperatingSystem };
        var warnings = new CheckBox { Content = "Warnings and errors", IsChecked = notificationPreferences.WarningsAndErrors };
        var completed = new CheckBox { Content = "Completed operations", IsChecked = notificationPreferences.CompletedOperations };
        body.Children.Add(enabled); body.Children.Add(operatingSystem); body.Children.Add(warnings); body.Children.Add(completed);
        body.Children.Add(Label("New events are grouped into one alert. Existing history remains available through the bell.", 16));
        if (OperatingSystem.IsLinux()) body.Children.Add(Label("Enabling desktop alerts checks required support and installs missing libraries. Linux may ask for permission.", 16));
        body.Children.Add(ActionButton("Save notification settings…", designPreview || managerRoot is null ? null :
            () => _ = SaveNotificationPreferencesAsync(new() { InApp = enabled.IsChecked == true,
                OperatingSystem = operatingSystem.IsChecked == true,
                WarningsAndErrors = warnings.IsChecked == true, CompletedOperations = completed.IsChecked == true })));
        if (notificationPreferenceNotice.Length > 0) body.Children.Add(Label(notificationPreferenceNotice, 16));
    }

    private async Task SaveNotificationPreferencesAsync(NotificationPreferences desired)
    {
        var dialog = new Window { Title = "Save notification settings?", Width = 520, Height = 280,
            Background = Brush.Parse("#0F1921"), FontFamily = FontFamily, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var body = new StackPanel { Margin = new Thickness(28), Spacing = 18 };
        body.Children.Add(Label("Save notification settings?", 23, true));
        body.Children.Add(Label("These preferences control new alerts. Recorded activity is kept.", 16));
        body.Children.Add(ActionButton("Save", () => dialog.Close(true), true));
        body.Children.Add(ActionButton("Cancel", () => dialog.Close(false))); dialog.Content = body;
        if (!await dialog.ShowDialog<bool>(this)) return;
        try
        {
            await NotificationStore().SaveAsync(desired, lifetime.Token);
            notificationPreferences = desired; notificationPreferenceNotice = "Notification settings saved.";
            if (!desired.InApp) newActivityCount = 0;
            UpdateNotificationBadge();
            if (!desired.OperatingSystem) { desktopNotifications?.Dispose(); desktopNotifications = null; }
            else if (OperatingSystem.IsLinux() && client is not null)
            {
                await LinuxNotificationSetup.EnsureAsync(client, text => notificationPreferenceNotice = text, lifetime.Token);
                notificationPreferenceNotice = "Notification settings saved. Desktop notification support is ready.";
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException or System.Text.Json.JsonException)
        { notificationPreferenceNotice = "Notification setup could not finish. Review your current preferences and activity before retrying."; }
        if (!lifetime.IsCancellationRequested) Render();
    }
}
