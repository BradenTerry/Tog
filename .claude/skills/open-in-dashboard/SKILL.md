---
name: open-in-dashboard
description: Show a file to the user inside the running Agents Dashboard app, as a tab in its editor. Images (png, jpeg, gif, webp, svg) open as pictures, text files as text. Use whenever you have made or found something the user should look at, above all screenshots, and when the user asks to open, show or view a file "in the dashboard", "in the app" or "in my dashboard view".
---

# Open a file in the dashboard

If you have the `mcp__agents-dashboard__dashboard_open_file` tool (you do when
the dashboard runs you), call it with the path, and a `line` if you like,
instead of everything below. It tells you whether the file opened. The folder
below is for agents running in a terminal.

The dashboard watches `~/.agents-dashboard/open/` for requests. Each one is a
small JSON file naming an absolute path. Write it under a temporary name and
rename it to `.json`, so the app never reads a half-written request:

```sh
open_in_dashboard() {
  local dir=~/.agents-dashboard/open f
  mkdir -p "$dir"
  for p in "$@"; do
    f="$dir/$(uuidgen)"
    python3 -c 'import json,os,sys; print(json.dumps({"path": os.path.abspath(sys.argv[1])}))' "$p" > "$f.tmp" \
      && mv "$f.tmp" "$f.json"
  done
}
open_in_dashboard /tmp/shots/before.png /tmp/shots/after.png
```

Add `"line": N` to land on a line of a text file.

## What happens

- Every open dashboard window opens the file as a tab among the documents of
  the agent it is looking at.
- A file inside that agent's worktree opens as the worktree's own, editable.
  Anything else opens read-only.
- Asking again for a file already open brings it forward and reads it again,
  so after you retake a screenshot, send the same path again.

## Checking it worked

The app deletes a request once it has read it. If the file is still in the
folder a few seconds later, the dashboard is not running, or is a build from
before this feature: tell the user and give them the paths instead. A request
older than two minutes is dropped, so one left behind will not pop up later.

Do not open the image yourself in another viewer as well unless the user asks.
