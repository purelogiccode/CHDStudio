#!/usr/bin/env node
import { appendFileSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = join(dirname(fileURLToPath(import.meta.url)), '..', '..');

const args = process.argv.slice(2);
const getArg = (name) => {
    const index = args.indexOf(`--${name}`);
    return index === -1 ? undefined : args[index + 1];
};

const readProjectVersion = (relativePath) => {
    const path = join(repoRoot, relativePath);
    const text = readFileSync(path, 'utf8');
    const assembly = text.match(/<AssemblyVersion>([^<]+)<\/AssemblyVersion>/);
    const file = text.match(/<FileVersion>([^<]+)<\/FileVersion>/);
    if (!assembly || !file) {
        console.error(`Could not find <AssemblyVersion>/<FileVersion> in ${path}`);
        process.exit(1);
    }

    const assemblyVersion = assembly[1].trim();
    const fileVersion = file[1].trim();
    if (assemblyVersion !== fileVersion) {
        console.error(
            `${relativePath} has mismatched versions: AssemblyVersion=${assemblyVersion}, FileVersion=${fileVersion}.`
        );
        process.exit(1);
    }

    return assemblyVersion;
};

const version = readProjectVersion(join('CHDStudio', 'CHDStudio.csproj'));
const testsVersion = readProjectVersion(join('CHDStudio.Tests', 'CHDStudio.Tests.csproj'));
if (testsVersion !== version) {
    console.error(
        `Version mismatch: CHDStudio is ${version}, CHDStudio.Tests is ${testsVersion}. Update both project files together.`
    );
    process.exit(1);
}

const escapedVersion = version.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
const whatsNew = readFileSync(join(repoRoot, 'WhatsNew.md'), 'utf8').replace(/\r\n/g, '\n');
const notesHeading = new RegExp(`^## ${escapedVersion}(?:\\s|\\(|$)`, 'm');
if (!notesHeading.test(whatsNew)) {
    console.error(
        `No "## ${version}" section found in WhatsNew.md. Add the release notes for this version.`
    );
    process.exit(1);
}

const tag = getArg('tag');
if (tag !== undefined) {
    const expected = `release_${version}`;
    if (tag !== expected) {
        console.error(`Tag "${tag}" does not match the project version. Expected "${expected}" (update CHDStudio.csproj or the tag).`);
        process.exit(1);
    }
    console.log(`Tag "${tag}" matches CHDStudio ${version}.`);
}

if (args.includes('--github-output')) {
    const outputFile = process.env.GITHUB_OUTPUT;
    if (!outputFile) {
        console.error('--github-output was requested but GITHUB_OUTPUT is not set.');
        process.exit(1);
    }
    appendFileSync(outputFile, `version=${version}\n`);
}

console.log(version);
