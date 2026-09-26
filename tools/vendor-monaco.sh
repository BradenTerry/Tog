#!/usr/bin/env bash
set -euo pipefail

# Monaco's AMD build is 24 MB of minified modules and workers. Committing it
# would dominate the repository and every clone, and it is a dependency rather
# than source, so it is fetched into wwwroot at build time and ignored by git.
#
# The VERSION marker next to it is what makes the "already there" check honest:
# loader.js exists after any version was vendored, so testing for the file alone
# would pin the first version fetched on a machine forever.

VERSION=0.56.0

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
target="$root/src/AgentsDashboard.App/wwwroot/monaco"

if [ -f "$target/vs/loader.js" ] && [ -f "$target/VERSION" ] \
    && [ "$(cat "$target/VERSION")" = "$VERSION" ]; then
    exit 0
fi

# An already-extracted package can be handed in as the first argument, which is
# how a machine with no network (or a build that already downloaded it once)
# vendors without going back to the registry.
package="${1:-}"

if [ -n "$package" ]; then
    if [ ! -f "$package/min/vs/loader.js" ]; then
        echo "vendor-monaco: $package is not an extracted monaco-editor package" >&2
        exit 1
    fi
else
    temp="$(mktemp -d)"
    trap 'rm -rf "$temp"' EXIT

    echo "vendor-monaco: fetching monaco-editor@$VERSION"
    (cd "$temp" && npm pack "monaco-editor@$VERSION" --silent >/dev/null)
    tarball="$(find "$temp" -maxdepth 1 -name '*.tgz' | head -n 1)"

    if [ -z "$tarball" ]; then
        echo "vendor-monaco: npm pack produced no tarball" >&2
        exit 1
    fi

    tar -xzf "$tarball" -C "$temp"
    package="$temp/package"
fi

# Written to a sibling first and moved into place, so an interrupted copy never
# leaves a half-populated vs/ that the check above would then accept.
staging="$target.partial"
rm -rf "$staging"
mkdir -p "$staging"

cp -R "$package/min/vs" "$staging/vs"
cp "$package/LICENSE" "$staging/LICENSE"
printf '%s\n' "$VERSION" > "$staging/VERSION"

rm -rf "$target"
mv "$staging" "$target"

echo "vendor-monaco: monaco-editor@$VERSION is in wwwroot/monaco"
