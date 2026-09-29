---
name: publish-local
description: Republish the Tog desktop app from the main branch into the macOS app bundle on the Desktop (~/Desktop/Tog.app), so the installed app runs the latest merged code. Works with the app open, which then offers Update available. Use when the user asks to republish, reinstall, update or publish the app locally, or to put the current version on their Desktop.
---

# Publish the app locally

The installed app is `~/Desktop/Tog.app`: a zsh launcher and an
Info.plist, nothing else. The builds live outside the bundle, in
`~/Library/Application Support/Tog/Tog/builds`, with
`current` and `next` naming the one to run and one waiting. macOS will not let
the app rewrite its own bundle, so nothing is swapped inside it.
`tools/publish-local.sh` adds a build and names it. This skill makes sure it
publishes the right code.

## Steps

1. **Publish from main, in the main checkout.** Find it with
   `git worktree list --porcelain` (the first `worktree` line). Do not publish
   from a linked worktree: the app would then run from the bundle while its
   source sits in a worktree that may be removed.

2. **Bring main up to date.** In the main checkout, run `git status --short`.
   If anything is uncommitted there, stop and ask the user. Do not stash it and
   do not discard it. Then confirm the branch is `main` and
   `git pull --ff-only`. If the pull cannot fast-forward, stop and say why.

3. **The app may stay open.** If the app is running, the script never touches
   the build it runs from: it names the new one in `next`, and the app shows
   Update available in its title bar within a few seconds. Clicking it restarts
   into the new build; otherwise the launcher applies it on the next start.
   Never kill the app yourself.

4. **Publish.** From the main checkout:

   ```
   tools/publish-local.sh
   ```

   Pass a path to publish somewhere other than the Desktop. The script builds
   in Release, copies in the ACP bridge (`src/Tog.App/acp`), swaps
   the build into its own folder, makes it current (or stages it, above) and
   signs the bundle ad hoc. It rewrites the bundle's launcher every time, since
   the launcher does the switch. Builds over a day old are cleared at launch.
   Extensions live in the user's data directory and are not touched.

5. **Report** the commit that was published (the script prints it), the
   bundle path, and whether it was installed or staged. If staged, tell the
   user to click Update available. A copy from before builds moved out of the
   bundle says so: it has to be quit and reopened once. Otherwise offer
   to open it with `open "$HOME/Desktop/Tog.app"`.

## When it fails

- "The ACP bridge is not installed": run `dotnet build` in the main checkout
  once, which runs `tools/vendor-acp.mjs`, then publish again.
- The app opens and closes at once: read `~/Library/Logs/Tog/app.log`.
