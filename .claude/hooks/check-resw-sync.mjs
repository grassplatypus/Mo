#!/usr/bin/env node
// PostToolUse check: every locale under src/Mo/Strings must define the same keys.
// Rule: .claude/rules/70-localization.md
import fs from 'node:fs';
import path from 'node:path';

const STRINGS_DIR = 'src/Mo/Strings';
const KEY = /<data\s+name="([^"]+)"/g;

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

export function keysOf(text) {
    return new Set([...text.matchAll(KEY)].map((m) => m[1]));
}

export function compare(locales) {
    const union = new Set(locales.flatMap(([, keys]) => [...keys]));
    const missing = [];
    for (const [name, keys] of locales) {
        const gaps = [...union].filter((k) => !keys.has(k)).sort();
        if (gaps.length) missing.push([name, gaps]);
    }
    return missing;
}

// Naming a couple of keys is enough to place the mistake. During a bulk rename the
// gap runs to dozens and repeats on every edit, which is noise, not information.
const SHOWN = 3;

function readLocales(root) {
    const dir = path.join(root, STRINGS_DIR);
    if (!fs.existsSync(dir)) return [];

    const locales = [];
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
        if (!entry.isDirectory()) continue;
        const resw = path.join(dir, entry.name, 'Resources.resw');
        if (fs.existsSync(resw)) locales.push([entry.name, keysOf(fs.readFileSync(resw, 'utf8'))]);
    }
    return locales;
}

function main() {
    const root = process.env.CLAUDE_PROJECT_DIR ?? process.cwd();
    const listAll = process.argv.includes('--list');

    if (!listAll) {
        let payload;
        try {
            payload = JSON.parse(readStdin() || '{}');
        } catch {
            return 0;
        }
        const filePath = payload?.tool_response?.filePath ?? payload?.tool_input?.file_path;
        if (typeof filePath !== 'string' || !filePath.toLowerCase().endsWith('.resw')) return 0;
    }

    const locales = readLocales(root);
    if (locales.length < 2) return 0;

    const missing = compare(locales);
    if (!missing.length) {
        if (listAll) process.stdout.write('Locales are in sync.\n');
        return 0;
    }

    if (listAll) {
        for (const [name, gaps] of missing)
            process.stdout.write(`${name} is missing ${gaps.length}:\n${gaps.map((g) => '  ' + g).join('\n')}\n`);
        return 1;
    }

    let truncated = false;
    const detail = missing
        .map(([name, gaps]) => {
            if (gaps.length > SHOWN) truncated = true;
            const shown = gaps.slice(0, SHOWN).join(', ');
            return gaps.length > SHOWN
                ? `  ${name} is missing ${gaps.length}, starting ${shown}`
                : `  ${name} is missing ${gaps.length}: ${shown}`;
        })
        .join('\n');

    const hint = truncated
        ? '\n  Full list: node .claude/hooks/check-resw-sync.mjs --list'
        : '';
    process.stderr.write(
        `Resources.resw locales are out of sync (.claude/rules/70-localization.md).\n${detail}${hint}\n`
    );
    return 2;
}

if (process.argv[1] && import.meta.filename === process.argv[1]) {
    process.exit(main());
}
