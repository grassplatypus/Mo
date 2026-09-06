# Code style

## Comments: 3 lines maximum

**Prose comments**, meaning a run of `//`, a `/* … */`, a `<!-- … -->` in XAML or a run
of `#`, may not exceed **3 consecutive lines**.

**XML doc comments are measured per tag, not per block.** `<summary>`, each `<param>`,
`<returns>`, `<remarks>` and the rest each get their own 3-line budget, so a documented
API with several parameters is fine:

```csharp
/// <summary>
/// Three lines is the limit for one tag.
/// </summary>
/// <param name="a">First operand, described at length so the text wraps
/// onto a second line.</param>
/// <param name="b">Second operand.</param>
/// <returns>The sum.</returns>
```

What the limit still catches is the rambling single tag, a `<summary>` or `<remarks>`
that turns into an essay. That is the case this rule exists for.

Enforced by `.claude/hooks/check-comment-length.mjs` (`PostToolUse` on `Write`/`Edit`,
exit code 2). A `Write` is judged on the whole file; an `Edit` only on the span it
wrote, so legacy comments elsewhere do not block an unrelated change.

If an explanation needs more room than that, it is not a comment. It is a rule file
under `.claude/rules/` or a document under `docs/`. Link to it from the code instead.
That is where the long `<remarks>` blocks this repo used to carry now live.

The check is line-based and cannot tell code from string literals, so a file that embeds
comment-like text in a string (a test fixture, say) can trip it. Build such data from
parts rather than weakening the rule.

Prefer comments that record *why* a non-obvious choice was made. Restating the code
in prose burns lines against the limit for nothing.

## C#

- File-scoped namespaces.
- `sealed` on classes not designed for inheritance.
- Nullable reference types enabled everywhere.
- Private fields `_camelCase`.
- `string.Empty`, never `""`.
- Indentation and encoding come from `.editorconfig` (4 spaces, CRLF, UTF-8 BOM).

## Blocking calls are a build error

`BannedSymbols.txt` + `Microsoft.CodeAnalysis.BannedApiAnalyzers` fail the build
(RS0030 via `WarningsAsErrors`) on `Task.Wait()`, `.Result`, and
`GetAwaiter().GetResult()`. `Program.Main` installs a
`DispatcherQueueSynchronizationContext`, so blocking the UI thread on a task whose
continuation posts back to it deadlocks before any window exists, with no exception and
no crash log.

Suppress locally with `#pragma warning disable RS0030` *only* with a comment
establishing the call is off the UI thread.

The same trap has a second shape on the STA thread. `Program.RedirectToPrimary` calls
`RedirectActivationToAsync`, which completes through a COM cross-apartment callback:
blocking `Main` on it starves the message pump that callback needs, so the secondary
process hangs invisibly and every further launch piles up another wedged `Mo.exe`.

The fix, taken from Microsoft's own AppLifecycle instancing sample, is to run the
redirect on a thread-pool thread while the STA thread waits in
`CoWaitForMultipleObjects`, which keeps pumping COM messages. Any new cross-apartment
await from `Main` needs the same treatment.

## Never use `[ObservableProperty]` on a serialized model

`MoJsonContext` and the CommunityToolkit MVVM generator both run against the *same
original compilation*, so System.Text.Json sees `private string _name` and never the
`Name` property MVVM emits afterward. The member silently vanishes from the JSON
contract and profiles deserialize blank.

`DisplayProfile` therefore derives from `ObservableObject` but writes its properties by
hand with `SetProperty`. `ProfileService.EnsureRoundTrips` verifies each save
round-trips and throws rather than overwriting a good file.
