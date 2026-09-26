# Where the agent data comes from

Nothing is installed, no hooks are added, and no settings file of yours is
edited. Claude Code already writes everything the dashboard shows, and the
dashboard only reads it.

## Which agents there are

The agents are the ones the dashboard runs itself over ACP (see
`agent-control.md`). `AgentHost` knows their state as it changes, and hands the
monitor the running ones through `IAgentSessionSource`, which is how they land
in worktrees in the snapshot, count towards "waiting on you", and raise
notifications. Sessions started in a terminal are not listed; they can be picked
up under New agent, Resume.

## The work summary

The row label is Claude's own generated title, read from the tail of the
session's transcript at `<config>/projects/<slug>/<sessionId>.jsonl`, where the
record looks like `{"type":"ai-title","aiTitle":"Estimate Azure hosting costs"}`.
Skills used come from the same file.

A transcript is append-only and can reach tens of megabytes, so it is read
forward from a per-session cursor: the first read scans, every read after it sees
only what was appended. Whole lines only, so a record still being written is
picked up next time rather than parsed half-formed.

The slug is derived from the working directory, but that is Claude's private
convention, so it is used as a fast path only; a miss falls back to scanning the
projects directory. A transcript we cannot find costs a work summary, never an
agent.

## Subagents

Subagents run inside the parent agent's process and have no session of their own.
Claude writes one small file per subagent beside the transcript and removes it
when the subagent finishes, which makes the directory listing an answer to "what
is running right now" rather than a tally of everything that ever ran.

## Notifications

Every other signal the dashboard has terminates inside its own window: the
waiting rail, the counts, the pulsing dot, the window title. None of them reach
you once the window is behind your editor, which is exactly when an agent sitting
on a question costs the most. An OS notification is the only channel that does,
which is also why the rules about raising one are strict:

```mermaid
flowchart TD
  A[an agent is waiting] --> B{was it already waiting<br/>when the app opened?}
  B -->|yes| N[say nothing]
  B -->|no| C{have we already<br/>announced this block?}
  C -->|yes| N
  C -->|no| D[notify once]
  D --> E[it stops waiting]
  E --> F[it may be announced again<br/>next time it blocks]
```

Opening the dashboard onto three blocked agents should show you three blocked
agents, not fire three notifications about a state you are already looking at.
