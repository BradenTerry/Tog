#!/usr/bin/env bash
# Publishes this checkout of the app into a macOS app bundle, by default
# ~/Desktop/Agents Dashboard.app, replacing what is there.
#
# The bundle is a zsh launcher plus a framework-dependent `dotnet publish` in
# Contents/Resources/app. The launcher gives the app the PATH a terminal would
# have, because Finder starts apps with a bare one and the app runs node, git,
# claude and dotnet. The ACP bridge in src/AgentsDashboard.App/acp is not part
# of the publish output (it is not a static asset), so it is copied in.
# Extensions live in the user's data directory, not the bundle, and are left
# alone.
#
# Usage: tools/publish-local.sh [path/to/Agents Dashboard.app]
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
bundle="${1:-$HOME/Desktop/Agents Dashboard.app}"
app_dir="$bundle/Contents/Resources/app"

if pgrep -f "$app_dir/agents-dashboard" >/dev/null; then
    echo "The app in $bundle is running. Quit it first, then publish again." >&2
    exit 1
fi

staging="$(mktemp -d)"
trap 'rm -rf "$staging"' EXIT

echo "Publishing $(git -C "$repo" rev-parse --abbrev-ref HEAD) at $(git -C "$repo" rev-parse --short HEAD)"
dotnet publish "$repo/src/AgentsDashboard.App" -c Release -o "$staging/app" --nologo -v quiet

acp="$repo/src/AgentsDashboard.App/acp"
if [[ ! -f "$acp/VERSION" ]]; then
    echo "The ACP bridge is not installed in $acp. Build once (it runs tools/vendor-acp.sh), then publish again." >&2
    exit 1
fi
# The publish output already has acp/ with the package json files in it, and
# cp -R into an existing directory nests the copy as acp/acp, so replace it.
rm -rf "$staging/app/acp"
cp -R "$acp" "$staging/app/acp"

# A new bundle gets the launcher and Info.plist; an existing one keeps its own.
mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"
if [[ ! -f "$bundle/Contents/MacOS/launcher" ]]; then
    cat >"$bundle/Contents/MacOS/launcher" <<'LAUNCHER'
#!/bin/zsh
# Finder launches apps with a bare PATH, so give it the one node, git, claude and dotnet live on.
export PATH="$HOME/.local/bin:/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin"
export DOTNET_ROOT="${DOTNET_ROOT:-/usr/local/share/dotnet}"
APP_DIR="$(cd "$(dirname "$0")/../Resources/app" && pwd)"
cd "$APP_DIR"
LOG_DIR="$HOME/Library/Logs/AgentsDashboard"
mkdir -p "$LOG_DIR"
exec "$APP_DIR/agents-dashboard" "$@" >>"$LOG_DIR/app.log" 2>&1
LAUNCHER
    chmod +x "$bundle/Contents/MacOS/launcher"
fi

if [[ ! -f "$bundle/Contents/Info.plist" ]]; then
    cat >"$bundle/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>Agents Dashboard</string>
    <key>CFBundleDisplayName</key><string>Agents Dashboard</string>
    <key>CFBundleIdentifier</key><string>com.braden.agentsdashboard</string>
    <key>CFBundleVersion</key><string>1.0</string>
    <key>CFBundleShortVersionString</key><string>1.0</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleExecutable</key><string>launcher</string>
    <key>LSMinimumSystemVersion</key><string>12.0</string>
    <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST
fi

# Swap the payload in whole, so a failed copy never leaves half an app.
if [[ -d "$app_dir" ]]; then
    mv "$app_dir" "$staging/previous"
fi
mv "$staging/app" "$app_dir"

# Ad hoc signing, as before: enough for Gatekeeper to run a locally built app.
codesign --force --deep -s - "$bundle" 2>/dev/null

echo "Published to $bundle"
