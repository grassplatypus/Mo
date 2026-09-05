# Tooling rules

## Files are read and written with the dedicated tools only

| Intent | Tool | Never |
| --- | --- | --- |
| Read a file | `Read` | `cat`, `head`, `tail`, `type`, `Get-Content`, `sed -n`, `awk` |
| Search contents | `Grep` | `grep -r` piped through a reader |
| Find files | `Glob` | `find`/`dir` recursion |
| Change part of a file | `Edit` | `sed -i`, `perl -pi`, `patch`, `.Replace()` scripts |
| Create/replace a file | `Write` | `> file`, `>> file`, `tee`, `Set-Content`, `Out-File` |

The shell is for **running** things (build, test, git, process inspection), not for
moving bytes into or out of files.

## Blocked bypasses

`.claude/hooks/guard-shell-file-ops.mjs` runs as a `PreToolUse` hook on `Bash` and
`PowerShell` and denies these outright:

- **Heredocs** (`<<EOF`, `<<'PY'`) except after `git commit|tag|notes|merge` and `gh`.
  The specific pattern this exists to stop is piping a literal script into an
  interpreter to do string surgery on a file:
  `python <<'PY' … open(p).read().replace(a, b) … PY`.
- **Inline interpreter scripts**: `python -c`, `node -e`, `perl -e`, `ruby -e`,
  and any `… | python`/`| node`/`| bash` that reads its program from stdin.
- **In-place editors**: `sed -i`, `perl -i`, `awk -i inplace`, `patch`, `truncate -s`,
  `dd of=`.
- **Output redirection to a file**, `>` and `>>`. Redirecting to `/dev/null`,
  `$null`, or a scratchpad/temp path is still allowed.
- **PowerShell file cmdlets**: `Set-Content`, `Add-Content`, `Clear-Content`,
  `Out-File`, `Set-ItemProperty`, `New-Item` (unless `-ItemType Directory`),
  `[IO.File]::Write*`/`Read*`.

Reading a file through the shell (`cat foo.cs`, `head -50 x.md`, `Get-Content`)
returns **ask**, not deny. Approve it only when `Read` genuinely cannot do the job.

If a hook blocks something you believe is legitimate, say so and let the user decide.
Do not reshape the command to slip past the pattern.

## The one remaining seam

Running a *script file* (`node tools/x.mjs`, `dotnet run`) is allowed, since the hook only
stops programs supplied inline or over a pipe. Writing a script whose purpose is to
rewrite repo files, then running it, is the same bypass taken in two steps and is
equally out of bounds. Scripts that build, test, probe or report are fine.

## Temporary files

Scratch work goes in the session scratchpad directory, never in the repo and never in
`/tmp`. Writing there uses `Write` like anything else.
