// The Claude ACP bridge: the official Agent Client Protocol adapter over the
// Claude Agent SDK, which is how the dashboard runs Claude. It is a Node package
// with its own dependencies, so it is installed at build time into a folder git
// ignores, like Monaco, rather than committed. Unlike the others it keeps its
// node_modules, because the app launches it.
//
// What is installed is exactly tools/vendor/acp/package-lock.json, with npm ci
// and install scripts off: the bridge runs as the user, and without the
// lockfile every machine resolved its transitive dependencies afresh on the day
// it built. To move to a newer bridge, change the version in
// tools/vendor/acp/package.json, run npm install --package-lock-only
// --ignore-scripts there, and commit both files; the VERSION marker is the
// lockfile's hash, so the next build notices.

import { join } from 'node:path';
import { app, hasNpm, installInto, isCurrent, lockVersion, pinned, replace } from './lib/vendor.mjs';

const PACKAGE = '@agentclientprotocol/claude-agent-acp';
const VERSION = lockVersion('acp');
const target = join(app, 'acp');

if (isCurrent(target, join('node_modules', ...PACKAGE.split('/'), 'dist', 'index.js'), VERSION)) {
    process.exit(0);
}

if (!hasNpm()) {
    console.error('vendor-acp: npm is not installed, so the Claude ACP bridge was not fetched.');
    console.error('vendor-acp: install Node 22 or newer and build again. The app runs, but cannot start agents.');
    process.exit(0);
}

console.log(`vendor-acp: installing ${PACKAGE}@${pinned('acp', PACKAGE)} from tools/vendor/acp/package-lock.json`);
replace(target, VERSION, staging => installInto('acp', staging));
