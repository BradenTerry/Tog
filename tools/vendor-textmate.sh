#!/usr/bin/env bash
set -euo pipefail

# VS Code's own tokenizer and the TextMate grammars it colours languages with, so
# a Razor file (and C#, HTML, CSS, JavaScript and the rest) reads in the editor
# the way it does in VS Code. Monaco's built-in grammars are a smaller hand-made
# set and Razor's is thin. Dependencies rather than source, so they are fetched
# into wwwroot at build time and ignored by git, like Monaco.
#
#   vscode-textmate   the tokenizer VS Code runs the grammars with
#   vscode-oniguruma  the regular expression engine they are written for (WASM)
#   tm-grammars       the grammars, collected from VS Code and the projects that
#                     own them (Razor's is dotnet/razor's), with their licences
#
# The editor fetches a grammar only when a file in its language is opened.

TEXTMATE=9.3.2
ONIGURUMA=2.0.1
GRAMMARS=1.32.22
VERSION="$TEXTMATE+$ONIGURUMA+$GRAMMARS"

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
target="$root/src/AgentsDashboard.App/wwwroot/textmate"

if [ -f "$target/index.json" ] && [ -f "$target/VERSION" ] \
    && [ "$(cat "$target/VERSION")" = "$VERSION" ]; then
    exit 0
fi

if ! command -v npm >/dev/null 2>&1 || ! command -v node >/dev/null 2>&1; then
    echo "vendor-textmate: npm and node are needed, so files use Monaco's own colouring." >&2
    exit 0
fi

temp="$(mktemp -d)"
trap 'rm -rf "$temp"' EXIT

echo "vendor-textmate: fetching vscode-textmate@$TEXTMATE, vscode-oniguruma@$ONIGURUMA, tm-grammars@$GRAMMARS"
(cd "$temp" && npm pack "vscode-textmate@$TEXTMATE" "vscode-oniguruma@$ONIGURUMA" "tm-grammars@$GRAMMARS" --silent >/dev/null)
for package in textmate oniguruma grammars; do
    mkdir -p "$temp/$package"
done
tar -xzf "$temp"/vscode-textmate-*.tgz -C "$temp/textmate"
tar -xzf "$temp"/vscode-oniguruma-*.tgz -C "$temp/oniguruma"
tar -xzf "$temp"/tm-grammars-*.tgz -C "$temp/grammars"

staging="$target.partial"
rm -rf "$staging"
mkdir -p "$staging/grammars"

cp "$temp/textmate/package/release/main.js" "$staging/vscode-textmate.js"
cp "$temp/oniguruma/package/release/main.js" "$staging/vscode-oniguruma.js"
cp "$temp/oniguruma/package/release/onig.wasm" "$staging/onig.wasm"
cp "$temp/grammars/package/grammars/"*.json "$staging/grammars/"
cp "$temp/textmate/package/LICENSE.md" "$staging/LICENSE-vscode-textmate.md" 2>/dev/null || true
cp "$temp/oniguruma/package/LICENSE.txt" "$staging/LICENSE-vscode-oniguruma.txt" 2>/dev/null || true
cp "$temp/oniguruma/package/NOTICES.txt" "$staging/NOTICES-vscode-oniguruma.txt" 2>/dev/null || true
cp "$temp/grammars/package/LICENSE" "$staging/LICENSE-tm-grammars" 2>/dev/null || true
cp "$temp/grammars/package/NOTICE" "$staging/NOTICE-tm-grammars" 2>/dev/null || true

# The package's index is an ES module. The editor wants two lookups from it: a
# grammar's file by its scope name, which is how one grammar embeds another,
# and a language's scope name by its name.
node --input-type=module -e "
import { grammars } from '$temp/grammars/package/index.js';
import { writeFileSync } from 'node:fs';
const index = { scopes: {}, names: {} };
for (const g of grammars) {
    index.scopes[g.scopeName] = g.name + '.json';
    index.names[g.name] = g.scopeName;
}
writeFileSync('$staging/index.json', JSON.stringify(index));
"

printf '%s\n' "$VERSION" > "$staging/VERSION"

rm -rf "$target"
mv "$staging" "$target"
