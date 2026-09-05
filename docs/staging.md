# Staging from the Changes tab

Reading a diff and deciding what to commit are the same sitting, so the Changes
tab does both. Each file's header says where its changes are and offers to move
them; the toolbar stages or unstages everything.

| Badge | Means |
| --- | --- |
| **staged** | The index has it and the working tree has nothing further |
| **partly staged** | Staged, and then changed again since |
| **unstaged** | Changed, and not staged |
| **new** | Not tracked yet |

A staged file is marked with a dot in the file tree, filled when it is fully
staged and hollow when only part of it is, so the list can be scanned without
opening each file.

## Where the two states come from

`git status --porcelain=v2` reports a two-character field per file, and the two
characters answer two different questions: the first is the index against HEAD,
the second is the working tree against the index. A file can be both, which is
what **partly staged** means and why staging offers to "stage the rest".

## Unstaging without a HEAD

`git restore --staged` restores the index from HEAD, and a repository with no
commits does not have one. Taking the path out of the index reaches the same
state, so that is what happens there instead. A fresh project is worth handling:
it is exactly when everything is new and staging matters most.

## The diff does not move when you stage

The Uncommitted view is taken against HEAD, so it shows staged and unstaged work
together. Moving a file between them changes which badge it carries and not a
single line of its diff. Only the staging state is re-read after an action, which
is one `git status` rather than a whole diff.
