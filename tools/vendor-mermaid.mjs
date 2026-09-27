// Mermaid, which draws the diagrams in the Files tab's Markdown preview. Its
// browser build is one 5.5 MB script, a dependency rather than source, so it is
// fetched into wwwroot at build time and ignored by git, like Monaco. The page
// loads it only when a preview actually has a diagram in it.

import { join } from 'node:path';
import { app, copy, hasNpm, install, isCurrent, replace } from './lib/vendor.mjs';

const VERSION = '12.0.0';
const target = join(app, 'wwwroot', 'mermaid');

if (isCurrent(target, 'mermaid.min.js', VERSION)) {
    process.exit(0);
}

if (!hasNpm()) {
    console.error('vendor-mermaid: npm is not installed, so diagrams in Markdown previews will show as text.');
    process.exit(0);
}

console.log(`vendor-mermaid: fetching mermaid@${VERSION}`);
const pkg = join(install('vendor-mermaid', [`mermaid@${VERSION}`]), 'mermaid');

replace(target, VERSION, staging => {
    copy(join(pkg, 'dist', 'mermaid.min.js'), join(staging, 'mermaid.min.js'));
    copy(join(pkg, 'LICENSE'), join(staging, 'LICENSE'));
});
