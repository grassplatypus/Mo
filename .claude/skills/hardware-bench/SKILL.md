---
name: hardware-bench
description: Build an interactive console bench when the only way to tell whether a fix worked is for a person to look at the screen. Use when investigating display, cursor, audio or input behaviour where each trial costs a visible mode change and the pass/fail signal is human perception rather than a value you can read back.
---

# Interactive hardware bench

When the verdict is "does the cursor point the right way", "did the screen flicker",
"is the sound coming from the right device", **do not script a timed sequence.** Build a
menu the user drives, and hand them the command.

This exists because the alternative was tried and cost an hour. A background run that
rotated a display, waited 30 seconds, applied a candidate fix, waited 30 more, then
restored, produced answers that could not be trusted: the user could not tell which
phase they were looking at, one reply landed against the wrong step, and a wrong
hypothesis was nearly recorded as confirmed.

## When this applies

All three have to hold:

1. The pass/fail signal is something only a person can judge.
2. A trial has a visible cost: a mode change, a blank screen, a rearranged desktop.
3. You have more than one candidate to try, or the same candidate at several settings.

If you can read the answer back through an API, write a normal probe instead and decide
for yourself. Do not make the user judge what you can measure.

## Shape of the bench

A console app in the session scratchpad, referencing the project's interop assembly so
it exercises the *same* P/Invoke declarations the product uses. A bug in the marshalling
is then a bug the bench reproduces.

```
── target: \\.\DISPLAY1 ──────────────────────────
  r  rotate portrait <-> landscape   <- breaks it
  f  flip 180, same resolution       <- breaks it, layout untouched

  1  SetSystemCursor
  2  mouse trails on/off
  3  display power cycle (asks for the blank duration)

  s  status    t  change target    q  quit
> _
```

Rules that made the difference:

- **Put the break and the candidate fixes in one menu.** The user breaks the state once,
  then walks the candidates. Otherwise every candidate needs its own setup round.
- **Print the API you called and what it returned**, next to each action. "3 fixed it"
  then names the cause without a second experiment.
- **Number the actions and never renumber them** between builds. The user reports by
  number.
- **Prefer the variant that does not disturb the desktop.** Flipping 180 degrees keeps
  the resolution and the window layout; swapping aspect ratio reflows everything. Offer
  both, default to the gentle one.
- **Always offer restore**, and restore on exit.
- Ask for parameters interactively (blank duration, delay) rather than baking them in.
  Narrowing a threshold then costs one keypress, not a rebuild.

## Mechanics that bite

- The user will have the bench running while you rebuild, and the running process locks
  the exe. Build to a separate output directory (`dotnet build -o bench2`) or ask them
  to quit first. Do not kill their process.
- Arguments carrying backslashes get mangled on the way through the shell. Normalise
  inside the program (`\\.\` + last path segment) rather than fighting the quoting.
- Console output goes to *their* terminal, not to you. Anything you need for the record,
  have them paste, or write it to a scratchpad file as well.
- Give them the full command to paste. Do not assume a working directory.

## Reporting

Ask one question: which numbered action fixed it. Then confirm the negative results are
real by having them repeat the break and skip that action.

Record the negatives. A table of what was ruled out is worth more than the fix itself,
because it stops the next person retrying the same six dead ends. Put it in the rule file
for that subsystem, not in a commit message.
