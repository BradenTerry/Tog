# Reviewing an agent's diff

The Changes document is a pull request review over a worktree that has no pull
request: read the diff, comment on lines, submit the lot as one piece of
feedback the agent can act on. The diff opens in the editor, and Source control
in the right panel lists its files and sends the review. The two share one
`ChangesModel` per worktree (see [workbench.md](workbench.md)).

```mermaid
sequenceDiagram
  participant U as You
  participant D as Dashboard
  participant W as Worktree
  participant C as Claude session

  U->>D: comment on BlogService.cs L42-45
  D->>D: hold it in a draft, saved to disk
  U->>D: Submit review
  D->>W: write .agents-dashboard/reviews/review-<time>.md
  D->>U: copy a paste-ready prompt to the clipboard
  D-->>C: best effort, push the prompt into the session
  Note over D,C: the file and the clipboard land first,<br/>and are what the feature rests on
```

## What you can diff against

| Base | What it shows |
| --- | --- |
| **Uncommitted** | Everything not committed: staged, unstaged and untracked. |
| **Whole branch** | The merge base with the repository's default branch, so it reads like the pull request this would become. Working-tree changes are included, because an agent's work is usually a mix of committed and not. |
| **Against a ref** | Any ref you name. |

Untracked files are folded in as all-additions. Git will not diff them, but a
file the agent just created is exactly the kind of change worth reviewing, and
leaving it out would hide the most interesting part of the work.

A repository with no commits yet diffs against the empty tree rather than
failing, so a fresh project is reviewable from the first file.

## Finding a file

The changed files are listed as a tree. A flat list is fine for a handful of
files and useless for a hundred: the paths all share a long prefix, so the part
that tells them apart is the part that gets clipped.

Directory chains with nothing to branch on are drawn as one row
(`src/AgentsDashboard.Core/Testing` rather than three rows each holding one
child), which is what keeps a deep .NET layout from spending most of its width on
indentation. Folders carry the number of changed files under them, files carry
their own additions and deletions and a badge for comments you have left.

Picking a file in Source control opens the Changes document at its diff. In Blazor Server every commentable line is a
handler registered over the circuit, and tens of thousands of them make the page
stop answering clicks, so only the files within about a screen of the viewport
have their lines drawn. `watchDiffWindow` in `app.js` watches the files with an
IntersectionObserver on the document's own scroller and reports, in batches, which are
near and how tall each drawn file measured; every other file is a block that
height, so the scrollbar and the jumps from Source control land where they would with
everything drawn. A file with a comment editor open always stays drawn. The one
cost is the browser's find: it only sees the files that are drawn.

A single file over 3000 diff lines still starts folded behind **Show it**, since
drawing it is a lot at once however little else is on screen. Picking it in the
tree opens it.

The trees themselves draw only their visible rows through `Virtualize`, which
works out which rows those are from the pinned height of `.tree-row`.

## Commenting

Click a line number to comment on that line, or drag down the gutter to cover a
range. The selection highlights as you drag, in either direction. Shift-clicking
another line number extends the range too, for when a drag is awkward. Escape
cancels.

The target is the whole line-number gutter rather than a small glyph, because
picking lines is the primary gesture on this screen and it should be where the
pointer already is.

### Selecting a range

Press on a line number, drag along the gutter, release. The rows light up as you
go, in either direction, and the comment editor opens on the last row of the
range with the range in its heading. Shift-clicking another line number extends
what is already selected, for when a drag is awkward, and Escape cancels.

A comment belongs to one side of the diff, so a drag covers one side: the side
under the pointer when you let go. Rows of the other side inside the drag are
stepped over. Dragging from a removed line down into the added lines that
replaced it therefore comments on the added lines, which is the side you were
pointing at.

That last rule is the fix for a bug worth remembering. The drag used to lock onto
the side of the row it started on and ignore every row of the other side, which
sounds harmless until you notice that a changed block is removed lines followed
by added ones: a drag down the gutter crosses the boundary almost every time. It
stopped extending at the crossing, so a one-line replacement -- a `-` with a `+`
under it, the most common shape in any diff -- selected a single line no matter
how far you dragged, and the feature looked like it did not exist. Walking rows
in the order they are drawn, rather than matching on side, is what makes the
gesture follow the pointer.

### Why the drag is not on the server

A drag is a stream of mousemove events, and sending each one over the Blazor
circuit would make one gesture cost hundreds of round trips. So the drag is
followed entirely in `app.js`: it paints a preview with a class the server never
sets, and calls back into the component once, on release, with the range.

Two details keep the two sides from disagreeing about a row. The preview class is
`picking` and the server's is `in-range`, so neither can clear the other's; and
the preview is removed before the server is told, so the class it renders is the
only one left. A mouse click on the gutter is also stopped from reaching the
server's own click handler, since the drag has already dealt with it -- a keyboard
activation reports a click detail of zero and is let through, which is what keeps
the gutter usable without a mouse.

Comments collect into a draft that is saved as you go, so navigating away or
restarting does not lose it.

## Browsing the rest

The **Files** tab lists the whole worktree in the same tree, with a filter box,
and shows any file with line numbers. The listing comes from git rather than from
walking the directory, so `.gitignore` is honoured for free: a worktree's `bin`,
`obj` and `node_modules` are not files you want to browse, and no hand-written
skip list would keep up with a project's own ignore rules. Untracked files are
marked, and a file path from the UI is checked against the worktree root before
anything is read.

The draft is kept outside the worktree, in `~/.agents-dashboard/drafts`. An
unsubmitted review is not part of the work: in the worktree it would show up as
an untracked file in the very diff it is about.

## What gets written

```markdown
# Review 2026-09-05 14:22

Overall: the retry logic looks right, two things to fix.

## src/Soar.Web/Services/BlogService.cs

- **L42-45**: this swallows the cancellation. Let OperationCanceledException through.

  > +        catch (Exception)
  > +        {
  > +            return Array.Empty<Post>();
  > +        }

- **L88**: off by one, Take(count) should be Take(count + 1).
```

Grouped by file, ordered by line, each entry leading with its range, with the
lines it refers to quoted underneath. The agent reads a list of located changes
rather than a paragraph it has to map back onto the code.

## Getting it to the agent

Three attempts, in this order, and the order is the design:

1. **The markdown file** is written into the worktree, first and
   unconditionally. It is the artifact that survives everything else failing.
2. **The clipboard** gets a short prompt pointing at that file, so you can paste
   it into the agent's terminal yourself.
3. **The agent** is handed the prompt as its next message over ACP: the one
   picked under **Send to** (the agent you are viewing, by default), or a new
   agent started on the review in that worktree.

Only the third can fail, and its failure changes nothing: the review is already
written and already on the clipboard.
