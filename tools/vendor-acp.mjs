// The Claude ACP bridge: the official Agent Client Protocol adapter over the
// Claude Agent SDK, which is how the dashboard runs Claude. It is a Node package
// with its own dependencies, so it is installed at build time into a folder git
// ignores, like Monaco, rather than committed. Unlike the others it keeps its
// node_modules, and its install scripts run, because the app launches it.
//
// What is installed is exactly tools/acp/package-lock.json, with npm ci: the
// bridge runs as the user, and without the lockfile every machine resolved its
// transitive dependencies afresh on the day it built. To move to a newer
// bridge, change the version in tools/acp/package.json, run npm install
// --package-lock-only there, and commit both files; the VERSION marker below
// is read from the lockfile, so the next build notices.

import { join } from 'node:path';
import { copyFileSync, readFileSync } from 'node:fs';
import { app, fail, hasNpm, isCurrent, npm, replace, root } from './lib/vendor.mjs';

const PACKAGE = '@agentclientprotocol/claude-agent-acp';
const source = join(root, 'tools', 'acp');
const target = join(app, 'acp');

const lock = JSON.parse(readFileSync(join(source, 'package-lock.json'), 'utf8'));
const VERSION = lock.packages[`node_modules/${PACKAGE}`]?.version;
if (!VERSION) {
    fail('vendor-acp', `${PACKAGE} is not in tools/acp/package-lock.json`);
}

if (isCurrent(target, join('node_modules', ...PACKAGE.split('/'), 'dist', 'index.js'), VERSION)) {
    process.exit(0);
}

if (!hasNpm()) {
    console.error('vendor-acp: npm is not installed, so the Claude ACP bridge was not fetched.');
    console.error('vendor-acp: install Node 22 or newer and build again. The app runs, but cannot start agents.');
    process.exit(0);
}

console.log(`vendor-acp: installing ${PACKAGE}@${VERSION} from tools/acp/package-lock.json`);
replace(target, VERSION, staging => {
    copyFileSync(join(source, 'package.json'), join(staging, 'package.json'));
    copyFileSync(join(source, 'package-lock.json'), join(staging, 'package-lock.json'));
    const result = npm(['ci', '--no-fund', '--no-audit', '--silent'], staging);
    if (result.error || result.status !== 0) {
        fail('vendor-acp', `npm ci in tools/acp failed`);
    }
});
