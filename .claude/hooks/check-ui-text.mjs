#!/usr/bin/env node
// PostToolUse check: the mechanically checkable half of the UI copy rules.
// Rule: .claude/rules/70-localization.md
import fs from 'node:fs';
import path from 'node:path';

const EM_DASH = '—';
const VALUE = /<data\s+name="([^"]+)"[^>]*>\s*<value>([\s\S]*?)<\/value>/g;

// Which words count as internal is a per-project judgement, so the list lives next
// to the hook rather than inside it. Absent file means the check is skipped and only
// the universal rules run, which is what makes this hook portable.
const TERMS_FILE = '.claude/hooks/ui-text-terms.json';

const SHOWN = 3;

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

export function entriesOf(text) {
    return [...text.matchAll(VALUE)].map((m) => ({ key: m[1], value: m[2] }));
}

export function loadTerms(root) {
    try {
        const raw = JSON.parse(fs.readFileSync(path.join(root, TERMS_FILE), 'utf8'));
        return Array.isArray(raw?.internal) ? raw.internal.filter((t) => typeof t === 'string' && t) : [];
    } catch {
        return [];
    }
}

function lineOf(text, needle) {
    const at = text.indexOf(needle);
    return at < 0 ? 0 : text.slice(0, at).split('\n').length;
}

/// Every finding for one resw file: em dashes, internal vocabulary, and a
/// description that merely repeats its own header.
export function auditResw(text, terms = []) {
    const found = [];
    const entries = entriesOf(text);
    const headers = new Map();

    for (const { key, value } of entries) {
        if (key.endsWith('.Header')) headers.set(key.slice(0, -'.Header'.length), value.trim());
    }

    for (const { key, value } of entries) {
        const line = lineOf(text, `name="${key}"`);

        if (value.includes(EM_DASH)) {
            found.push(`${line}: ${key} uses an em dash. Use a comma, a colon, parentheses, or a new sentence.`);
        }

        for (const term of terms) {
            if (value.toLowerCase().includes(term.toLowerCase())) {
                found.push(`${line}: ${key} names an internal concept ("${term}"). Say what the user sees instead.`);
                break;
            }
        }

        if (key.endsWith('.Description')) {
            const header = headers.get(key.slice(0, -'.Description'.length));
            if (header && header.length > 3 && value.includes(header)) {
                found.push(`${line}: ${key} repeats its own header ("${header}"). Say something the header does not.`);
            }
        }
    }
    return found;
}

/// XAML and the package manifest carry user-visible attributes too, but no
/// key/value shape, so only the em dash rule applies. Comments are blanked first,
/// keeping their line count so reported line numbers still point at the file.
export function auditMarkup(text) {
    const bare = text.replace(/<!--[\s\S]*?-->/g, (m) => '\n'.repeat((m.match(/\n/g) ?? []).length));
    const found = [];
    bare.split('\n').forEach((line, i) => {
        if (line.includes(EM_DASH)) {
            found.push(`${i + 1}: em dash in user-visible markup. Use a comma, a colon, parentheses, or a new sentence.`);
        }
    });
    return found;
}

/// Markdown gets the em dash rule and nothing else. Fenced code and inline spans are
/// blanked first: a dash inside a command or a table of measured output is data.
export function auditMarkdown(text) {
    const bare = text
        .replace(/```[\s\S]*?```/g, (m) => '\n'.repeat((m.match(/\n/g) ?? []).length))
        .replace(/`[^`\n]*`/g, '');

    const found = [];
    bare.split('\n').forEach((line, i) => {
        if (line.includes(EM_DASH)) {
            found.push(`${i + 1}: em dash in prose. Use a comma, a colon, parentheses, or a new sentence.`);
        }
    });
    return found;
}

function main() {
    const listIdx = process.argv.indexOf('--list');
    const listAll = listIdx !== -1;

    let filePath;
    if (listAll) {
        filePath = process.argv[listIdx + 1];
    } else {
        let payload;
        try {
            payload = JSON.parse(readStdin() || '{}');
        } catch {
            return 0;
        }
        filePath = payload?.tool_response?.filePath ?? payload?.tool_input?.file_path;
    }
    if (typeof filePath !== 'string' || !fs.existsSync(filePath)) return 0;

    const lower = filePath.toLowerCase();
    const isResw = lower.endsWith('.resw');
    const isMarkup = lower.endsWith('.xaml') || lower.endsWith('.appxmanifest');
    const isMarkdown = lower.endsWith('.md');
    if (!isResw && !isMarkup && !isMarkdown) return 0;

    const root = process.env.CLAUDE_PROJECT_DIR ?? process.cwd();
    const text = fs.readFileSync(filePath, 'utf8');
    const found = isResw ? auditResw(text, loadTerms(root))
        : isMarkup ? auditMarkup(text)
        : auditMarkdown(text);

    if (listAll) {
        process.stdout.write(found.length
            ? found.map((f) => `  ${filePath}:${f}`).join('\n') + '\n'
            : 'No findings.\n');
        return found.length ? 1 : 0;
    }

    if (!found.length) return 0;

    // Same reasoning as check-resw-sync: a long list repeated on every edit of a bulk
    // change is noise. Show enough to start on, and say how to see the rest.
    const shown = found.slice(0, SHOWN);
    const hint = found.length > SHOWN
        ? `\n  ${found.length - SHOWN} more: node .claude/hooks/check-ui-text.mjs --list "${filePath}"`
        : '';

    process.stderr.write(
        `Text people read (.claude/rules/70-localization.md).\n` +
            shown.map((f) => `  ${filePath}:${f}`).join('\n') + hint + '\n'
    );
    return 2;
}

if (process.argv[1] && import.meta.filename === process.argv[1]) {
    process.exit(main());
}
