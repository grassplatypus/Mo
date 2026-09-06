# Localization

Mo ships two string bundles:

```
src/Mo/Strings/en-us/Resources.resw
src/Mo/Strings/ko-KR/Resources.resw
```

Both currently define the same 285 keys, and they must stay that way.

## Every locale defines every key

Adding a UI string means adding a `<data name="…">` entry to **every** locale, in the
same edit. A key present in only one bundle falls back silently: the user sees English
in a Korean UI with no error anywhere.

Enforced by `.claude/hooks/check-resw-sync.mjs` (`PostToolUse` on `Write`/`Edit` of any
`.resw`). It compares the key sets of every locale directory and fails the edit with the
missing keys listed per locale.

If you genuinely cannot write the translation, add the key with the English text as a
placeholder and say so. Do not leave the bundle short a key.

## The language is set before the first window

`App.OnLaunched` applies the override *before* creating any window, because the initial
resource lookups (window title, every `x:Uid` binding) resolve against whatever
bundle is active at that moment.

When `AppSettings.Language` is empty, it falls back to the first preferred **system**
language rather than leaving it unset. Without that fallback the WinAppSDK
`ResourceLoader` sometimes refuses to resolve `ko-KR` resources on unpackaged and
sideloaded MSIX builds, because our `DefaultLanguage` is `en-US`. Korean Windows then
shows an English UI.

## Keys must resolve, not just match each other

The sync check above only proves the two bundles agree with *each other*. The commoner
failure is a key that exists in neither: `GetString("HotkeyConflictTtile")` compiles,
throws nothing, and shows an empty string.

`check-resource-keys.mjs` resolves every `GetString("…")` in C# and every `x:Uid` in
XAML against the en-us bundle. All of them resolve right now, so anything it reports
is new.

**An `x:Uid` needs a key with a property suffix.** `x:Uid="RemoveDataButton"` is answered
by `RemoveDataButton.Text`, never by a bare `RemoveDataButton` entry: that names no
property, XAML sets nothing, and the control renders blank. Two buttons shipped empty for
a long time this way, and the check used to pass them because the key did exist. It now
requires the suffix.

The same key read from code takes the full name, `GetString("RemoveDataButton.Text")`.

## How the strings are written

Every `<value>` in these bundles is text a user reads, so it follows the house style:
short, plain, no em dashes, no explaining the machine, and Korean that does not read
like a translation of the English. The full policy is in the global preferences file.

`check-ui-text.mjs` enforces the part of that policy a script can judge, on `.resw`,
`.xaml`, `Package.appxmanifest` and `.md`:

- no em dash anywhere in user-visible text (XML comments are excluded),
- no `<data>` value containing a term from `.claude/hooks/ui-text-terms.json`,
- no `*.Description` that contains its own `*.Header` verbatim.

## The em dash ban covers documentation too

Not only UI strings: the rule files, `docs/`, `README.md` and `CLAUDE.md` are all text
someone reads, and the hook checks them. Markdown gets the em dash rule alone; fenced
blocks and inline code are skipped, since a dash inside a command or a table of measured
output is data, not prose.

The whole tree was swept once and is clean, so any report is something a change
introduced. Use a comma, a colon, parentheses, or start a new sentence.

The term list is a project file, not part of the hook, because what counts as internal
differs per product. Mo's list holds `DDC/CI`, `NVAPI`, `JSON` and friends; `HDR`,
`sRGB` and `Hz` are deliberately absent, since a user reads those on the monitor's own
box. Add a word when it leaks into the UI a second time.

Everything the hook cannot judge stays your job: translationese, over-formal 한자어,
whether a sentence sounds native, and whether a description earns its line.

Two things this repo gets wrong most often:

- A `SettingsCard` description that explains the defect the setting works around.
  `ResetCursorAfterRotationCard` says "The screen blinks briefly." The reason the blink
  is needed lives in `30-display-apis.md`, not on the card.
- A description that restates its header. If the header needs propping up, rewrite the
  header.

## Conventions

- Key names are PascalCase and describe the *use site*, not the text
  (`ApplyConfirmTitle`, not `AreYouSure`).
- `AppSettings.Language` is a BCP-47 override. Empty follows the Windows display
  language; `"ko-KR"` / `"en-US"` force a bundle on next launch.
- Never bake user-facing English into a model. `DisplayProfile.Description` is the
  user's own note; monitor count and timestamps are formatted at render time, and
  `LegacyDescription.IsGenerated` strips the English descriptions older builds wrote
  into that field.
