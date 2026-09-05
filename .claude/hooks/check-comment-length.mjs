#!/usr/bin/env node
// PostToolUse check: prose comments cap at MAX_LINES; an XML doc block is measured
// per tag instead, so <summary>/<param>/<returns> each get their own budget.
// Rule: .claude/rules/10-code-style.md
import fs from 'node:fs';
import path from 'node:path';

const MAX_LINES = 3;
const DOC = /^\s*\/\/\//;
const DOC_TAG = /^<(summary|remarks|param|typeparam|returns|response|exception|value|example|seealso|permission|inheritdoc)\b/;

const SLASHES = { line: /^\s*\/\//, doc: DOC, block: [/^\s*\/\*/, /\*\//] };
// PowerShell comment-based help is sectioned like XML doc, so measure it the same way.
const HASH = { line: /^\s*#/, doc: null, block: [/^\s*<#/, /#>/], section: /^\.[A-Z]+/ };
const ANGLE = { line: null, doc: null, block: [/^\s*<!--/, /-->/] };

const SYNTAX = {
    '.cs': SLASHES,
    '.csx': SLASHES,
    '.js': SLASHES,
    '.mjs': SLASHES,
    '.cjs': SLASHES,
    '.ts': SLASHES,
    '.xaml': ANGLE,
    '.xml': ANGLE,
    '.ps1': HASH,
    '.psm1': HASH,
    '.py': { line: /^\s*#/, doc: null, block: null },
    '.yml': { line: /^\s*#/, doc: null, block: null },
    '.yaml': { line: /^\s*#/, doc: null, block: null },
};

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

const OPENERS = /^\s*(\/\*|<!--|<#)/;
const TERMINATOR = /^(#>|\*\/|-->)$/;

// Structured comments get one budget per section rather than one for the whole block:
// an XML doc <summary>/<param>/<returns>, a PowerShell .SYNOPSIS/.PARAMETER/.EXAMPLE.
// A closing tag stays with the section it closes.
function sectionViolations(run, offset, strip, isHeading, kind) {
    const found = [];
    let start = 0;
    let span = 0;
    const flush = () => {
        if (span > MAX_LINES) found.push([offset + start + 1, span, kind]);
    };
    for (let i = 0; i < run.length; i++) {
        const content = strip ? run[i].replace(strip, '').trim() : run[i].trim();
        if (span > 0 && isHeading.test(content)) {
            flush();
            start = i;
            span = 1;
            continue;
        }
        // Blank lines and the closing delimiter are structure, not prose.
        if (!content || TERMINATOR.test(content)) continue;
        if (span === 0) start = i;
        span++;
    }
    flush();
    return found;
}

function kindOf(line, syntax) {
    if (syntax.doc && syntax.doc.test(line)) return 'doc';
    if (syntax.line && syntax.line.test(line)) return 'line';
    return null;
}

export function findViolations(lines, syntax) {
    const found = [];
    let i = 0;
    while (i < lines.length) {
        const line = lines[i];
        if (syntax.block && syntax.block[0].test(line)) {
            let end = i;
            if (!syntax.block[1].test(line.replace(OPENERS, ''))) {
                end = i + 1;
                while (end < lines.length && !syntax.block[1].test(lines[end])) end++;
                if (end >= lines.length) end = lines.length - 1;
            }
            const run = lines.slice(i, end + 1);
            const sectioned = syntax.section && run.some((l) => syntax.section.test(l.trim()));
            if (sectioned) found.push(...sectionViolations(run, i, null, syntax.section, 'section'));
            else if (run.length > MAX_LINES) found.push([i + 1, run.length, 'block']);
            i = end + 1;
            continue;
        }

        const kind = kindOf(line, syntax);
        if (!kind) { i++; continue; }

        let end = i;
        while (end < lines.length && kindOf(lines[end], syntax) === kind) end++;
        const run = lines.slice(i, end);

        if (kind === 'doc') found.push(...sectionViolations(run, i, DOC, DOC_TAG, 'doc'));
        else if (run.length > MAX_LINES) found.push([i + 1, run.length, 'prose']);
        i = end;
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

    const syntax = SYNTAX[path.extname(filePath).toLowerCase()];
    if (!syntax) return 0;

    let text;
    try {
        text = fs.readFileSync(filePath, 'utf8');
    } catch {
        return 0;
    }

    // An Edit is judged only on the span it wrote, so untouched legacy
    // comments elsewhere in the file do not block an unrelated change.
    let range = null;
    const written = payload?.tool_input?.new_string;
    if (payload?.tool_name === 'Edit' && typeof written === 'string') {
        const at = text.indexOf(written);
        if (at < 0) return 0;
        const from = text.slice(0, at).split(/\r?\n/).length;
        range = [from, from + written.split(/\r?\n/).length - 1];
    }

    const violations = findViolations(text.split(/\r?\n/), syntax)
        .filter(([line, span]) => !range || (line + span - 1 >= range[0] && line <= range[1]));
    if (!violations.length) return 0;

    const label = {
        doc: 'XML doc tag', section: 'help section', block: 'block comment', prose: 'comment run',
    };
    const detail = violations
        .map(([line, span, kind]) => `  ${filePath}:${line} - ${span}-line ${label[kind] ?? 'comment'}`)
        .join('\n');
    process.stderr.write(
        `Limit is ${MAX_LINES} lines per comment run, and ${MAX_LINES} per XML doc tag ` +
        `(.claude/rules/10-code-style.md). Shorten these:\n${detail}\n`
    );
    return 2;
}

if (process.argv[1] && import.meta.filename === process.argv[1]) {
    process.exit(main());
}
