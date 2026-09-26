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
# With the app running its files cannot be swapped, so the build is staged
# instead, in Contents/Resources/app.next with a .staged marker written last.
# The app notices the marker and offers Update available in its status bar;
# the launcher swaps the staged build in on the next start, however the app is
# started.
#
# Usage: tools/publish-local.sh [path/to/Agents Dashboard.app]
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
bundle="${1:-$HOME/Desktop/Agents Dashboard.app}"
app_dir="$bundle/Contents/Resources/app"

next_dir="$bundle/Contents/Resources/app.next"
running=false
if pgrep -f "$app_dir/agents-dashboard" >/dev/null; then
    running=true
fi

staging="$(mktemp -d)"
trap 'rm -rf "$staging"' EXIT

commit="$(git -C "$repo" rev-parse --short HEAD)"
echo "Publishing $(git -C "$repo" rev-parse --abbrev-ref HEAD) at $commit"
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

# The launcher is rewritten every time, since it carries the swap below and an
# older one would never apply a staged build. Info.plist is only written once.
mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"
cat >"$bundle/Contents/MacOS/launcher" <<'LAUNCHER'
#!/bin/zsh
# Finder launches apps with a bare PATH, so give it the one node, git, claude and dotnet live on.
export PATH="$HOME/.local/bin:/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin"
export DOTNET_ROOT="${DOTNET_ROOT:-/usr/local/share/dotnet}"
BUNDLE="$(cd "$(dirname "$0")/../.." && pwd)"
RESOURCES="$BUNDLE/Contents/Resources"
LOG_DIR="$HOME/Library/Logs/AgentsDashboard"
mkdir -p "$LOG_DIR"

# A build published while the app was open waits in app.next. It is moved
# there whole with its marker, so a folder without one is not a staged build
# and is left alone. If a move fails the old build is put back and started,
# and why goes in the log: nothing else would ever say.
log() { echo "$(date '+%Y-%m-%d %H:%M:%S') launcher: $*" >>"$LOG_DIR/app.log"; }
if [[ -f "$RESOURCES/app.next/.staged" ]]; then
    staged_commit="$(grep '^commit=' "$RESOURCES/app.next/.staged" 2>/dev/null)"
    rm -rf "$RESOURCES/app.previous" 2>>"$LOG_DIR/app.log"
    if mv "$RESOURCES/app" "$RESOURCES/app.previous" 2>>"$LOG_DIR/app.log"; then
        if mv "$RESOURCES/app.next" "$RESOURCES/app" 2>>"$LOG_DIR/app.log"; then
            rm -f "$RESOURCES/app/.staged"
            rm -rf "$RESOURCES/app.previous"
            codesign --force --deep -s - "$BUNDLE" 2>/dev/null
            log "updated to the staged build ($staged_commit)"
        else
            mv "$RESOURCES/app.previous" "$RESOURCES/app"
            log "could not move the staged build into place; kept the current one"
        fi
    else
        log "could not move the current build aside; the staged one waits"
    fi
fi

APP_DIR="$RESOURCES/app"
cd "$APP_DIR"
exec "$APP_DIR/agents-dashboard" "$@" >>"$LOG_DIR/app.log" 2>&1
LAUNCHER
chmod +x "$bundle/Contents/MacOS/launcher"

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

# Any older staged build is superseded by this one either way.
rm -rf "$next_dir"

if $running; then
    # The marker goes in before the move, and the move is a rename, so the app
    # and the launcher never see a staged build without it or a half-copied one.
    printf 'commit=%s\nsubject=%s\nstaged=%s\n' \
        "$commit" "$(git -C "$repo" log -1 --format=%s)" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
        >"$staging/app/.staged"
    mv "$staging/app" "$next_dir"
    # Not signed here: that would rewrite the running binary. The launcher
    # signs the bundle once it has swapped the build in.
    echo "The app is running, so the build is staged. Click Update available in its status bar, or it applies on the next start."
    exit 0
fi

# Swap the payload in whole, so a failed copy never leaves half an app.
if [[ -d "$app_dir" ]]; then
    mv "$app_dir" "$staging/previous"
fi
mv "$staging/app" "$app_dir"

# Ad hoc signing, as before: enough for Gatekeeper to run a locally built app.
codesign --force --deep -s - "$bundle" 2>/dev/null

echo "Published to $bundle"
