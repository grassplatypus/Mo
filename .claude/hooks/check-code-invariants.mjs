#!/usr/bin/env node
// PostToolUse check for the C# invariants a script can decide: serialized-model shape,
// the single apply choke point, DDC/CI handle discipline, and warning suppression.
// Rules: .claude/rules/10-code-style.md, 40-safety-invariants.md, 50-persistence.md
import fs from 'node:fs';
import path from 'node:path';

// Only these may call IDisplayService.ApplyProfile: the choke point and the revert path.
const APPLY_ALLOWED = [
    'src/Mo/Services/ProfileService.cs',
    'src/Mo/Services/ApplyGuardService.cs',
    'src/Mo/Services/DisplayService.cs',
];
const DDCI_ALLOWED = ['src/Mo/Services/MonitorColorService.cs'];
const MODELS_DIR = 'src/Mo/Models/';

const RULES = [
    {
        id: 'observable-property-on-model',
        applies: (rel) => rel.startsWith(MODELS_DIR),
        test: /\[ObservableProperty\]/,
        why: 'a serialized model must not use [ObservableProperty] — MoJsonContext sees only the private field and the member vanishes from the JSON contract',
    },
    {
        id: 'apply-bypasses-guard',
        applies: (rel) => !APPLY_ALLOWED.includes(rel),
        test: /\.ApplyProfile\s*\(/,
        why: 'every apply must go through ProfileService.ApplyProfileAsync, which owns the capture/confirm/revert safety net',
    },
    {
        id: 'raw-ddci-call',
        applies: (rel) => !DDCI_ALLOWED.includes(rel),
        test: /MonitorConfigApi\.[A-Za-z_][A-Za-z0-9_]*\s*\(/,
        why: 'DDC/CI calls must go through MonitorColorService.WithHandles/WithHandleFor — a handle used outside the lock is a use-after-free (constants such as MonitorConfigApi.VCP_* are fine)',
    },
    {
        id: 'suppression-attribute',
        applies: () => true,
        test: /\[\s*SuppressMessage/,
        why: 'SuppressMessage hides an analyzer finding project-wide; fix the finding or suppress it locally with a #pragma that says why',
    },
];

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

const stripComment = (line) => line.replace(/\/\/.*$/, '');

// A pragma is allowed, but only with a comment near it establishing why it is safe.
export function pragmaViolations(lines) {
    const found = [];
    for (let i = 0; i < lines.length; i++) {
        const m = lines[i].match(/#pragma\s+warning\s+disable\s+(\S+)/);
        if (!m) continue;
        const context = lines.slice(Math.max(0, i - 3), i + 2).join('\n');
        if (!/\/\/|\/\*/.test(context)) {
            found.push([i + 1, `#pragma warning disable ${m[1]} with no comment saying why it is safe`]);
        }
    }
    return found;
}

export function findViolations(text, rel) {
    const lines = text.split(/\r?\n/);
    const found = [];
    for (const rule of RULES) {
        if (!rule.applies(rel)) continue;
        for (let i = 0; i < lines.length; i++) {
            if (rule.test.test(stripComment(lines[i]))) found.push([i + 1, rule.why]);
        }
    }
    return found.concat(pragmaViolations(lines)).sort((a, b) => a[0] - b[0]);
}

function main() {
    let payload;
    try {
        payload = JSON.parse(readStdin() || '{}');
    } catch {
        return 0;
    }

    const filePath = payload?.tool_response?.filePath ?? payload?.tool_input?.file_path;
    if (typeof filePath !== 'string' || path.extname(filePath).toLowerCase() !== '.cs') return 0;
    if (!fs.existsSync(filePath)) return 0;

    const root = process.env.CLAUDE_PROJECT_DIR ?? process.cwd();
    const rel = path.relative(root, filePath).split(path.sep).join('/');
    if (rel.startsWith('..') || rel.includes('/obj/') || rel.includes('/bin/')) return 0;

    const violations = findViolations(fs.readFileSync(filePath, 'utf8'), rel);
    if (!violations.length) return 0;

    const detail = violations.map(([line, why]) => `  ${filePath}:${line}\n    ${why}`).join('\n');
    process.stderr.write(`Project invariant violated:\n${detail}\n`);
    return 2;
}

if (process.argv[1] && import.meta.filename === process.argv[1]) {
    process.exit(main());
}
