// Monaco's AMD build is 24 MB of minified modules and workers. Committing it
// would dominate the repository and every clone, and it is a dependency rather
// than source, so it is fetched into wwwroot at build time and ignored by git.

import { existsSync } from 'node:fs';
import { join } from 'node:path';
import { app, copy, fail, install, isCurrent, lockVersion, pinned, replace } from './lib/vendor.mjs';

const VERSION = lockVersion('monaco');
const MONACO = pinned('monaco', 'monaco-editor');
const target = join(app, 'wwwroot', 'monaco');

if (isCurrent(target, join('vs', 'loader.js'), VERSION)) {
    process.exit(0);
}

// An already-extracted package can be handed in as the first argument, which is
// how a machine with no network (or a build that already downloaded it once)
// vendors without going back to the registry.
let pkg = process.argv[2];

if (pkg) {
    if (!existsSync(join(pkg, 'min', 'vs', 'loader.js'))) {
        fail('vendor-monaco', `${pkg} is not an extracted monaco-editor package`);
    }
} else {
    console.log(`vendor-monaco: fetching monaco-editor@${MONACO}`);
    pkg = join(install('monaco'), 'monaco-editor');
}

replace(target, VERSION, staging => {
    copy(join(pkg, 'min', 'vs'), join(staging, 'vs'));
    copy(join(pkg, 'LICENSE'), join(staging, 'LICENSE'));
});

console.log(`vendor-monaco: monaco-editor@${MONACO} is in wwwroot/monaco`);
