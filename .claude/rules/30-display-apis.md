# Display APIs

## CCD (Windows standard)

- `QueryDisplayConfig` reads all display paths and modes; `SetDisplayConfig` applies.
- Monitor identity: use `DevicePath` (stable across reboots), **not** `AdapterId`
  (changes every boot).
- Rotation: `DISPLAYCONFIG_ROTATION` (Identity, Rotate90, Rotate180, Rotate270).
- Rotation leaves the mouse cursor on the old rotation; see "The rotated cursor plane
  is an open bug" below before attempting anything about it.

**Persistence flags**: pass `SDC_SAVE_TO_DATABASE | SDC_PATH_PERSIST_IF_REQUIRED` so
Windows 10 1903+ keeps DPI/rotation-aware layouts across reboots, and retry without
`PATH_PERSIST_IF_REQUIRED` on failure for older builds.

**Do not add `SDC_VIRTUAL_MODE_AWARE` unless the query gains `QDC_VIRTUAL_MODE_AWARE`
in the same edit.** The two flags select the same union. Without them `modeInfoIdx` is
a plain index, which is how `DisplayService` reads it in three places; with them it is
`cloneGroupId:16 | sourceModeInfoIdx:16`, so plain indices 0, 1, 2 arrive as three
different clone groups all pointing at source mode 0. Mo shipped the set flag without
the query flag for a long time and never noticed, because of the bug below.

**Count, not capacity**: `GetDisplayConfigBufferSizes` sizes for the worst case and
`QueryDisplayConfig` writes back what it actually filled. Passing the array length to
`SetDisplayConfig` hands it uninitialised paths and it answers `ERROR_INVALID_PARAMETER`
(87). This killed the whole CCD fallback silently: both the primary and the retry failed,
`ApplyProfile` returned `Failed`, and only the NVAPI branch ever applied anything. Slice
the arrays to the written-back counts right after the query.

**Compact the mode array when you drop a path.** A supplied config may only carry modes
its paths still reference. Shrinking `finalPaths` while passing the whole mode array
orphans the removed path's entries and `SetDisplayConfig` answers 87. `CompactModes`
rebuilds the array and renumbers `modeInfoIdx`.

**Changing a mode means dropping `targetInfo.modeInfoIdx`.** Writing a new
`sourceMode.width/height` or `targetInfo.refreshRate` leaves the target mode entry
describing the old timing, and that entry wins: the apply reports success and the panel
does not change. Set the index to `0xFFFFFFFF` on any path whose size or rate actually
differs, which asks Windows to derive a timing from the refresh rate.

That orphans the entry it pointed at, so **`CompactModes` runs on every apply**, not only
when a path is dropped. Both operations orphan modes and either one alone earns an 87.

**A call that supplies no mode array must say so in every path.** The fallback passes
`0, null` for the modes, so it also has to set `sourceInfo.modeInfoIdx` and
`targetInfo.modeInfoIdx` to `0xFFFFFFFF` on a copy of the paths. Real indices next to a
count of zero are as malformed as the array cases above. It keeps `SDC_SAVE_TO_DATABASE`:
without it the change applies and then does not survive a reboot.

The mode list behind the editor's pickers comes from `EnumDisplaySettings`, not CCD. Its
pels are the extent *in the display's current orientation*, so every entry is normalized
with `RotationGeometry.ToSource` before it is offered. Without that, the same panel
yields two different lists depending on how it happens to be rotated.

`dmDisplayOrientation` only carries a value when `dmFields` has `DM_DISPLAYORIENTATION`;
a driver that reports rotated pels without setting the bit would otherwise have every
mode of a 2560x1440 panel recorded as 1440x2560. Fall back to the rotation CCD reported.

**Topology calls take `SDC_TOPOLOGY_EXTEND | SDC_APPLY` and nothing else.** Measured
2026-08-23: adding `SDC_ALLOW_CHANGES` or `SDC_SAVE_TO_DATABASE` turns the same call
into 87. Those flags belong to `SDC_USE_SUPPLIED_DISPLAY_CONFIG`.

### CCD re-activates a monitor perfectly well

The claim that it cannot, which is the reason the vendor driver paths were kept, does
not survive measurement. On the probe machine, disabling `\\.\DISPLAY2` through CCD and
then calling `SDC_TOPOLOGY_EXTEND | SDC_APPLY` brought it straight back, first try.

| Step | Result |
| --- | --- |
| Disable, whole mode array passed | 87, monitor stays on |
| Disable, mode array compacted | 0, monitor goes off |
| Re-enable, `EXTEND \| APPLY \| ALLOW_CHANGES \| SAVE_TO_DATABASE` | 87, nothing happens |
| Re-enable, `EXTEND \| APPLY` | 0, monitor comes back |

Mo was making both malformed calls. What looked like a CCD limitation was Mo's own
argument list. Do not reintroduce a vendor path on the strength of that claim without
re-running this.

## NVAPI (preferred on NVIDIA GPUs)

- `NvidiaRotationService.ApplyFullProfile()` applies a complete profile.
- Uses `PathInfo.GetDisplaysConfig()` / `SetDisplaysConfig()` for in-place modification.
- **Persistence flags**: always pass `DisplayConfigFlags.SaveToPersistence |
  DriverReloadAllowed`, with a fallback to `DriverReloadAllowed` alone on failure.
  Without `SaveToPersistence` the driver reverts *its own* change on reboot.

  This is a caveat about calling NVAPI correctly, **not** a reason to prefer NVAPI.
  Windows stores rotation and layout in its own display database and restores them on
  every boot; that is why a portrait monitor stays portrait without any of this. A
  user-facing string once claimed the driver path is what survives a restart. It was
  this bullet, misread. Do not write that again.
- CCD↔NVAPI display matching goes through the GDI device name bridge (`\\.\DISPLAY1`).
- PathInfo cache re-enables disabled monitors; falls back to `displayswitch.exe /extend`
  for cold-start activation.

The NVAPI branch returns early on success, so it owns the *whole* apply, geometry
included. `ApplyFullProfile` must set `path.Position`, `path.Resolution` and
`path.IsGDIPrimary`, not only `target.Rotation`.

Rotating changes a display's desktop footprint; if the positions never reach the driver
it repacks the desktop on its own and the profile's arrangement is lost. Log every
field it writes. `nvapi_debug.log` showing nothing but `Rotation:` lines is what
identified this.

## Rotation mixes two coordinate systems in one structure

CCD's `DISPLAYCONFIG_SOURCE_MODE` and NVAPI's `PathInfo.Resolution` are the *panel's own
mode, before rotation*, while the position stored next to them is in *post-rotation
desktop coordinates*.

Measured on a 2560x1440 panel at 270°: CCD source 2560x1440 at (0,0), NVAPI Resolution
2560x1440, GDI desktop rectangle 1440x2560.

`MonitorInfo.Width/Height` is the desktop extent everywhere in Mo, which the layout
canvas draws directly, so every call across that boundary goes through
`RotationGeometry.ToDesktop` / `ToSource` (Mo.Core).

Swap unconditionally at 90/270: guarding on `width > height` looks safer but is a
silent no-op for a natively portrait panel, which is the one case where guessing wrong
writes a mode the panel lacks.

## Radeon (ADL)

Verified against a real Radeon + GeForce machine; do not "simplify" these away.

- **Locate displays by `AdapterInfo.strDisplayName`**, which is the GDI name
  (`\\.\DISPLAY1`). `ADLDisplayInfo.strDisplayName` is the *EDID model name* and will
  never match one. `AdlDisplays.Resolve` does adapter-then-display in that order.
- **Filter on `iVendorID == 1002`** (decimal, not `0x1002`). ADL enumerates non-AMD
  adapters too: the probe machine listed four `NVIDIA GeForce RTX 5080` adapters on
  `\\.\DISPLAY1..4` next to Radeon ones on `\\.\DISPLAY5..9`. An adapter count is
  therefore not a test for "has AMD"; use `AdlDisplays.HasAmdAdapter`.
- ADL functions are `__cdecl`; the **allocation callback is `__stdcall`**
  (`ADL_MAIN_MALLOC_CALLBACK`). Declaring it `Cdecl` corrupts the stack on every ADL
  allocation.
- Buffers from `ADL2_Display_*_Get` are allocated through our callback and owned by us
  (`Marshal.FreeHGlobal`); the `AdapterInfo` buffer is ours to allocate instead.
- `ADL2_Display_Modes_Set` changes modes only. Topology (enabling/disabling outputs)
  would need `DisplayMapConfig_Set`, so `TryApplyAmdFullProfile` bails to CCD whenever
  a profile disables a monitor.
- The AMD full-profile path is opt-in (`RotationMethod.AmdDriver`) and **verifies by
  reading the configuration back**; a mismatch returns false so CCD corrects it.
  Whether ADL wants pre- or post-rotation `iXRes/iYRes` is undocumented, and that
  read-back is what keeps the guess from mattering.

## Intel: there is no driver path, and there cannot be one

Mo carried an `IntelRotationService` built on IGCL. It was removed on 2026-08-23 after
checking Intel's own published header (`intel/drivers.gpu.control-library`,
`include/igcl_api.h`). Do not write another one without reading that file first.

- **IGCL has no rotation API.** None of its 112 exported functions mentions rotation or
  orientation. `ctl_display_orientation_t` exists but appears in exactly one place,
  `ctl_combined_display_child_info_t`, which configures *combined display* stitching
  through `ctlGetSetCombinedDisplay`. That is not per-monitor rotation.
- **`ctlSetDisplayProperties` does not exist.** Only `ctlGetDisplayProperties` does. The
  old P/Invoke threw `EntryPointNotFoundException` into a bare `catch`.
- It never got that far anyway. `ctl_init_args_t` is 36 bytes: `Size`, `Version`,
  `AppVersion`, `flags`, `SupportedVersion` (all `uint32_t`), then a 16-byte
  `ctl_application_id_t` GUID. Mo's struct typed `SupportedVersion` as 8 bytes and
  omitted the GUID entirely, so it passed `Size = 24` to an API that validates it.

Intel therefore uses CCD, like any GPU without a vendor path. That is not a downgrade:
CCD rotates correctly, and the cursor-plane bug that motivates the NVIDIA workaround has
never been observed on Intel or AMD.

`RotationMethod.IntelDriver` survives in the enum so an old `settings.json` still
deserializes. Nothing offers it and nothing handles it, so it falls through to CCD.

## Rotation strands the hardware cursor plane

The GPU keeps drawing the pointer through the rotation it had *before* the mode change.
The offset is exactly the rotation delta: after a 270°→90° flip the click lands
point-symmetric about the display centre. The pointer also appears to hit an invisible
wall at a monitor edge it has visually already left.

The desktop bounding rectangle is **not** involved: measured right after the rotation,
win32k's virtual screen and the cursor clip both still match the union of the display
rects exactly. Only the cursor plane is stale.

Measured 2026-08-23 on RTX 5080, driver 32.0.16.1088.

| Tried | Result |
| --- | --- |
| Blank and wake the panels (`SC_MONITORPOWER` off → on) | **fixes it, permanently** |
| Mouse trails on (`SPI_SETMOUSETRAILS` ≥ 2) | fixes it *while on*, reverts when switched off |
| CCD `SetDisplayConfig` rotate | no effect |
| GDI `ChangeDisplaySettingsEx` rotate, staged + committed | no effect |
| `ChangeDisplaySettingsEx(…, CDS_UPDATEREGISTRY \| CDS_RESET)` | no effect |
| `SetDisplayConfig(SDC_TOPOLOGY_EXTEND \| SDC_APPLY)` | no effect |
| `NvAPI_DISP_SetDisplayConfig`, both flag sets | no effect |
| Round trip through the other aspect (real surface re-allocation) | no effect |
| `SPI_SETCURSORS`, `SetSystemCursor`, pointer shadow, `ShowCursor` | no effect |

**This is an NVIDIA problem, not a Windows one.** An integrated Radeon rotates through
the same CCD code with no cursor trouble at all, which is why `ResetCursorPlane` is not
called from the AMD branch and why the setting defaults to off. Do not describe it in
code or UI as a Windows or CCD bug; earlier comments did, and they were wrong.

Mouse trails working is the diagnosis: trails force Windows onto a **software** cursor,
which is composited into the already-rotated framebuffer and is therefore always right.
Re-uploading the cursor *shape* does nothing because the stale state is the driver's
rotation, not the bitmap. Only a pipeline re-init reseats it, meaning display power or a
reboot, which is why no mode-set of any flavour helps.

`CursorPlaneReset` does the power cycle. The blank cannot be shortened below the
monitor's own power-on latency: 100 ms of `Sleep` still costs whatever the panel takes.

Three gates, all of them load-bearing:

- `AppSettings.ResetCursorAfterRotation`, **off by default**. This was measured on one
  RTX 5080 and the cost is a full blackout, so AMD and Intel users do not inherit it.
- Rotation actually changing, including a disabled monitor coming up rotated, which has
  no "before" to compare against.

  **Measure rotation against the state before the apply, never against the paths read
  afterwards.** `SDC_TOPOLOGY_EXTEND` restores every panel from Windows' own display
  database, which remembers each monitor's last rotation. A monitor therefore arrives at
  the loop already turned, the per-path comparison finds nothing to change, and the plane
  is left stranded even though the screen visibly rotated.

  `hasRotationChange` is seeded from `matchedMonitorRotates`, computed against the
  original `currentConfig` before anything is touched, and `offBeforeApply` covers a panel
  the extend switched on. Both are pre-extend by construction. `boot.log` carries the
  verdict as `rotates=` and `trigger=` on `apply.branch`.
- `ApplyTrigger.User` only. Schedule, AutoSwitch and Startup are the quiet triggers by
  design (`40-safety-invariants.md`), and a revert must not stack a second blackout on
  the apply that caused it.

The wake path is unconditional and lives in a `finally`. `SendMessageTimeout` against
`HWND_BROADCAST` returns 0 when any single window is slow, *after* earlier windows have
already acted on the OFF, so treating that return as a delivery flag and skipping the ON
leaves every panel dark. The thing that actually relights them is the `SendInput` nudge,
not `SC_MONITORPOWER -1`, and that fails while a higher-integrity process holds the
foreground.

DPMS does not remove a monitor from Windows' view, so there is nothing to poll for
"the panels are back". `WakeSettleMs` is a budget, not a measurement. It has to cover
the panel's sync time, because `ApplyGuardService` opens its countdown as soon as
`ApplyProfile` returns and a countdown on a dark screen cannot be answered.

A `cursorplane.reset` line in `boot.log` means the reset ran, not that anything went
dark. `offAccepted=` on it is the broadcast's own answer: false means no panel blanked
and the plane was never reseated, which reads to the user exactly like the gate never
opening. Check that before assuming the detection is at fault.

`UnstickCursor` only releases a leftover `ClipCursor` and never fixed this.
`SystemParametersInfo(SPI_SETWORKAREA, …, pvParam: NULL)`, which earlier builds called
here, is inert: that action requires a valid `RECT*`.

What is established about the two GDI paths, for whoever picks this up:

- NVIDIA App's engine is `NVIDIA app\NvCpl\nvxdapix.dll`; the legacy control panel is
  `nvcpl.dll`. They differ: `nvcpl.dll` does not import `SetDisplayConfig` at all,
  `nvxdapix.dll` imports CCD, GDI *and* calls `NvAPI_DISP_SetDisplayConfig`.
- Both write `DEVMODEW` with `dmSize` 220, `dmSpecVersion` 0x0401 and
  `dmFields = 0x005C00A0` = `DM_POSITION | DM_DISPLAYORIENTATION | DM_BITSPERPEL |
  DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY`, stage with
  `CDS_UPDATEREGISTRY | CDS_NORESET`, then commit once with
  `ChangeDisplaySettingsEx(NULL, NULL, NULL, 0, NULL)`.
  `DM_BITSPERPEL` is easy to miss reading the hex; `InteropLayoutTests` asserts the sum.
- A commit whose staged values match the current ones is a no-op, so re-asserting the
  configuration after a CCD apply changes nothing. `CDS_RESET` forces it through.
- `dmPelsWidth/Height` under `DM_DISPLAYORIENTATION` are the **post-rotation desktop
  extent**, confirmed by reading back a 270° panel as 1440x2560. That is the opposite
  of CCD's source mode and NVAPI's `Resolution`.

Rotating from the NVIDIA App does clear the cursor, but calling
`NvAPI_DISP_SetDisplayConfig` ourselves does not, so whatever the App does extra is
still unaccounted for. That is the open thread if a blank-free fix is ever needed.

## Profile apply flow (`DisplayService.ApplyProfile`)

1. Try NVAPI full profile (if an NVIDIA GPU is available).
2. Fall back to CCD (topology extend → `SetDisplayConfig` with persistence flags).
3. `UnstickCursor`, which releases a leftover cursor clip and nothing more.
4. `CursorPlaneReset`, under the three gates above. It runs inside `ApplyProfile`, so
   it finishes before `ConfirmOrRevertAsync` opens its countdown. That ordering is only
   half the guarantee; the other half is `WakeSettleMs` being long enough.

**Reboot restore**: `App.RestoreLastAppliedProfileAsync` re-applies the profile recorded
in `AppSettings.LastAppliedProfileId` after launch. Gated by `RestoreOnStartup`; the
colour re-push is gated by `RestoreColorOnStartup`, since DDC/CI state is *not* kept by
Windows, so colour must be re-pushed every boot.

## Per-monitor scaling is an undocumented CCD call

`DISPLAYCONFIG_DEVICE_INFO_GET_DPI_SCALE` (-3) and `SET_DPI_SCALE` (-4) are not in the
SDK headers. They are what the Settings app uses, they have been stable since Windows 10
1607, and there is no documented alternative, so this is not a shortcut to replace with
a supported API. There isn't one.

The values are **steps relative to the display's recommended scale**, never percentages.
`minScaleRel` is the offset from recommended down to the lowest step, so `-minScaleRel`
is the recommended step's index into the fixed ladder
`100 125 150 175 200 225 250 300 350 400 450 500`. `Mo.Core`'s `DpiScaling` owns that
arithmetic and is where it gets tested; the service only marshals.

Address it by **`sourceInfo.adapterId` and `sourceInfo.id`**, not the target. A scale
belongs to the desktop source, which is why `ApplyDpiScaling` runs after the topology has
settled: the source id does not exist until the display is part of the desktop.

`DpiScaleTests` proves the struct layout by reading real displays back. A driver that
does not implement the call returns relative values that index off the ladder, which
`ToPercent` reports as 100 rather than throwing.

Both vendor branches return early, so each calls `ApplyDpiScaling` itself. Neither NVAPI
nor ADL exposes scaling; it is a CCD call either way.

`DisplayProfile` carried a `DpiScale` field for a long time that nothing captured and
nothing applied, so it was always 100 and `ProfileDiffer` could never report a change.
The README and CLAUDE.md claimed DPI was saved and restored the whole time. Every profile
on disk therefore says 100 without meaning it, which is what `SchemaVersion` and
`MonitorInfo.DpiScaleUnset` exist for; see `50-persistence.md`.

## Colour control

- **DDC/CI** via `IMonitorColorService` using dxva2.dll: brightness, contrast, RGB
  gain, plus raw VCP Get/Set (colour-temperature preset code 0x14, etc.).
- **WMI fallback** for laptop internal display brightness (`WmiMonitorBrightness`).
- **HDR toggle** via `IDisplayService.GetHdrState` / `SetHdrEnabled` using CCD
  `DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO` / `SET_ADVANCED_COLOR_STATE`.
- **Manufacturer** resolved from the EDID manufacturer ID via `EdidManufacturer`
  (`GSM→LG`, `SAM→Samsung`, `DEL→Dell`, …). Tries both byte orders.
- Real-time UI: `DisplayTuningPage`, where 60 ms throttled slider changes go straight to
  `ApplyToMonitor` without touching any saved profile.
