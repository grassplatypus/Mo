#!/usr/bin/env node
// PreToolUse guard for Bash / PowerShell: the shell must not read or edit files.
// Rule: .claude/rules/00-tooling.md
import fs from 'node:fs';

const READ_COMMANDS = new Set([
    'cat', 'head', 'tail', 'more', 'less', 'nl', 'strings', 'od', 'xxd',
    'type', 'get-content', 'gc', 'sed', 'awk',
]);

const DENY_PATTERNS = [
    [/\bsed\b[^|;&\n]*\s-[a-zA-Z]*i\b/, 'sed -i edits files in place'],
    [/\bperl\b[^|;&\n]*\s-[a-zA-Z]*i\b/, 'perl -i edits files in place'],
    [/\bawk\b[^|;&\n]*-i\s+inplace/, 'awk -i inplace edits files in place'],
    [/(^|[\s;&|(])patch\b/, 'patch rewrites files from a diff'],
    [/\b(python3?|py)\b\s+(-[^\s-]\S*\s+)*-c\b/, 'inline python -c can rewrite files'],
    [/\bnode\b\s+(-[^\s-]\S*\s+)*(-e|--eval|-p|--print)\b/, 'inline node -e can rewrite files'],
    [/\b(perl|ruby|php)\b\s+(-[^\s-]\S*\s+)*-e\b/, 'inline -e script can rewrite files'],
    [/\|\s*(python3?|py|node|deno|bun|perl|ruby|php|bash|sh)\b(\s+-\S*)*\s*($|[|;&])/, 'piping a script into an interpreter hides a file rewrite'],
    [/\b(Set-Content|Add-Content|Clear-Content|Out-File|Set-ItemProperty)\b/i, 'this cmdlet writes files directly'],
    [/\bNew-Item\b(?![^|;&\n]*-ItemType\s+Directory)/i, 'New-Item creates or truncates a file'],
    [/\[(System\.)?IO\.File\]::(Write|Append|Replace|Create|Delete|Move)/i, '[IO.File] writes files directly'],
    [/\[(System\.)?IO\.File\]::Read/i, '[IO.File] reads files directly'],
    [/\btruncate\b\s+-s/, 'truncate rewrites a file in place'],
    [/\bdd\b[^|;&\n]*\bof=/, 'dd of= writes a file directly'],
];

const HEREDOC = /<<-?\s*\\?['"]?[A-Za-z_][A-Za-z0-9_]*/;
const HEREDOC_OK = /^\s*(git\s+(commit|tag|notes|merge)|gh\s)/;
const SINKS = /^(\/dev\/null|\$null|nul|\/dev\/stderr|\/dev\/stdout)$/i;
const SCRATCH = /(scratchpad|[\\/]tmp[\\/]|Temp[\\/]claude)/i;

function readStdin() {
    const chunks = [];
    const buf = Buffer.alloc(65536);
    for (;;) {
        let n = 0;
        try {
            n = fs.readSync(0, buf, 0, buf.length, null);
        } catch (err) {
            if (err.code === 'EAGAIN') continue;
            break;
        }
        if (n <= 0) break;
        chunks.push(Buffer.from(buf.subarray(0, n)));
    }
    return Buffer.concat(chunks).toString('utf8');
}

// Blank out quoted spans so operators inside string literals are not matched.
function stripQuoted(text) {
    let out = '';
    let quote = null;
    for (let i = 0; i < text.length; i++) {
        const ch = text[i];
        if (quote) {
            if (ch === '\\' && quote === '"') { i++; out += ' '; continue; }
            if (ch === quote) quote = null;
            out += ' ';
            continue;
        }
        if (ch === '"' || ch === "'") { quote = ch; out += ' '; continue; }
        out += ch;
    }
    return out;
}

function findRedirect(stripped) {
    const re = /(^|[^0-9<>=&!-])>>?\s*([^\s|;&)]*)/g;
    let m;
    while ((m = re.exec(stripped)) !== null) {
        const target = m[2];
        if (!target || target.startsWith('&')) continue;
        if (SINKS.test(target) || SCRATCH.test(target)) continue;
        return target;
    }
    return null;
}

function readCommandTarget(command) {
    const stripped = stripQuoted(command);
    const cut = stripped.search(/[|;&\n]/);
    const seg = command.slice(0, cut === -1 ? command.length : cut).trim();
    const words = seg.split(/\s+/).filter(Boolean);
    while (words.length && /^[A-Za-z_][A-Za-z0-9_]*=/.test(words[0])) words.shift();
    if (!words.length) return null;
    const name = words[0].replace(/^.*[\\/]/, '').toLowerCase();
    if (!READ_COMMANDS.has(name)) return null;
    return words.slice(1).find((w) => !w.startsWith('-')) ?? null;
}

function decide(command) {
    if (HEREDOC.test(command) && !HEREDOC_OK.test(command)) {
        return ['deny', 'Heredoc blocked (allowed only for git commit/tag/notes and gh). Feeding a script or file body through <<EOF bypasses the edit tools. Use Read / Edit / Write.'];
    }
    for (const [re, why] of DENY_PATTERNS) {
        if (re.test(command)) return ['deny', `Blocked: ${why}. Use Read / Edit / Write instead.`];
    }
    const stripped = stripQuoted(command);
    const target = findRedirect(stripped);
    if (target) {
        return ['deny', `Blocked: redirects into "${target}". Use Write / Edit. Redirects to /dev/null or a scratchpad path are allowed.`];
    }
    if (/\btee\b/.test(stripped) && !/\btee\b[^|;&]*\/dev\/null/.test(stripped)) {
        return ['deny', 'Blocked: tee writes files from the shell. Use Write instead.'];
    }
    const readTarget = readCommandTarget(command);
    if (readTarget) {
        return ['ask', `Reads "${readTarget}" through the shell. Prefer Read / Grep / Glob.`];
    }
    return null;
}

let payload;
try {
    payload = JSON.parse(readStdin() || '{}');
} catch {
    process.exit(0);
}
const command = payload?.tool_input?.command;
if (typeof command === 'string' && command.trim()) {
    const verdict = decide(command);
    if (verdict) {
        process.stdout.write(JSON.stringify({
            hookSpecificOutput: {
                hookEventName: 'PreToolUse',
                permissionDecision: verdict[0],
                permissionDecisionReason: verdict[1],
            },
        }));
    }
}
process.exit(0);
