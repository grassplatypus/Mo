# Persistence

## JSON serialization

Source-generated `MoJsonContext` keeps the app trimming-safe:

```csharp
JsonSerializer.Serialize(profile, MoJsonContext.Default.DisplayProfile);
JsonSerializer.Deserialize(json, MoJsonContext.Default.AppSettings);
```

The `[ObservableProperty]` trap that breaks this contract is in `10-code-style.md`.

## Profile storage

- One JSON file per profile in `ApplicationData.Current.LocalFolder/profiles/`
  (unpackaged builds fall back to `%LOCALAPPDATA%/Mo/`).
- Settings in `ApplicationData.Current.LocalFolder/settings.json`.
- Both write via temp file + atomic replace, so an interrupted write cannot truncate
  them.
- Logs in `%LOCALAPPDATA%/Mo/logs/`: `boot.log`, `crash_*.log`, `startup_crash_*.log`,
  `nvapi_debug.log`.

`DisplayProfile.Description` is the **user's own note only**. Monitor count and
last-modified time are derived at render time; older builds baked untranslatable
English into the field, and `LegacyDescription.IsGenerated` clears those on load.

## Profile ordering

`DisplayProfile.SortOrder` is the user's own ordering, and it is load-bearing: the slot
hotkeys bind `<modifier>+1..9` to `Profiles[0..8]`, the next/previous hotkeys cycle in
this order, and the tray menu follows it.

Before it existed the order was `Directory.GetFiles` order, meaning GUID filenames, so
shortcuts pointed at arbitrary profiles.

Reorder via drag or the profile menu's Move up / Move down (drag alone is not enough:
it competes with click-to-open and gives keyboard users no path). Order changes go
through `PersistOrderAsync`, which does **not** touch `ModifiedAt`, then call
`App.RegisterAllHotkeys()` to rebind the slots.

## Schema version and stored defaults

`DisplayProfile.SchemaVersion` exists so a load can tell an old file's default from a
value someone chose. Absent means 0; `ProfileService.SaveProfileAsync` stamps the current
version on every write.

This was paid for by `DpiScale`. It defaulted to 100 and sat in every profile on disk,
written by builds that never captured scaling, so when the apply learned to write scaling
it would have dragged every 150% monitor to 100%. `MigrateUncapturedDpiScale` clears it
on load for `SchemaVersion == 0`, and `MonitorInfo.DpiScaleUnset` (0) is what "leave it
alone" looks like from then on.

The lesson generalises: **a field with a plausible-looking default is indistinguishable
from a captured value.** Give a new optional field a sentinel that cannot be mistaken for
a real one, and bump the schema when an old default would now be acted on.

Removing a field needs no migration. System.Text.Json skips unknown members, so
`unmatchedAction` sitting in old files is harmless.

## Runtime-only profile state

`IsActive` and `IsAvailable` are `[JsonIgnore]` and describe the machine right now, not
the profile. `ProfileListViewModel` maintains both: `IsActive` from `ProfileApplied`
plus `LastAppliedProfileId` on cold start, `IsAvailable` from `CheckCompatibility`,
refreshed on `SystemEvents.DisplaySettingsChanged` rather than polled.

## Window placement

`AppSettings.WindowPlacement` is restored on first activation, not in the constructor:
Windows applies its own default placement when a window is first shown and discards a
position set beforehand (the size survives).

`WindowPlacementValidator` (Mo.Core) rejects a saved rectangle that no longer lands on
any monitor. This app rearranges displays for a living, so that happens often.

Work areas come from `WindowHelper.GetWorkAreas()` (Win32 `EnumDisplayMonitors`);
`DisplayArea.FindAll()` throws `InvalidCastException` in this app's
self-contained/unpackaged configuration.
