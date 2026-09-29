// Shared by the vendor-*.mjs scripts. They were bash, which a Windows machine
// may not have on the PATH MSBuild's Exec sees (or has WSL's bash there, which
// runs in another filesystem). Node is needed anyway, to fetch the packages and
// to run the ACP bridge, so the scripts are written for it.

import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, renameSync, utimesSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

export const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
export const app = join(root, 'src', 'Tog.App');

// Each vendored set is pinned by a committed package.json and package-lock.json
// in tools/vendor/<name>, and installed with npm ci, so every build gets the
// same tree, checked against the lockfile's integrity hashes, and a registry
// that serves something else fails the build instead of shipping it. To move a
// version, edit the package.json there and run npm install --package-lock-only
// --ignore-scripts beside it.
export const pins = join(root, 'tools', 'vendor');

// The marker a vendored folder is checked against: the lockfile's hash, so any
// change to what is pinned, a transitive dependency included, vendors again.
export function lockVersion(name) {
    return createHash('sha256').update(readFileSync(join(pins, name, 'package-lock.json'))).digest('hex');
}

// The version the pinned package.json names for one package, for messages.
export function pinned(name, pkg) {
    return JSON.parse(readFileSync(join(pins, name, 'package.json'), 'utf8')).dependencies[pkg];
}

// The VERSION marker makes the "already there" check honest: the entry file
// exists after any version was vendored, so testing for it alone would pin the
// first version a machine fetched forever.
//
// When it is current the marker is touched: MSBuild runs the script whenever
// the lockfile is newer than the marker, and a checkout rewrites the lockfile's
// time without changing it, which would otherwise run the script every build.
export function isCurrent(target, entry, version) {
    const marker = join(target, 'VERSION');
    const current = existsSync(join(target, entry)) && existsSync(marker)
        && readFileSync(marker, 'utf8').trim() === version;
    if (current) {
        const now = new Date();
        utimesSync(marker, now, now);
    }
    return current;
}

// npm is a .cmd shim on Windows, which Node only starts through a shell.
export function npm(args, cwd) {
    const windows = process.platform === 'win32';
    return spawnSync(windows ? 'npm.cmd' : 'npm', args, { cwd, stdio: ['ignore', 'ignore', 'inherit'], shell: windows });
}

export function hasNpm() {
    const result = npm(['--version']);
    return !result.error && result.status === 0;
}

// Installs a pinned set into a folder with npm ci and returns its
// node_modules. npm install rather than npm pack, so nothing has to untar:
// Windows' tar and Git's GNU tar disagree about what C:\ means. Install scripts
// never run; nothing pinned needs one, and a compromised package's most common
// foothold is its postinstall.
export function installInto(name, dir) {
    copy(join(pins, name, 'package.json'), join(dir, 'package.json'));
    copy(join(pins, name, 'package-lock.json'), join(dir, 'package-lock.json'));
    const result = npm(['ci', '--ignore-scripts', '--no-fund', '--no-audit', '--silent'], dir);
    if (result.error || result.status !== 0) {
        fail(`vendor-${name}`, `npm ci from tools/vendor/${name} failed`);
    }
    return join(dir, 'node_modules');
}

// The same, into a scratch folder that is removed when the script ends.
export function install(name) {
    const temp = mkdtempSync(join(tmpdir(), `vendor-${name}-`));
    process.on('exit', () => rmSync(temp, { recursive: true, force: true }));
    return installInto(name, temp);
}

// Written to a sibling first and moved into place, so an interrupted copy never
// leaves a half-populated folder that isCurrent would then accept.
export function replace(target, version, fill) {
    const staging = `${target}.partial`;
    rmSync(staging, { recursive: true, force: true });
    mkdirSync(staging, { recursive: true });
    fill(staging);
    writeFileSync(join(staging, 'VERSION'), `${version}\n`);
    rmSync(target, { recursive: true, force: true });
    renameSync(staging, target);
}

export function copy(from, to, { optional = false } = {}) {
    if (optional && !existsSync(from)) {
        return;
    }
    cpSync(from, to, { recursive: true });
}

export function fail(name, message) {
    console.error(`${name}: ${message}`);
    process.exit(1);
}
