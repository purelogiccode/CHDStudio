#!/usr/bin/env node
import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const docsDir = join(repoRoot, 'docs');

const token = process.env.WIKI_TOKEN;
if (!token) {
    console.log('WIKI_TOKEN is not set - skipping wiki sync. Add a repository secret named WIKI_TOKEN (classic PAT with the "repo" scope, or fine-grained with Contents: Read and write) to enable it. See AGENTS.md.');
    process.exit(0);
}

const repo = process.env.GITHUB_REPOSITORY;
if (!repo) {
    console.error('GITHUB_REPOSITORY is not set.');
    process.exit(1);
}

const remote = process.env.WIKI_GIT_URL ?? `https://x-access-token:${token}@github.com/${repo}.wiki.git`;
const sha = process.env.GITHUB_SHA ?? '';
const workDir = mkdtempSync(join(tmpdir(), 'chdstudio-wiki-'));
const wikiDir = join(workDir, 'wiki');
const gitEnv = { ...process.env, GIT_TERMINAL_PROMPT: '0' };

const git = (args, cwd) => execFileSync('git', args, { cwd, env: gitEnv, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
const sanitize = (text) => text.split(token).join('***');

const stripFrontMatter = (text) => {
    const normalized = text.replace(/\r\n/g, '\n');
    if (!normalized.startsWith('---\n')) {
        return normalized;
    }
    const end = normalized.indexOf('\n---', 3);
    if (end === -1) {
        return normalized;
    }
    const after = normalized.indexOf('\n', end + 1);
    return after === -1 ? '' : normalized.slice(after + 1).replace(/^\n+/, '');
};

// GitHub Pages resolves `page.md` links to `page.html`; the wiki does not. Rewrite
// relative Markdown links to the flat wiki page names so the same docs tree works in
// both homes. Absolute URLs, anchors and external links are left untouched.
const toWikiLinks = (text) =>
    text.replace(
        /\]\((?!https?:\/\/)([^()\s]+?)\.md(#[^)]+)?\)/g,
        (match, target, anchor = '') => {
            const page = target
                .replace(/^\.\//, '')
                .replace(/^docs\//, '')
                .replace(/^index$/, 'Home');
            return `](${page}${anchor})`;
        }
    );

try {
    git(['-c', 'core.autocrlf=false', 'clone', '--depth', '1', remote, wikiDir]);
    git(['config', 'core.autocrlf', 'false'], wikiDir);

    const changed = [];
    const expectedTargets = new Set();
    for (const name of readdirSync(docsDir)) {
        if (!name.endsWith('.md')) {
            continue;
        }
        const target = name === 'index.md' ? 'Home.md' : name;
        expectedTargets.add(target);
        const content = toWikiLinks(
            stripFrontMatter(readFileSync(join(docsDir, name), 'utf8'))
        );
        const targetPath = join(wikiDir, target);
        let previous = '';
        try {
            previous = readFileSync(targetPath, 'utf8').replace(/\r\n/g, '\n');
        } catch {
            previous = '';
        }
        if (previous !== content) {
            writeFileSync(targetPath, content, 'utf8');
            changed.push(target);
        }
    }

    const whatsNew = toWikiLinks(
        stripFrontMatter(readFileSync(join(repoRoot, 'WhatsNew.md'), 'utf8'))
    ).replace(/\r\n/g, '\n');
    const whatsNewTarget = join(wikiDir, 'WhatsNew.md');
    expectedTargets.add('WhatsNew.md');
    let previousWhatsNew = '';
    try {
        previousWhatsNew = readFileSync(whatsNewTarget, 'utf8').replace(/\r\n/g, '\n');
    } catch {
        previousWhatsNew = '';
    }
    if (previousWhatsNew !== whatsNew) {
        writeFileSync(whatsNewTarget, whatsNew, 'utf8');
        changed.push('WhatsNew.md');
    }

    // Delete generated pages that no longer exist in docs/. Manual wiki pages
    // that do not follow the docs naming scheme (for example _Footer.md or a
    // hand-written scratch page) are preserved.
    const generatedPagePattern = /^(?:_Sidebar|Home|WhatsNew|\d{2}-.+)\.md$/;
    for (const name of readdirSync(wikiDir)) {
        if (!name.endsWith('.md') || expectedTargets.has(name)) {
            continue;
        }
        if (!generatedPagePattern.test(name)) {
            continue;
        }
        rmSync(join(wikiDir, name), { force: true });
        changed.push(`deleted ${name}`);
    }

    if (changed.length === 0) {
        console.log('Wiki is already up to date.');
        process.exit(0);
    }

    const message = `docs: sync wiki from docs/${sha ? ` (${sha.slice(0, 7)})` : ''}`;
    git(['-c', 'user.name=github-actions[bot]', '-c', 'user.email=41898282+github-actions[bot]@users.noreply.github.com', 'add', '-A'], wikiDir);
    git(['-c', 'user.name=github-actions[bot]', '-c', 'user.email=41898282+github-actions[bot]@users.noreply.github.com', 'commit', '-m', message], wikiDir);
    git(['push', 'origin', 'HEAD:master'], wikiDir);
    console.log(`Wiki updated (${changed.length} file(s)): ${changed.join(', ')}`);
} catch (error) {
    const details = sanitize(String(error.stderr || '')) + sanitize(String(error.stdout || '')) || sanitize(String(error.message || error));
    console.error(`Wiki sync failed:\n${details}`);
    process.exit(1);
} finally {
    rmSync(workDir, { recursive: true, force: true });
}
