# Examples

Extensions to read and copy from. Tog loads none of them unless you add
one, and none is shipped with the app.

- [NodeTests](NodeTests) lists a worktree's `node:test` tests and runs them.
  A worktree view, a service shared by every window, and a process started
  only from a button.

To start your own, use the template (`dotnet new install templates/extension`,
then `dotnet new tog-extension`) or ask an agent; see
[docs/extensions.md](../docs/extensions.md).
