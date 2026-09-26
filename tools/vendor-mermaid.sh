#!/usr/bin/env bash
set -euo pipefail

# Mermaid, which draws the diagrams in the Files tab's Markdown preview. Its
# browser build is one 5.5 MB script, a dependency rather than source, so it is
# fetched into wwwroot at build time and ignored by git, like Monaco. The page
# loads it only when a preview actually has a diagram in it.

VERSION=12.0.0

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
target="$root/src/AgentsDashboard.App/wwwroot/mermaid"

if [ -f "$target/mermaid.min.js" ] && [ -f "$target/VERSION" ] \
    && [ "$(cat "$target/VERSION")" = "$VERSION" ]; then
    exit 0
fi

if ! command -v npm >/dev/null 2>&1; then
    echo "vendor-mermaid: npm is not installed, so diagrams in Markdown previews will show as text." >&2
    exit 0
fi

temp="$(mktemp -d)"
trap 'rm -rf "$temp"' EXIT

echo "vendor-mermaid: fetching mermaid@$VERSION"
(cd "$temp" && npm pack "mermaid@$VERSION" --silent >/dev/null)
tarball="$(find "$temp" -maxdepth 1 -name '*.tgz' | head -n 1)"
tar -xzf "$tarball" -C "$temp"

staging="$target.partial"
rm -rf "$staging"
mkdir -p "$staging"
cp "$temp/package/dist/mermaid.min.js" "$staging/mermaid.min.js"
cp "$temp/package/LICENSE" "$staging/LICENSE"
printf '%s\n' "$VERSION" > "$staging/VERSION"

rm -rf "$target"
mv "$staging" "$target"
