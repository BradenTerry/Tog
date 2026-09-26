---
name: publish-local
description: Republish the Agents Dashboard desktop app from the main branch into the macOS app bundle on the Desktop (~/Desktop/Agents Dashboard.app), so the installed app runs the latest merged code. Use when the user asks to republish, reinstall, update or publish the app locally, or to put the current version on their Desktop.
---

# Publish the app locally

The installed app is `~/Desktop/Agents Dashboard.app`: a zsh launcher plus a
framework-dependent `dotnet publish` in `Contents/Resources/app`.
`tools/publish-local.sh` builds that payload and swaps it in. This skill makes
sure it publishes the right code.

## Steps

1. **Publish from main, in the main checkout.** Find it with
   `git worktree list --porcelain` (the first `worktree` line). Do not publish
   from a linked worktree: the app would then run from the bundle while its
   source sits in a worktree that may be removed.

2. **Bring main up to date.** In the main checkout, run `git status --short`.
   If anything is uncommitted there, stop and ask the user. Do not stash it and
   do not discard it. Then confirm the branch is `main` and
   `git pull --ff-only`. If the pull cannot fast-forward, stop and say why.

3. **Check that the app is not running.** The script refuses when the bundle's
   app is running, because it replaces its files. Ask the user to quit it. Do
   not kill it yourself.

4. **Publish.** From the main checkout:

   ```
   tools/publish-local.sh
   ```

   Pass a path to publish somewhere other than the Desktop. The script builds
   in Release, copies in the ACP bridge (`src/AgentsDashboard.App/acp`), swaps
   the payload in whole and signs the bundle ad hoc. Extensions live in the
   user's data directory and are not touched.

5. **Report** the commit that was published (the script prints it) and the
   bundle path. Offer to open it with `open "$HOME/Desktop/Agents Dashboard.app"`.

## When it fails

- "The ACP bridge is not installed": run `dotnet build` in the main checkout
  once, which runs `tools/vendor-acp.sh`, then publish again.
- The app opens and closes at once: read `~/Library/Logs/AgentsDashboard/app.log`.
