# UI responsiveness

The policy is in the global preferences file: heavy work off the UI thread, always show
that it is running, never show "empty" before the load finishes. This file is how that
lands in WinUI 3, and what Mo got wrong.

## The apply is the heavy one

`DisplayService.ApplyProfile` talks to drivers and sleeps: `UnstickCursor` alone costs
about a second, `CursorPlaneReset` blanks the panels for as long as they take to come
back, and the topology-extend retry waits up to three seconds. None of that may run on
the dispatcher.

`ProfileService.ApplyProfileAsync` is the single choke point every caller goes through,
so the hop to the background belongs there and nowhere else. Do not sprinkle `Task.Run`
at the call sites; one of them will be forgotten.

`SendMessageTimeout(HWND_BROADCAST, …)` is the worst offender to get wrong. Its timeout
is **per window**, and while it waits, the calling thread pumps only sent messages: no
paint, no input. On the UI thread one slow tray app freezes Mo.

## A page constructor runs on the dispatcher

Navigation cannot paint until it returns, so nothing in it may touch hardware, and
nothing may resolve a service whose constructor does.

`SettingsPage` built its rotation-method list there, which resolved
`NvidiaRotationService` and `AmdRotationService` for the first time. Both probe for
adapters in their own constructors, loading `nvapi64.dll` and `atiadlxx.dll`, so pressing
Settings sat on a driver enumeration before the page appeared. The list feeds a combo
inside an expander that is `Visibility="Collapsed"`, so none of it was ever seen.

It fills from `RotationMethodCombo_Loaded` now, on a pool thread, which never runs while
that expander stays hidden. `SystemInfoService.GetDebugReportAsync` moved the same way:
the full hardware report was built on entry to Settings for a panel almost nobody opens.

Both are the same mistake. Before putting work in a constructor, ask what it costs and
whether anyone will look at the result.

## Empty is not the same as "not loaded yet"

`ProfileListViewModel` used to derive its empty state from `Profiles.Count == 0` while
the constructor kicked off `LoadAsync` and forgot about it. Every launch therefore
showed "No profiles saved yet" for as long as the load took, to users who had profiles.

The shape that fixes it:

```csharp
[ObservableProperty] private bool _isLoaded;

public Visibility IsEmpty  => IsLoaded && Profiles.Count == 0 ? Visible : Collapsed;
public Visibility IsBusy   => IsLoaded ? Collapsed : Visible;
```

Both visibilities have to change when either input changes, so raise them together.
`[ObservableProperty]` is fine here: view models are not serialized, and the trap in
`10-code-style.md` is about models that are.

## A launch the user made produces a window

`StartMinimized` applies to an automatic launch only. Windows starting Mo at sign-in is
recognised by `App.StartupLaunchArgument` on the HKCU Run entry, or by a StartupTask
activation on a packaged build; anything else is someone opening Mo, and they get the
window. Starting hidden on a hand launch is indistinguishable from failing to start,
which is the same complaint the single-instance redirect exists to prevent.

`StartupService.RepairRegistryEntry` rewrites an entry left by an older build or a
previous install path, so the distinction keeps working after an upgrade.

## Every dialog goes through ShowThemedAsync

A `ContentDialog` lives in a popup rooted at the `XamlRoot`, not under the window's
content, so the `RequestedTheme` that themes the app never reaches it. Pick Dark while
Windows is Light and every dialog comes up Light. `ThemeHelper.ShowThemedAsync` copies
the host's `ActualTheme` onto the dialog first; call it instead of `ShowAsync`.

The caption buttons have the same shape of problem and the same kind of fix
(`ApplyCaptionButtonColors`): they belong to the window, not to its content.

## Every navigation destination lives in one list

`IsSettingsVisible` is off and Settings is an ordinary `MenuItems` entry, pushed to the
bottom by the `NavSpacer` border whose height `ShellPage.UpdateSpacerHeight` sets from the
pane's viewport.

The selection bar is **not** a shared element in the pane. Read out of the shipped
`generic.xaml`: each `NavigationViewItemPresenter` owns a `SelectionIndicator` rectangle
inside its own `LayoutRoot`, and a selection change offset-animates the *destination*
item's rectangle from the previous item's position. That rectangle therefore has to
render outside its own `ScrollViewer` for the whole flight.

`MenuItems` and `FooterMenuItems` sit in two different scroll hosts
(`MenuItemsScrollViewer`, `FooterItemsScrollViewer`), and `ScrollContentPresenter` clips
to the viewport, so a bar crossing that seam is cut off mid-travel. Setting
`ScrollViewer.CanContentRenderOutsideBounds` and clearing the hosts' backgrounds were both
tried on a real window and neither helped.

`MenuItemsScrollViewer` occupies row 0 of `ItemsContainerGrid` at `*` height, so it spans
the whole pane. Inside that one host the bar is never clipped, which is why the margin
works and the footer does not. Do not move Settings to `FooterMenuItems`.

**Push it down with a margin on the item above, never with a spacer item.** Anything added
to `MenuItems` is wrapped in a container of NavigationView's own making, so a spacer is
clickable, and it makes Settings non-adjacent to its neighbour.

## The stock selection bar cannot travel, at any distance

`ShellPage` hides it and draws its own. This is not preference; the built-in one is
structurally incapable of showing the movement.

`NavigationView` draws travel by scaling the indicator: `Scale.Y` peaks at
`|to - from| / 16 + 1` over 600 ms, while `Offset.Y` holds and snaps at the 200 ms mark.
The indicator is a 3x16 `Rectangle` inside `LayoutRoot`, a 36px `Grid` with
`CornerRadius` 4 template-bound from the item. A non-zero corner radius installs a clip,
so the rectangle has **10px of headroom above and below and can grow 26px at most**.

Crossing 400px asks for 416px of growth and gets 26, so 1.6% of the movement is drawn.
Even a 40px hop between neighbours needs 56px and is cut off. Nothing exposes that clip:
no property, no resource, no attached knob. Upstream #5332 and #5275 report the symptom;
both are closed. The rounded pill arrived in the Windows 11 restyle and the animation code
was never updated to match.

Duration is 600 ms, so speed was never the explanation.

Ours is suppressed with one scoped override, which works because the template binds
`Fill` to a `ThemeResource`:

```xml
<Page.Resources>
    <SolidColorBrush x:Key="NavigationViewSelectionIndicatorForeground" Color="Transparent" />
</Page.Resources>
```

A `Style` cannot reach a template *part*, so hiding it any other way means vendoring the
whole template. Keep the override on the page, not the app.

`UpdateSelectionBar` positions ours off the stock indicator's own transform, so it matches
WinUI at any pane width and DPI, and falls back to the item's box if that part is ever
renamed. It reads the real brush from `Application.Current.Resources`, past the page's
transparent override, so high contrast stays correct, and it skips its own animation when
`UISettings.AnimationsEnabled` is false.

Two other routes exist and both cost more than they return. Copying the whole
`NavigationView` template to put `CanContentRenderOutsideBounds="True"` on the two scroll
hosts would keep the footer *and* the animation, at the price of owning 370 lines of
vendored template that has to be re-merged on every SDK bump. Removing `x:Name` from
`PaneContentGrid` in a copied template makes `AnimateSelectionChanged` take its
no-animation branch, killing the travel everywhere.

Setting `CanContentRenderOutsideBounds` from code does **not** work: the clip belongs to
the inner `ScrollContentPresenter`, which reads its own copy of that property, and the
shipped template never binds the two together.

Upstream has no fix and none is scheduled; microsoft-ui-xaml #6957, #5253, #5332 and
#11042 are all closed unplanned, and #2878, asking merely for a way to turn the animation
off, has sat open since 2020.

## Work triggered per item is work done N times

`ProfileService.LoadAllAsync` adds profiles one at a time, so anything hanging off
`Profiles.CollectionChanged` runs once per profile. A CCD round trip there is what made
startup stutter. Both listeners coalesce now: `ProfileListViewModel.RefreshAvailability`
keeps one read in flight and re-runs once if asked again, and `App.QueueHotkeyRebind`
collapses a burst into a single re-bind on the next dispatcher turn.

The same rule caught `AutoSwitchService.Start`, whose baseline read now happens on a pool
thread, and the startup profile restore, whose compatibility check does two CCD round
trips a second and a half into launch.

## Do not put an ItemsRepeater in a page-level ScrollViewer

It virtualizes against that ScrollViewer, so a container whose height changes (a
`SettingsExpander` opening, say) makes it re-measure on every scroll tick and the content
visibly shimmers. A plain `ItemsControl` has no viewport to fight with. Reach for the
repeater when a list is long enough for virtualization to pay for itself, not for the
handful of rows a settings page shows.

## Marshalling back

Work started with `Task.Run` finishes on a pool thread. Anything touching a control or
an `ObservableCollection` bound to one has to come back through
`DispatcherQueue.TryEnqueue`. `App.xaml.cs` already does this for the hotkey
re-registration; follow that pattern rather than inventing another.
