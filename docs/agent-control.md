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

## What is not built on

A session's registry entry advertises a Unix socket at
`/tmp/cc-socks/<pid>.sock`, and there is a token beside it. That is how Claude's
own cross-session messaging works, and it would allow messaging a running agent
without stopping it. Its handshake is not documented, and the socket does not
answer anything without it. Nothing here is built on it, because a feature that
silently stops working after a Claude update is worse than one with a limit you
can read.
