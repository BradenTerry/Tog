#!/usr/bin/env bash
# Publishes this checkout of the app as a macOS app, by default
# ~/Desktop/Agents Dashboard.app.
#
# The bundle holds only a zsh launcher and an Info.plist. The builds live
# outside it, in ~/Library/Application Support/AgentsDashboard/<bundle name>:
# one folder per build under builds/, and two one-line files naming the build
# to run (current) and one waiting (next). macOS will not let an app signed
# only ad hoc rewrite its own bundle once Finder has launched it ("Operation
# not permitted"), so a build swapped inside the bundle could be installed from
# a terminal but never applied by the app itself.
#
# The launcher gives the app the PATH a terminal would have, because Finder
# starts apps with a bare one and the app runs node, git, claude and dotnet.
# The ACP bridge in src/AgentsDashboard.App/acp is not part of the publish
# output (it is not a static asset), so it is copied in. Extensions live in the
# user's data directory and are left alone.
#
# A new build is never written over a running one. With the app closed it
# becomes current. With it open it is named in next: the app offers Update
# available in its title bar, and the launcher makes it current on the next
# start, however the app is started.
#
# Usage: tools/publish-local.sh [path/to/Agents Dashboard.app]
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
bundle="${1:-$HOME/Desktop/Agents Dashboard.app}"
# Real paths (pwd -P): ps reports a running app by its real path, so a bundle
# reached through a link (/tmp is /private/tmp on macOS) would otherwise never
# match, and a running copy would look stopped.
bundle="$(mkdir -p "$bundle" && cd "$bundle" && pwd -P)"
name="$(basename "$bundle" .app)"
payload="$HOME/Library/Application Support/AgentsDashboard/$name"
payload="$(mkdir -p "$payload" && cd "$payload" && pwd -P)"

# Before builds lived outside the bundle they were in Contents/Resources/app.
legacy="$bundle/Contents/Resources/app"

# ps rather than pgrep: pgrep can come back empty for every process in a
# sandboxed shell. The executables are matched rather than whole command
# lines, so the grep's own command line (which holds the path) cannot match.
executables="$(ps -axo comm=)"
running=false
if grep -qF "$payload/builds/" <<<"$executables"; then
    running=true
fi
legacy_running=false
if grep -qxF "$legacy/agents-dashboard" <<<"$executables"; then
    legacy_running=true
    running=true
fi

commit="$(git -C "$repo" rev-parse --short HEAD)"
id="$(date +%Y%m%d-%H%M%S)-$commit"
echo "Publishing $(git -C "$repo" rev-parse --abbrev-ref HEAD) at $commit"

acp="$repo/src/AgentsDashboard.App/acp"
if [[ ! -f "$acp/VERSION" ]]; then
    echo "The ACP bridge is not installed in $acp. Build once (it runs tools/vendor-acp.sh), then publish again." >&2
    exit 1
fi

mkdir -p "$payload/builds"
staging="$payload/builds/.incoming-$id"
trap 'rm -rf "$staging"' EXIT

dotnet publish "$repo/src/AgentsDashboard.App" -c Release -o "$staging" --nologo -v quiet

# The publish output already has acp/ with the package json files in it, and
# cp -R into an existing directory nests the copy as acp/acp, so replace it.
rm -rf "$staging/acp"
cp -R "$acp" "$staging/acp"

printf 'commit=%s\nsubject=%s\nstaged=%s\n' \
    "$commit" "$(git -C "$repo" log -1 --format=%s)" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
    >"$staging/build-info"

# A rename within one folder: the build appears complete or not at all.
mv "$staging" "$payload/builds/$id"

# The launcher is rewritten every time, since it carries the switch below.
# Info.plist is only written once.
mkdir -p "$bundle/Contents/MacOS"
cat >"$bundle/Contents/MacOS/launcher" <<'LAUNCHER'
#!/bin/zsh
# Finder launches apps with a bare PATH, so give it the one node, git, claude and dotnet live on.
export PATH="$HOME/.local/bin:/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin"
export DOTNET_ROOT="${DOTNET_ROOT:-/usr/local/share/dotnet}"
BUNDLE="$(cd "$(dirname "$0")/../.." && pwd)"
PAYLOAD="$HOME/Library/Application Support/AgentsDashboard/$(basename "$BUNDLE" .app)"
LOG_DIR="$HOME/Library/Logs/AgentsDashboard"
mkdir -p "$LOG_DIR"
log() { echo "$(date '+%Y-%m-%d %H:%M:%S') launcher: $*" >>"$LOG_DIR/app.log"; }

# A build published while the app was open is named in next. Nothing runs
# from it yet, so it becomes current here, before the app starts: a rename of
# one small file, outside the bundle, which macOS does not guard.
if [[ -f "$PAYLOAD/next" ]]; then
    if mv -f "$PAYLOAD/next" "$PAYLOAD/current" 2>>"$LOG_DIR/app.log"; then
        log "updated to $(<"$PAYLOAD/current")"
    else
        log "could not apply the staged build; starting the current one"
    fi
fi

BUILD="$(<"$PAYLOAD/current" 2>/dev/null)"
APP_DIR="$PAYLOAD/builds/$BUILD"
if [[ -z "$BUILD" || ! -x "$APP_DIR/agents-dashboard" ]]; then
    log "no build to start in $PAYLOAD; publish again"
    exit 1
fi

# Builds left from earlier updates, once they are a day old: never the one
# starting, and never one a publish may have just written.
find "$PAYLOAD/builds" -mindepth 1 -maxdepth 1 -type d ! -name "$BUILD" -mtime +0 -exec rm -rf {} + 2>/dev/null

# The app finds its bundle and its builds through these, to offer updates.
export AGENTS_DASHBOARD_BUNDLE="$BUNDLE" AGENTS_DASHBOARD_PAYLOAD="$PAYLOAD"
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

# One-line files written whole and renamed into place, so the launcher and
# the app never read half of one.
name_build() { printf '%s\n' "$id" >"$payload/$1.tmp" && mv -f "$payload/$1.tmp" "$payload/$1"; }

if $running; then
    name_build next
    if $legacy_running; then
        # A copy from before this layout is open. It looks for its own marker,
        # so it only offers the update if one was staged for it; otherwise quit
        # and reopen it once. Its files are left alone while it runs.
        echo "An older copy of the app is open. Quit and reopen it once to move to the new build."
    else
        echo "The app is running, so the build is staged. Click Update available in its title bar, or it applies on the next start."
    fi
else
    name_build current
    rm -f "$payload/next"
    # The builds of the old layout, now that nothing runs from them.
    rm -rf "$legacy" "$bundle/Contents/Resources/app.next" "$bundle/Contents/Resources/app.previous"
    echo "Published to $bundle"
fi

# Ad hoc signing: enough for Gatekeeper to run a locally built app. The bundle
# holds only the launcher now, so this never touches a running build.
if ! $legacy_running; then
    codesign --force --deep -s - "$bundle" 2>/dev/null || true
fi
