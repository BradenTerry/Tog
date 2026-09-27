// The Claude ACP bridge: the official Agent Client Protocol adapter over the
// Claude Agent SDK, which is how the dashboard runs Claude. It is a Node package
// with its own dependencies, so it is installed at build time into a folder git
// ignores, like Monaco, rather than committed. Unlike the others it keeps its
// node_modules, and its install scripts run, because the app launches it.

import { join } from 'node:path';
import { app, fail, hasNpm, isCurrent, npm, replace } from './lib/vendor.mjs';
import { writeFileSync } from 'node:fs';

const VERSION = '0.81.2';
const PACKAGE = '@agentclientprotocol/claude-agent-acp';
const target = join(app, 'acp');

if (isCurrent(target, join('node_modules', ...PACKAGE.split('/'), 'dist', 'index.js'), VERSION)) {
    process.exit(0);
}

if (!hasNpm()) {
    console.error('vendor-acp: npm is not installed, so the Claude ACP bridge was not fetched.');
    console.error('vendor-acp: install Node 22 or newer and build again. The app runs, but cannot start agents.');
    process.exit(0);
}

console.log(`vendor-acp: installing ${PACKAGE}@${VERSION}`);
replace(target, VERSION, staging => {
    writeFileSync(join(staging, 'package.json'), '{ "name": "agents-dashboard-acp", "private": true }\n');
    const result = npm(['install', `${PACKAGE}@${VERSION}`, '--no-fund', '--no-audit', '--silent'], staging);
    if (result.error || result.status !== 0) {
        fail('vendor-acp', `npm install ${PACKAGE}@${VERSION} failed`);
    }
});
