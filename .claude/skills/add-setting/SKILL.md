---
name: add-setting
description: Add a user-facing setting to Mo, covering the AppSettings field, the SettingsViewModel two-way property, the SettingsPage card, and both resw bundles. Use whenever a new toggle, number, or choice has to appear on the Settings page, or when an existing setting needs a side-effect wired up.
---

# Adding a setting

A setting touches five places. Missing any one of them fails quietly: the value saves
but nothing reacts, or the card renders with a blank label.

Read `.claude/rules/50-persistence.md` and `.claude/rules/70-localization.md` first.
This skill is the checklist; those are the reasons.

## 1. `src/Mo/Models/AppSettings.cs`

Add a plain auto-property with a default that matches current behaviour.

```csharp
public bool MyNewToggle { get; set; } = true;
```

- **No `[ObservableProperty]`.** `MoJsonContext` and the MVVM generator run on the same
  original compilation, so the generated property is invisible to the serializer and the
  value would vanish from the JSON contract.
- `JsonHelper.cs` already declares `[JsonSerializable(typeof(AppSettings))]` for the
  whole type, so a new property needs **no** change there.
- A nullable reference type means "unset"; prefer a non-null default where the setting
  is always meaningful.

## 2. `src/Mo/ViewModels/SettingsViewModel.cs`

Expose it as a two-way property through the `Set` helper, which compares, assigns,
saves, and raises `PropertyChanged` in that order.

```csharp
public bool MyNewToggle
{
    get => _settings.Settings.MyNewToggle;
    set => Set(_settings.Settings.MyNewToggle, value, v => _settings.Settings.MyNewToggle = v);
}
```

If flipping the toggle must *do* something (register at logon, restart a timer, rebind
hotkeys), pass a fourth argument:

```csharp
    set => Set(_settings.Settings.MyNewToggle, value, v => _settings.Settings.MyNewToggle = v, v =>
    {
        _ = Task.Run(async () => { /* off the UI thread */ });
    });
```

The side effect runs **after** the save. Never block on the task here: `Task.Wait()`,
`.Result` and `GetAwaiter().GetResult()` are build errors (RS0030) for the deadlock
reason in `.claude/rules/10-code-style.md`.

## 3. `src/Mo/Views/SettingsPage.xaml`

Add a `SettingsCard` in the matching group. The `x:Uid` is what binds the strings.

```xml
<tk:SettingsCard x:Uid="MyNewToggleCard">
    <tk:SettingsCard.HeaderIcon>
        <FontIcon Glyph="&#xE946;" />
    </tk:SettingsCard.HeaderIcon>
    <ToggleSwitch IsOn="{x:Bind ViewModel.MyNewToggle, Mode=TwoWay}" OnContent="" OffContent="" />
</tk:SettingsCard>
```

- A card that only applies while another is on takes
  `IsEnabled="{x:Bind ViewModel.OtherToggle, Mode=OneWay}"`.
- Numbers use `NumberBox` with `Minimum`/`Maximum`/`SmallChange`.
- Do **not** nest a `SettingsExpander` inside another. It styles every child in `Items`
  as a `SettingsCard` and throws at element-prepare time. Use flat cards.

## 4. Both resw bundles, same edit, no exceptions

`src/Mo/Strings/en-us/Resources.resw` **and** `src/Mo/Strings/ko-KR/Resources.resw`:

```xml
<data name="MyNewToggleCard.Header" xml:space="preserve"><value>…</value></data>
<data name="MyNewToggleCard.Description" xml:space="preserve"><value>…</value></data>
```

Keep the entries at the same position in both files so diffs stay readable. A key in one
bundle only is blocked by `check-resw-sync.mjs`, and the description should say what the
setting protects against, not restate the header.

## 5. Verify

```bash
dotnet build Mo.slnx -c Debug -p:Platform=x64
```

Then confirm, in order:

1. The property round-trips. `ProfileService.EnsureRoundTrips` throws on a broken
   contract, so a silent JSON drop shows up as a save failure, not a wrong value.
2. Toggling the card writes `settings.json` under `%LOCALAPPDATA%/Mo/`.
3. The card shows real text in both languages (`AppSettings.Language` = `ko-KR` /
   `en-US` forces a bundle on next launch).

## Consuming the setting

Read it through `ISettingsService.Settings`, never from a cached copy taken at
construction, since the user can change it while the app runs. Settings that gate an apply
(`ConfirmApply`, `RestoreOnStartup`) are read at the moment of the apply, inside
`ProfileService.ApplyProfileAsync`.
