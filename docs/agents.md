# Where the agent data comes from

Nothing is installed, no hooks are added, and no settings file of yours is
edited. Claude Code already writes everything Tog shows, and Tog only reads it.

## Which agents there are

The agents are the ones Tog runs itself over ACP (see
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

## The conversation

`ConversationReader` reads the same transcript as a chat. Tool calls between two
things said fold into one "Ran 2 commands, read a file" line. File changes made
with Edit, MultiEdit and Write are pulled out of that fold into a "Files changed"
fold of their own, with a count, and each file in it opens to its diff. The first
diff is made from the call's input, which has no line numbers. The tool's result,
a record or two later, carries a `toolUseResult.structuredPatch` with line
numbers and context, and replaces it. A failed edit is marked failed.

This only sees changes made with the edit tools. A file changed by a shell
command (`sed`, a script) shows as the command, the same as in the terminal.

## Subagents

Subagents run inside the parent agent's process and have no session of their own.
Claude writes one small file per subagent beside the transcript
(`<session>/subagents/agent-<id>.meta.json`, with the Agent call's
`toolUseId`) and its conversation next to it (`agent-<id>.jsonl`, every line
marked `isSidechain`).

Claude used to remove the meta file when the subagent finished, so the listing
was "what is running now". It now keeps it, since a finished subagent can be
resumed, and the listing is every subagent that ever ran. Which are running is
read from the parent transcript instead: a foreground subagent is done when its
Agent call's result arrives, a background one when a `<task-notification>`
naming the call says it stopped. A subagent whose own transcript is written
more than a few seconds after that was resumed, and counts as running again.

Clicking a subagent in the chat's background strip shows its conversation in
the thread in place of the agent's, read-only and following it as it writes:
its first message is the task the agent gave it. It cannot be messaged; it
answers to the agent.

## Background work

A subagent, or a shell command started with `run_in_background`, keeps going
after the turn that started it ends, so an idle agent is not necessarily done.
The chat lists that work in a strip above the message box, and the agent picker
says "2 running in background" under the agent.

Subagents come from the files above. Background commands come from the
transcript (`TranscriptReader`): the Bash call carries `run_in_background: true`,
its result carries a `backgroundTaskId`, and when the command ends a
`<task-notification>` record names the call (`<tool-use-id>`) or the task
(`<task-id>`). The notice arrives as a user message when the agent is idle and
as a queued attachment mid-turn, so it is read from the raw line.

Background work dies with the agent process and nothing records that: no notice
for a command, a leftover file for a subagent. So the monitor ignores anything
that started before the current agent process did (`ProcessStartedAt`), and
nothing is listed for a stopped agent.

## Unread turns

Waiting agents sort to the top because they need an answer. An agent that
finished its turn while you were on another one needs a look instead, and
without a mark it reads the same as one you already read. So the agent list
puts a blue dot on it, as a messaging app marks an unread conversation, and
the title bar counts them under "unread".

```mermaid
flowchart TD
  T[a turn ends, in a reply or an error] --> H[AgentHost records TurnEndedAt<br/>and saves it in agents.json]
  H --> Q{is that agent's chat on screen?<br/>picked, Chat tab in front, panel open}
  Q -->|yes| S[SeenTurnsStore records it as seen]
  Q -->|no| U[unread: blue dot, title bar count]
  U --> O[you open its chat] --> S
```

"On screen" is the chat panel showing that agent's own thread: picked, the
Chat tab in front of an unfolded section, and its panel open. A subagent's
thread standing in for it does not count. `ChatPanel` marks it on every render
in view, so a turn that ends while you watch is never marked at all.

The turn end is the host's, not the transcript's: `AgentHost` stamps it when
the last turn in flight finishes, and `agents.json` keeps it, so a turn that
finished just before the app closed is still unread when it opens again. The
turns seen are in `seen-turns.json` in the app's data folder, per machine and
shared by every window, pruned to the agents still in the list. An agent only
shows as unread once it has stopped working: while it works, its own dot says
more.

Unread is counted alongside the states rather than instead of one: an unread
agent is also idle, stopped or failed, and is counted there too.

## Notifications

Every other signal Tog has terminates inside its own window: the
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

Opening Tog onto three blocked agents should show you three blocked
agents, not fire three notifications about a state you are already looking at.

An agent is waiting when it has a permission prompt or a form of questions
open (see `docs/agent-control.md`). The notification says what it waits for:
the tool call for a permission, the question when there is one, and "N questions
for you" for several. A form from an MCP server is always "An MCP server the
agent uses is asking", never the server's own words.
