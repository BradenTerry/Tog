#!/usr/bin/env bash
set -euo pipefail

# The Claude ACP bridge: the official Agent Client Protocol adapter over the
# Claude Agent SDK, which is how the dashboard runs Claude. It is a Node package
# with its own dependencies, so it is installed at build time into a folder git
# ignores, like Monaco, rather than committed.
#
# The VERSION marker makes the "already there" check honest: the entry point
# exists after any version was installed, so testing for it alone would pin the
# first version a machine fetched forever.

VERSION=0.81.2
PACKAGE=@agentclientprotocol/claude-agent-acp

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
target="$root/src/AgentsDashboard.App/acp"
entry="$target/node_modules/$PACKAGE/dist/index.js"

if [ -f "$entry" ] && [ -f "$target/VERSION" ] && [ "$(cat "$target/VERSION")" = "$VERSION" ]; then
    exit 0
fi

if ! command -v npm >/dev/null 2>&1; then
    echo "vendor-acp: npm is not installed, so the Claude ACP bridge was not fetched." >&2
    echo "vendor-acp: install Node 22 or newer and build again. The app runs, but cannot start agents." >&2
    exit 0
fi

# Installed into a sibling first and moved into place, so an interrupted install
# never leaves a half-populated folder that the check above would accept.
staging="$target.partial"
rm -rf "$staging"
mkdir -p "$staging"

echo "vendor-acp: installing $PACKAGE@$VERSION"
(cd "$staging" \
    && printf '{ "name": "agents-dashboard-acp", "private": true }\n' > package.json \
    && npm install "$PACKAGE@$VERSION" --no-fund --no-audit --silent >/dev/null)

printf '%s\n' "$VERSION" > "$staging/VERSION"

rm -rf "$target"
mv "$staging" "$target"
