// Shared by the vendor-*.mjs scripts. They were bash, which a Windows machine
// may not have on the PATH MSBuild's Exec sees (or has WSL's bash there, which
// runs in another filesystem). Node is needed anyway, to fetch the packages and
// to run the ACP bridge, so the scripts are written for it.

import { spawnSync } from 'node:child_process';
import { cpSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, renameSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

export const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
export const app = join(root, 'src', 'AgentsDashboard.App');

// The VERSION marker makes the "already there" check honest: the entry file
// exists after any version was vendored, so testing for it alone would pin the
// first version a machine fetched forever.
export function isCurrent(target, entry, version) {
    const marker = join(target, 'VERSION');
    return existsSync(join(target, entry)) && existsSync(marker)
        && readFileSync(marker, 'utf8').trim() === version;
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

// Installs packages into a scratch folder and hands back its node_modules.
// npm install rather than npm pack, so nothing has to untar: Windows' tar and
// Git's GNU tar disagree about what C:\ means.
export function install(name, packages) {
    const temp = mkdtempSync(join(tmpdir(), `${name}-`));
    process.on('exit', () => rmSync(temp, { recursive: true, force: true }));
    writeFileSync(join(temp, 'package.json'), '{ "name": "agents-dashboard-vendor", "private": true }\n');
    const result = npm(['install', ...packages, '--ignore-scripts', '--no-fund', '--no-audit', '--no-package-lock', '--silent'], temp);
    if (result.error || result.status !== 0) {
        fail(name, `npm install ${packages.join(' ')} failed`);
    }
    return join(temp, 'node_modules');
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
