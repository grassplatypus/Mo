#!/usr/bin/env node
// PostToolUse check on build files: versions must agree, and the build must not be
// made quieter — no new NoWarn, no turning warnings-as-errors off.
// Rule: .claude/rules/60-build-and-git.md
import fs from 'node:fs';
import path from 'node:path';

const PROPS = 'Directory.Build.props';
const MANIFEST = 'src/Mo/Package.appxmanifest';
// NETSDK1233 predates this check; anything new has to be argued for in review.
const ALLOWED_NOWARN = new Set(['NETSDK1233']);

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

export function versionMismatch(propsText, manifestText) {
    const mo = propsText.match(/<MoVersion>([^<]+)<\/MoVersion>/)?.[1];
    const identity = manifestText.match(/<Identity[^>]*\sVersion="([^"]+)"/s)?.[1];
    if (!mo || !identity) return null;

    // Only the three-part release version has to agree. The fourth component is the
    // build stamp Publish-Release.ps1 writes, and it differs on every build by design.
    const stamped = identity.split('.').slice(0, 3).join('.');
    return stamped === mo ? null : [mo, identity];
}

export function suppressionFindings(text) {
    const found = [];
    const lines = text.split(/\r?\n/);
    for (let i = 0; i < lines.length; i++) {
        const noWarn = lines[i].match(/<NoWarn>([^<]*)<\/NoWarn>/);
        if (noWarn) {
            const codes = noWarn[1]
                .split(';')
                .map((c) => c.trim())
                .filter((c) => c && !c.startsWith('$('));
            const added = codes.filter((c) => !ALLOWED_NOWARN.has(c));
            if (added.length) {
                found.push([i + 1, `NoWarn adds ${added.join(', ')} — silencing a warning project-wide hides the next real one`]);
            }
        }
        if (/<TreatWarningsAsErrors>\s*false\s*<\/TreatWarningsAsErrors>/i.test(lines[i])) {
            found.push([i + 1, 'TreatWarningsAsErrors turned off — the build is warning-clean today, keep it that way']);
        }
        const level = lines[i].match(/<WarningLevel>\s*(\d+)\s*<\/WarningLevel>/);
        if (level && Number(level[1]) < 4) {
            found.push([i + 1, `WarningLevel lowered to ${level[1]}`]);
        }
    }
    return found;
}

// The only keywords PowerShell accepts inside a comment-based help block.
const HELP_KEYWORDS = new Set([
    'SYNOPSIS', 'DESCRIPTION', 'PARAMETER', 'EXAMPLE', 'INPUTS', 'OUTPUTS', 'NOTES',
    'LINK', 'COMPONENT', 'ROLE', 'FUNCTIONALITY', 'FORWARDHELPTARGETNAME',
    'FORWARDHELPCATEGORY', 'REMOTEHELPRUNSPACE', 'EXTERNALHELP',
]);

/// A help line starting with a dot is read as a keyword, and one unknown keyword makes
/// PowerShell discard the entire block. ".NET 10 SDK ..." silently killed Get-Help for
/// Publish-Release.ps1; nothing failed, the help just stopped existing.
export function helpKeywordFindings(text) {
    const lines = text.split(/\r?\n/);
    const start = lines.findIndex((l) => l.trim().startsWith('<#'));
    if (start === -1) return [];

    const found = [];
    for (let i = start + 1; i < lines.length; i++) {
        if (lines[i].trim().startsWith('#>')) break;
        const word = lines[i].match(/^\s*\.([A-Za-z][A-Za-z0-9]*)/)?.[1];
        if (word && !HELP_KEYWORDS.has(word.toUpperCase())) {
            found.push([i + 1, `help line starts with ".${word}", which PowerShell reads as a keyword`]);
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
    if (!['.csproj', '.props', '.targets', '.appxmanifest', '.ps1'].includes(ext)) return 0;

    const root = process.env.CLAUDE_PROJECT_DIR ?? process.cwd();
    const rel = path.relative(root, filePath).split(path.sep).join('/');
    if (rel.startsWith('..') || rel.includes('/obj/')) return 0;

    const problems = [];

    if (ext === '.ps1') {
        if (!fs.existsSync(filePath)) return 0;
        for (const [line, why] of helpKeywordFindings(fs.readFileSync(filePath, 'utf8'))) {
            problems.push(`  ${filePath}:${line}\n    ${why}, so Get-Help returns nothing`);
        }
        if (!problems.length) return 0;
        process.stderr.write(`Build integrity (.claude/rules/60-build-and-git.md):\n${problems.join('\n')}\n`);
        return 2;
    }

    if (ext !== '.appxmanifest' && fs.existsSync(filePath)) {
        for (const [line, why] of suppressionFindings(fs.readFileSync(filePath, 'utf8'))) {
            problems.push(`  ${filePath}:${line}\n    ${why}`);
        }
    }

    const propsPath = path.join(root, PROPS);
    const manifestPath = path.join(root, MANIFEST);
    if (fs.existsSync(propsPath) && fs.existsSync(manifestPath)) {
        const mismatch = versionMismatch(
            fs.readFileSync(propsPath, 'utf8'),
            fs.readFileSync(manifestPath, 'utf8')
        );
        if (mismatch) {
            problems.push(
                `  MoVersion is ${mismatch[0]} but Package.appxmanifest says ${mismatch[1]}\n` +
                `    They must agree (manifest = MoVersion + ".0") or the release ships mislabelled.`
            );
        }
    }

    if (!problems.length) return 0;
    process.stderr.write(`Build integrity (.claude/rules/60-build-and-git.md):\n${problems.join('\n')}\n`);
    return 2;
}

if (process.argv[1] && import.meta.filename === process.argv[1]) {
    process.exit(main());
}
