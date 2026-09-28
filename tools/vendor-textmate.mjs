// VS Code's own tokenizer and the TextMate grammars it colours languages with, so
// a Razor file (and C#, HTML, CSS, JavaScript and the rest) reads in the editor
// the way it does in VS Code. Monaco's built-in grammars are a smaller hand-made
// set and Razor's is thin. Dependencies rather than source, so they are fetched
// into wwwroot at build time and ignored by git, like Monaco.
//
//   vscode-textmate   the tokenizer VS Code runs the grammars with
//   vscode-oniguruma  the regular expression engine they are written for (WASM)
//   tm-grammars       the grammars, collected from VS Code and the projects that
//                     own them (Razor's is dotnet/razor's), with their licences
//
// The editor fetches a grammar only when a file in its language is opened.

import { readdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { app, copy, hasNpm, install, isCurrent, replace } from './lib/vendor.mjs';

const TEXTMATE = '9.3.2';
const ONIGURUMA = '2.0.1';
const GRAMMARS = '1.32.22';
const VERSION = `${TEXTMATE}+${ONIGURUMA}+${GRAMMARS}`;
const target = join(app, 'wwwroot', 'textmate');

if (isCurrent(target, 'index.json', VERSION)) {
    process.exit(0);
}

if (!hasNpm()) {
    console.error('vendor-textmate: npm is needed, so files use Monaco\'s own colouring.');
    process.exit(0);
}

console.log(`vendor-textmate: fetching vscode-textmate@${TEXTMATE}, vscode-oniguruma@${ONIGURUMA}, tm-grammars@${GRAMMARS}`);
const modules = install('vendor-textmate', [`vscode-textmate@${TEXTMATE}`, `vscode-oniguruma@${ONIGURUMA}`, `tm-grammars@${GRAMMARS}`]);
const textmate = join(modules, 'vscode-textmate');
const oniguruma = join(modules, 'vscode-oniguruma');
const grammars = join(modules, 'tm-grammars');

// The package's index is an ES module. The editor wants two lookups from it: a
// grammar's file by its scope name, which is how one grammar embeds another,
// and a language's scope name by its name.
const { grammars: list } = await import(pathToFileURL(join(grammars, 'index.js')).href);
const index = { scopes: {}, names: {} };
for (const g of list) {
    index.scopes[g.scopeName] = g.name + '.json';
    index.names[g.name] = g.scopeName;
}

replace(target, VERSION, staging => {
    copy(join(textmate, 'release', 'main.js'), join(staging, 'vscode-textmate.js'));
    copy(join(oniguruma, 'release', 'main.js'), join(staging, 'vscode-oniguruma.js'));
    copy(join(oniguruma, 'release', 'onig.wasm'), join(staging, 'onig.wasm'));
    for (const file of readdirSync(join(grammars, 'grammars')).filter(f => f.endsWith('.json'))) {
        copy(join(grammars, 'grammars', file), join(staging, 'grammars', file));
    }
    copy(join(textmate, 'LICENSE.md'), join(staging, 'LICENSE-vscode-textmate.md'), { optional: true });
    copy(join(oniguruma, 'LICENSE.txt'), join(staging, 'LICENSE-vscode-oniguruma.txt'), { optional: true });
    copy(join(oniguruma, 'NOTICES.txt'), join(staging, 'NOTICES-vscode-oniguruma.txt'), { optional: true });
    copy(join(grammars, 'LICENSE'), join(staging, 'LICENSE-tm-grammars'), { optional: true });
    copy(join(grammars, 'NOTICE'), join(staging, 'NOTICE-tm-grammars'), { optional: true });
    writeFileSync(join(staging, 'index.json'), JSON.stringify(index));
});
