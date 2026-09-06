# Agent instructions

The canonical instructions for this repository are in [CLAUDE.md](CLAUDE.md), which
imports the rule files under `.claude/rules/`:

| File | Covers |
| --- | --- |
| `.claude/rules/00-tooling.md` | Read/Write/Edit only; no shell file I/O, no heredoc bypass |
| `.claude/rules/10-code-style.md` | 3-line comment limit, C# style, banned blocking APIs |
| `.claude/rules/20-architecture.md` | Layering, MVVM, DI, adding a page or service |
| `.claude/rules/30-display-apis.md` | CCD, NVAPI, ADL, rotation geometry, colour control |
| `.claude/rules/40-safety-invariants.md` | Apply guard, DDC/CI handle safety, uninstall |
| `.claude/rules/50-persistence.md` | JSON contract, profile storage, ordering, placement |
| `.claude/rules/60-build-and-git.md` | Build, test, CI, git conventions |
| `.claude/rules/70-localization.md` | Both resw bundles, key resolution, language override |
| `.claude/rules/80-ui-responsiveness.md` | Heavy work off the dispatcher, busy and empty states |

Two of those rules are enforced by hooks in `.claude/settings.json` rather than trust:
`guard-shell-file-ops.mjs` (PreToolUse, Bash/PowerShell) and `check-comment-length.mjs`
(PostToolUse, Write/Edit).

This file previously duplicated CLAUDE.md and had drifted out of date. Keep it a
pointer.
