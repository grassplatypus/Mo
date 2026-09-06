#!/usr/bin/env node
// PostToolUse check: every GetString("…") and x:Uid must resolve to a resw key.
// A typo compiles, throws nothing, and shows an empty string in the UI.
// Rule: .claude/rules/70-localization.md
import fs from 'node:fs';
import path from 'node:path';

const BASE_LOCALE = 'src/Mo/Strings/en-us/Resources.resw';
const KEY = /<data\s+name="([^"]+)"/g;
const CODE_REF = /GetString\(\s*"([^"]+)"/g;
const UID_REF = /x:Uid="([^"]+)"/g;

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

export function unresolved(text, keys, kind) {
    const found = [];
    const lines = text.split(/\r?\n/);
    const pattern = kind === 'code' ? CODE_REF : UID_REF;
    // x:Uid names a group of keys ("FooCard" -> FooCard.Header), a code key is exact.
    // The property suffix is what makes it resolve: a bare "FooCard" entry names no
    // property, so XAML sets nothing and the control renders blank with no error.
    const known = kind === 'code'
        ? keys
        : new Set([...keys].filter((k) => k.includes('.')).map((k) => k.split('.')[0]));
    for (let i = 0; i < lines.length; i++) {
        pattern.lastIndex = 0;
        for (const m of lines[i].matchAll(pattern)) {
            if (!known.has(m[1])) found.push([i + 1, m[1]]);
        }
    }
    return found;
}

function main() {
    let payload;
    try {
        payload = JSON.parse(readStdin() || '{}');
    } catch {
        return 0;
    }

    const filePath = payload?.tool_response?.filePath ?? payload?.tool_input?.file_path;
    if (typeof filePath !== 'string') return 0;
    const ext = path.extname(filePath).toLowerCase();
    if (!['.cs', '.xaml'].includes(ext)) return 0;

    const root = process.env.CLAUDE_PROJECT_DIR ?? process.cwd();
    const reswPath = path.join(root, BASE_LOCALE);
    if (!fs.existsSync(reswPath) || !fs.existsSync(filePath)) return 0;

    const keys = new Set([...fs.readFileSync(reswPath, 'utf8').matchAll(KEY)].map((m) => m[1]));
    const text = fs.readFileSync(filePath, 'utf8');
    const misses = unresolved(text, keys, ext === '.cs' ? 'code' : 'uid');
    if (!misses.length) return 0;

    const detail = misses.map(([line, key]) => `  ${filePath}:${line} - "${key}"`).join('\n');
    const hint = ext === '.cs'
        ? 'A missing key is silent at runtime: the UI just shows nothing.'
        : 'An x:Uid needs a key with a property suffix ("Foo.Text", "Foo.Header").\n' +
          'A bare "Foo" entry names no property, so the control renders blank.';
    process.stderr.write(
        `Resource keys that do not resolve in ${BASE_LOCALE} (.claude/rules/70-localization.md).\n` +
        `${hint}\n${detail}\n`
    );
    return 2;
}

if (process.argv[1] && import.meta.filename === process.argv[1]) {
    process.exit(main());
}
