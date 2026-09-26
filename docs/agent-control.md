# Starting, stopping and talking to agents

The Agents tab manages background agents in a worktree: start one, hand it a
message, stop it, remove it. Everything goes through the Claude CLI's own
commands.

```mermaid
stateDiagram-v2
    [*] --> Idle: claude --bg
    [*] --> Working: claude --bg "prompt"
    Working --> Idle: finishes its turn
    Idle --> Stopped: claude stop
    Working --> Stopped: claude stop
    Stopped --> Working: claude --bg --resume &lt;session id&gt; "message"
    Stopped --> [*]: claude rm
```

## Two kinds of agent

| | Started by | What the dashboard can do |
| --- | --- | --- |
| **Background** | The dashboard, with `claude --bg` | Start, message, stop, remove |
| **Interactive** | You, in a terminal | Watch only. It belongs to its terminal, and the clipboard is how a review reaches it. |

A background agent started with no prompt sits idle and costs nothing until you
send it one, so "start an agent here" is a cheap thing to do.

## Chat

Talking to an agent happens in its **Chat** tab, laid out like a messaging app:
every agent in the sidebar, the ones waiting on you first, and the conversation
on the right with the box to type in always under it. Enter sends, Shift+Enter
starts a new line. Every other place an agent appears (a worktree's Agents tab,
the Needs you rail) links there rather than carrying a composer of its
own.

The conversation is read from the session's transcript by `ConversationReader`:
what you typed, what the agent said, background task notices, and its tool calls
folded into one line per run ("Ran 3 commands, read a file", expandable).
Thinking, tool output and subagent traffic are left out. It follows the open
conversation once a second, reading only what was appended.

A message you send shows at once, marked as on its way, and stays marked until
the transcript records it, so a slow stop-and-resume never looks like nothing
happened. A send that fails keeps the text, with the reason and a retry.

An agent started in a terminal cannot be typed into. Its chat still shows the
conversation, and the send button copies the message for you to paste there.

## Sending a message stops the agent first

This is the one part with a real constraint behind it, and it is worth knowing.

`--resume` on a session that is **currently running** does not continue it. It
starts a *copy* of the conversation under a new id, prints a note saying so, and
exits successfully. That looks exactly like it worked, and leaves you with two
agents where you wanted one.

So sending a message stops the agent first, waits for it to actually be gone, and
only then resumes it with the message. Two details make that reliable:

- **The stop is waited on, not assumed.** `claude stop` returns before the process
  has finished exiting, and resuming a session the CLI still thinks is live
  produces exactly the copy described above.
- **Liveness is presence in `claude agents --json` without `--all`.** Nothing else
  works: a stopped session keeps its entry under `--all`, and a running session
  that has not transitioned yet reports no status and, briefly, no pid. Judging by
  either reads a live agent as stopped, and the resume then clones it.
- **Both output streams are read.** The CLI reports what it did on stdout but puts
  its notes on stderr, and "this started a copy of that conversation" is a note.
  Reading only stdout means the one line that says the send went wrong is thrown
  away.

The full session id is used, not the short one. Given the short id the CLI also
starts a copy, and says so.

For an idle agent stopping costs nothing. For a working one it is an
interruption, and the composer says so before you send.

## Parked agents

A stopped background agent keeps its conversation but has no process, so it is
absent from Claude's session registry, which is where the rest of the dashboard
gets its agents from. The CLI is the only thing that still remembers it.

The Agents tab therefore lists them separately, under **Parked here**, read from
`claude agents --json --all` and filtered to the ones whose working directory is
in this worktree. Forgetting an agent you parked would make parking one a
mistake.

Which agents count as parked is worked out on each render rather than when the
list was fetched: an agent started a moment ago is in the CLI's list before it
reaches the registry, and would otherwise appear in both places at once.

## More controls

Beyond start, message, stop and remove:

- **Logs**: `claude logs <id>` shows a background session's recent output, on a
  refresh button rather than a live tail. It only works while the session's
  daemon is alive, and says so in the CLI's own words when it is not.
- **Attach**: `claude attach <id>` is the way to actually sit in a session. It
  wants a terminal, so the dashboard copies the line rather than running it.
- **Respawn**: `claude respawn <id>` restarts a failed session, or one left on an
  old CLI binary, keeping its conversation.
- **Start defaults**: the model, effort and permission mode a new agent starts
  with are set under **Settings**.

## What is not built on

A session's registry entry advertises a Unix socket at
`/tmp/cc-socks/<pid>.sock`, and there is a token beside it. That is how Claude's
own cross-session messaging works, and it would allow messaging a running agent
without stopping it. Its handshake is not documented, and the socket does not
answer anything without it. Nothing here is built on it, because a feature that
silently stops working after a Claude update is worse than one with a limit you
can read.
