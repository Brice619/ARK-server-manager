using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace ArkServerManager.Desktop;

public sealed partial class MainWindow
{
    private string? lastScreenRoute;
    private bool lastScreenWasDetail;
    private string? lastDetailIdentity;
    private int detailBodyStart;
    private Control? settingsCategoryBody;
    private int lastFadeManagementTab=-1,lastFadeSettingsCategory=-1;
    private int lastFadeConsoleSubTab=-1,lastFadePlayerCategory=-1;
    private Control? approvedConsoleOutput,approvedPlayerTable,approvedPlayerDetails;
    private readonly Dictionary<ScrollViewer, (DispatcherTimer Timer, double Target)> railAnimations = [];

    private void ApplyScreenFade()
    {
        var route = $"{selectedServer}:{selectedCluster}:{managementTab}:{settingsCategory}:{settingsPage}:{applicationCategory}:{creationPage}:{creationStep}:{activityPage}:{installationsPage}:{advancedEditorServer}:{configuredMod}:{modBrowser}:{selectedRestorePoint}:{approvedConsoleTab}:{playerCategory}";
        if (route == lastScreenRoute) return;
        var detail = selectedServer is not null || selectedCluster is not null;
        var identity = detail ? $"{selectedServer}:{selectedCluster}" : null;
        var sameDetail = detail && lastDetailIdentity == identity;
        var categoryOnly=sameDetail&&managementTab==1&&lastFadeManagementTab==1&&lastFadeSettingsCategory!=settingsCategory&&advancedEditorServer is null;
        var consoleOnly=sameDetail&&managementTab==5&&lastFadeManagementTab==5&&lastFadeConsoleSubTab!=approvedConsoleTab;
        var playersOnly=sameDetail&&managementTab==4&&lastFadeManagementTab==4&&lastFadePlayerCategory!=playerCategory;
        lastFadeConsoleSubTab=approvedConsoleTab;lastFadePlayerCategory=playerCategory;
        lastFadeManagementTab=managementTab;lastFadeSettingsCategory=settingsCategory;
        lastDetailIdentity = identity;
        lastScreenRoute = route;
        var allServers = !settingsPage && !creationPage && !activityPage && !installationsPage && !detail;
        var approved = detail || allServers || lastScreenWasDetail && (activityPage || creationPage);
        lastScreenWasDetail = detail;
        if (detail && managementTab == 0) Dispatcher.UIThread.Post(() => _ = ObserveOverviewEntryAsync());
        if (!approved) { content.Transitions = null; content.Opacity = 1; return; }
        if (sameDetail)
        {
            content.Transitions = null; content.Opacity = 1;
            Dispatcher.UIThread.Post(() =>
            {
                if (route != lastScreenRoute) return;
                var panels=categoryOnly&&settingsCategoryBody is not null?new[]{settingsCategoryBody}:consoleOnly&&approvedConsoleOutput is not null?new[]{approvedConsoleOutput}:playersOnly&&approvedPlayerTable is not null&&approvedPlayerDetails is not null?new[]{approvedPlayerTable,approvedPlayerDetails}:content.Children.Skip(detailBodyStart);
                foreach (var panel in panels)
                {
                    panel.Transitions = null; panel.Opacity = 0;
                    panel.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(220), Easing = new CubicEaseOut() }];
                    Dispatcher.UIThread.Post(() => panel.Opacity = 1, DispatcherPriority.Render);
                }
            }, DispatcherPriority.Render);
            return;
        }
        content.Transitions = null;
        content.Opacity = 0;
        content.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(220), Easing = new CubicEaseOut() }];
        Dispatcher.UIThread.Post(() => { if (!lifetime.IsCancellationRequested) content.Opacity = 1; }, DispatcherPriority.Render);
    }

    public void CompleteVisualReviewFrame()
    {
        content.Transitions = null; content.Opacity = 1;
        foreach (var panel in content.Children) { panel.Transitions = null; panel.Opacity = 1; }
        if(settingsCategoryBody is not null){settingsCategoryBody.Transitions=null;settingsCategoryBody.Opacity=1;}
        foreach(var panel in new[]{approvedConsoleOutput,approvedPlayerTable,approvedPlayerDetails})if(panel is not null){panel.Transitions=null;panel.Opacity=1;}
    }

    internal static void AddApprovedScrollbars(Application application)
    {
        var scrollbar = new Style(s => s.OfType<ScrollBar>());
        scrollbar.Setters.Add(new Setter(ScrollBar.AllowAutoHideProperty, false));
        scrollbar.Setters.Add(new Setter(TemplatedControl.TemplateProperty, ApprovedScrollbarTemplate()));
        application.Styles.Add(scrollbar);
    }

    private static FuncControlTemplate<ScrollBar> ApprovedScrollbarTemplate() => new((bar, scope) =>
        {
            var horizontal = bar.Orientation == Orientation.Horizontal;
            var thumb = new Thumb { MinWidth = horizontal ? 32 : 14, MinHeight = horizontal ? 14 : 32,
                Template = new FuncControlTemplate<Thumb>((_, _) => new Border { Background = Brush.Parse("#A5DFF9"), CornerRadius = new(7) }) };
            var track = new Track { Name = "PART_Track", Orientation = bar.Orientation, Thumb = thumb, IsDirectionReversed = !horizontal };
            track.Bind(Track.MinimumProperty, new Binding(nameof(ScrollBar.Minimum)) { Source = bar });
            track.Bind(Track.MaximumProperty, new Binding(nameof(ScrollBar.Maximum)) { Source = bar });
            track.Bind(Track.ValueProperty, new Binding(nameof(ScrollBar.Value)) { Source = bar, Mode = BindingMode.TwoWay });
            if (horizontal)
            {
                track.ViewportSize = Math.Max(0.01, bar.Maximum * 0.133);
                bar.PropertyChanged += (_, e) => { if (e.Property == ScrollBar.MaximumProperty) track.ViewportSize = Math.Max(0.01, bar.Maximum * 0.133); };
            }
            else track.Bind(Track.ViewportSizeProperty, new Binding(nameof(ScrollBar.ViewportSize)) { Source = bar });
            scope.Register("PART_Track", track);
            return new Border { Height = horizontal ? 14 : double.NaN, Width = horizontal ? double.NaN : 14,
                Background = Brush.Parse("#1D4D67"), CornerRadius = new(7), Child = track,
                Margin = new Thickness(0) };
        });

    private static void ConfigureScrollViewer(ScrollViewer viewer)
    {
        viewer.TemplateApplied += (_, e) =>
        {
            foreach (var name in new[] { "PART_HorizontalScrollBar", "PART_VerticalScrollBar" })
            {
                if (e.NameScope.Find<ScrollBar>(name) is not { } bar) continue;
                bar.Template = ApprovedScrollbarTemplate(); bar.AllowAutoHide = false;
                if (bar.Orientation == Orientation.Horizontal) { bar.Height = 14; bar.Margin = new(0); }
                else { bar.Width = 14; bar.Margin = new(0); }
            }
        };
    }

    private ScrollViewer CreateCardRail(Control cards)
    {
        var rail = new ScrollViewer { Content = cards, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, IsScrollChainingEnabled = false,
            VerticalContentAlignment = VerticalAlignment.Top, HorizontalContentAlignment = HorizontalAlignment.Left };
        ConfigureScrollViewer(rail);
        cards.VerticalAlignment = VerticalAlignment.Top;
        cards.Margin = new Thickness(0, 0, 0, 32);
        if (cards is Panel children && children.Children.LastOrDefault() is { } last)
            last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, 0, last.Margin.Bottom);
        if (cards is StackPanel panel && panel.Children.Count > 0)
        {
            var cardHeight = panel.Children.Max(c => (double.IsFinite(c.Height) ? c.Height : 352) + c.Margin.Top + c.Margin.Bottom);
            rail.Height = cardHeight + 32;
            void FitRail()
            {
                var overflow = rail.Extent.Width > rail.Viewport.Width + 0.5;
                var dashboard = selectedServer is null && selectedCluster is null && !creationPage && !settingsPage && !activityPage && !installationsPage;
                if (dashboard)
                {
                    var height = Math.Min(352, Math.Max(160, ((Bounds.Height > 0 ? Bounds.Height : Height) - 270) / 2 - (overflow ? 32 : 0)));
                    foreach(var card in panel.Children.OfType<Button>())
                    {
                        card.Height=height;
                        if(card.Content is StackPanel body && body.Children.FirstOrDefault() is Border artwork)artwork.Height=Math.Max(30,height-144);
                    }
                    cardHeight=height+10;
                }
                cards.Margin = new Thickness(0, 0, 0, overflow ? 32 : 0);
                rail.Height = cardHeight + (overflow ? 32 : 0);
            }
            rail.ScrollChanged += (_, _) => FitRail(); rail.SizeChanged += (_, _) => FitRail();
            void OnWindowSizeChanged(object? sender, SizeChangedEventArgs args) => FitRail();
            rail.AttachedToVisualTree += (_,_) => SizeChanged += OnWindowSizeChanged;
            rail.DetachedFromVisualTree += (_,_) => SizeChanged -= OnWindowSizeChanged;
        }
        rail.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            var amount = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? -e.Delta.X : -e.Delta.Y;
            if (amount == 0) return;
            var target = railAnimations.TryGetValue(rail, out var pending) ? pending.Target : rail.Offset.X;
            var clamped = Math.Clamp(target + amount * 150, 0, Math.Max(0, rail.Extent.Width - rail.Viewport.Width));
            if (Math.Abs(clamped - target) < 0.1) return;
            e.Handled = true; SmoothScrollTo(rail, clamped);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        rail.PointerPressed += (_, _) => { if (railAnimations.Remove(rail, out var motion)) motion.Timer.Stop(); };
        rail.DetachedFromVisualTree += (_, _) => { if (railAnimations.Remove(rail, out var motion)) motion.Timer.Stop(); };
        return rail;
    }

    private void SmoothScrollTo(ScrollViewer rail, double requested, bool vertical = false)
    {
        if (railAnimations.Remove(rail, out var old)) old.Timer.Stop();
        var start = vertical ? rail.Offset.Y : rail.Offset.X;
        var target = Math.Clamp(requested, 0, Math.Max(0, vertical ? rail.Extent.Height - rail.Viewport.Height : rail.Extent.Width - rail.Viewport.Width));
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        railAnimations[rail] = (timer, target);
        timer.Tick += (_, _) =>
        {
            var fraction = Math.Min(1, elapsed.Elapsed.TotalMilliseconds / 180);
            var eased = 1 - Math.Pow(1 - fraction, 3);
            rail.Offset = vertical ? new Vector(0, start + (target - start) * eased) : new Vector(start + (target - start) * eased, 0);
            if (fraction >= 1) { timer.Stop(); railAnimations.Remove(rail); }
        };
        timer.Start();
    }
}
