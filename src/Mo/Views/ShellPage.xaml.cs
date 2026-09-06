using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Mo.Helpers;
using Mo.Services;

namespace Mo.Views;

public sealed partial class ShellPage : Page
{
    private readonly INavigationService _navigationService;

    public ShellPage()
    {
        InitializeComponent();

        _navigationService = App.Services.GetRequiredService<INavigationService>();
        _navigationService.Frame = ContentFrame;

        // Apply localized strings. The ToolTip matters in LeftCompact mode, where the
        // rail is icons-only until the user expands it.
        NavProfilesItem.Content = ResourceHelper.GetString("NavProfiles");
        NavDisplayTuningItem.Content = ResourceHelper.GetString("NavDisplayTuning");
        NavSettingsItem.Content = ResourceHelper.GetString("NavSettings");
        ToolTipService.SetToolTip(NavProfilesItem, NavProfilesItem.Content);
        ToolTipService.SetToolTip(NavDisplayTuningItem, NavDisplayTuningItem.Content);
        ToolTipService.SetToolTip(NavSettingsItem, NavSettingsItem.Content);

        // NavigationView.IsBackEnabled defaults to false and nothing set it, so the back
        // arrow rendered but could never be pressed. Keep it in step with the frame.
        ContentFrame.Navigated += (_, _) =>
        {
            NavView.IsBackEnabled = ContentFrame.CanGoBack;
            SyncSelectedNavItem();
        };

        Loaded += ShellPage_Loaded;
    }

    private void ShellPage_Loaded(object sender, RoutedEventArgs e)
    {
        _menuScrollViewer = FindDescendantByName<ScrollViewer>(NavView, "MenuItemsScrollViewer");

        // Honour the same system setting WinUI's own indicator honours: someone who
        // turned animations off should not get ours instead.
        try { _systemAnimationsEnabled = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled; }
        catch { _systemAnimationsEnabled = true; }

        BootLog.Write("nav.init",
            $"menuScrollViewer={(_menuScrollViewer == null ? "not found" : "found")}, " +
            $"systemAnimations={_systemAnimationsEnabled}");

        ApplySelectionBarBrush();
        ShellRoot.ActualThemeChanged += (_, _) =>
        {
            ApplySelectionBarBrush();
            UpdateSelectionBar(animate: false);
        };

        // Nothing here is measurable at Loaded, and the pane resizes again on its own
        // when the compact rail expands, so every one of these restarts the fit.
        NavView.SizeChanged += (_, _) => { RestartSpacerFit(); UpdateSelectionBar(animate: false); };
        NavView.PaneOpened += (_, _) => { RestartSpacerFit(); UpdateSelectionBar(animate: false); };
        NavView.PaneClosed += (_, _) => { RestartSpacerFit(); UpdateSelectionBar(animate: false); };
        if (_menuScrollViewer != null)
            _menuScrollViewer.SizeChanged += (_, _) => RestartSpacerFit();

        RestartSpacerFit();

        NavView.SelectedItem = NavView.MenuItems[0];
        _navigationService.NavigateTo(typeof(ProfileListPage));
    }

    private ScrollViewer? _menuScrollViewer;

    // ── Selection bar ──

    private const double SelectionBarDurationMs = 250;

    /// <summary>Left inset of the stock indicator inside its item, from the presenter's
    /// `NavigationViewItemButtonMargin`. Only used when the part cannot be found.</summary>
    private const double SelectionBarInsetX = 4;

    private bool _systemAnimationsEnabled = true;

    /// <summary>Moves our own bar to the selected item. The stock one is transparent: it
    /// lives inside a 36px rounded, and therefore clipping, `LayoutRoot`, while the
    /// animation draws travel by scaling a 16px rectangle to the full distance. Crossing
    /// 400px asks for 416px of growth and gets 26.</summary>
    private void UpdateSelectionBar(bool animate)
    {
        if (NavView.SelectedItem is not NavigationViewItem item || item.ActualHeight <= 0)
        {
            SelectionBar.Opacity = 0;
            return;
        }

        if (!TryGetBarOrigin(item, out var origin)) return;

        // An item scrolled out of the pane would drag the bar off with it.
        if (origin.Y < 0 || origin.Y > ShellRoot.ActualHeight) { SelectionBar.Opacity = 0; return; }

        SelectionBar.Opacity = 1;
        SelectionBarTransform.X = origin.X;

        double target = origin.Y;
        if (!animate || !_systemAnimationsEnabled || Math.Abs(SelectionBarTransform.Y - target) < 0.5)
        {
            SelectionBarTransform.Y = target;
            return;
        }

        var move = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(SelectionBarDurationMs),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
            {
                EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut,
            },
            EnableDependentAnimation = true,
        };

        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(move, SelectionBarTransform);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(move, "Y");

        _selectionBarStoryboard?.Stop();
        _selectionBarStoryboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        _selectionBarStoryboard.Children.Add(move);
        _selectionBarStoryboard.Begin();
    }

    private Microsoft.UI.Xaml.Media.Animation.Storyboard? _selectionBarStoryboard;

    /// <summary>Where the bar belongs, in shell coordinates. Prefers the stock indicator's
    /// own position so it matches WinUI exactly at any pane width or DPI, and falls back
    /// to the item's box if a future SDK renames that template part.</summary>
    private bool TryGetBarOrigin(NavigationViewItem item, out Windows.Foundation.Point origin)
    {
        origin = default;
        try
        {
            var anchor = FindDescendantByName<Microsoft.UI.Xaml.Shapes.Rectangle>(item, "SelectionIndicator");
            if (anchor != null)
            {
                origin = anchor.TransformToVisual(ShellRoot).TransformPoint(new Windows.Foundation.Point(0, 0));
                return true;
            }

            var box = item.TransformToVisual(ShellRoot).TransformPoint(new Windows.Foundation.Point(0, 0));
            origin = new Windows.Foundation.Point(
                box.X + SelectionBarInsetX,
                box.Y + ((item.ActualHeight - SelectionBar.Height) / 2));
            return true;
        }
        catch { return false; }
    }

    /// <summary>Paints the bar with the brush the stock indicator would have used, read
    /// past this page's transparent override. Straight to the accent brush would be wrong
    /// in high contrast, where that key resolves elsewhere.</summary>
    private void ApplySelectionBarBrush()
    {
        const string key = "NavigationViewSelectionIndicatorForeground";
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
            SelectionBar.Fill = brush;
    }

    /// <summary>Pushes Settings to the bottom of the pane with a margin, so the three
    /// destinations stay adjacent in one items host and the selection bar can animate
    /// between them.</summary>
    /// <remarks>Corrects against the host's measured extent rather than computing the gap
    /// from item heights. Those miss the repeater's own spacing and padding, and guessing
    /// a pixel too many is what put a scrollbar in the pane.</remarks>
    private void UpdateSpacerHeight()
    {
        if (_menuScrollViewer is not { } host) return;
        if (host.ViewportHeight <= 0 || host.ExtentHeight <= 0) return;

        // One pixel of slack: landing exactly on the viewport height is the boundary a
        // scrollbar appears at, and rounding decides which side of it we end up on.
        double overflow = host.ExtentHeight - (host.ViewportHeight - 1);
        if (Math.Abs(overflow) < 1) return;

        var margin = NavDisplayTuningItem.Margin;
        double gap = Math.Max(0, margin.Bottom - overflow);
        if (Math.Abs(margin.Bottom - gap) < 1) return;

        NavDisplayTuningItem.Margin = new Thickness(margin.Left, margin.Top, margin.Right, gap);

        // Changing a margin does not resize the host, so nothing would call this again.
        // Bounded, because a layout that will not settle must not spin forever.
        if (_spacerPasses++ < MaxSpacerPasses)
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, UpdateSpacerHeight);

        // The bar is placed off the item's position, which just moved.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => UpdateSelectionBar(animate: false));
    }

    private const int MaxSpacerPasses = 8;
    private int _spacerPasses;

    private void RestartSpacerFit()
    {
        _spacerPasses = 0;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, UpdateSpacerHeight);
    }

    private static T? FindDescendantByName<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed && typed.Name == name) return typed;

            if (FindDescendantByName<T>(child, name) is { } found) return found;
        }
        return null;
    }

    /// <summary>Keeps the rail highlight on the section the frame is actually showing —
    /// otherwise going back leaves it on whatever was clicked last.</summary>
    private void SyncSelectedNavItem()
    {
        var current = ContentFrame.CurrentSourcePageType;

        // The editor is a detail view of the profile list, so it keeps that section lit.
        if (current == typeof(ProfileListPage) || current == typeof(ProfileEditorPage))
            NavView.SelectedItem = NavProfilesItem;
        else if (current == typeof(DisplayTuningPage))
            NavView.SelectedItem = NavDisplayTuningItem;
        else if (current == typeof(SettingsPage))
            NavView.SelectedItem = NavSettingsItem;

        // After the item is selected, and after layout has caught up with it.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => UpdateSelectionBar(animate: true));
    }

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem item)
        {
            var pageType = item.Tag?.ToString() switch
            {
                "Profiles" => typeof(ProfileListPage),
                "DisplayTuning" => typeof(DisplayTuningPage),
                "Settings" => typeof(SettingsPage),
                _ => typeof(ProfileListPage),
            };
            _navigationService.NavigateTo(pageType);
        }
    }

    private void NavView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        _navigationService.GoBack();
    }
}
