# Following a .NET test run

This is the Tests extension, `extensions/DotnetTests`. It is not part of the app
and not shipped with it: link the folder in Settings, Extensions. See
[extensions.md](extensions.md).

The problem this solves: an agent runs `dotnet test`, and for the next minute you
have no idea whether it is building, running, passing, or already finished and
scrolled away. The dashboard answers that continuously, for runs you start and
runs an agent starts.

## Where the live data comes from

Since **Microsoft.Testing.Platform 2.3.0** the TRX report is streamed to disk as
the run progresses rather than written at the end. That turns the report file
into a live feed, with no wrapper process and no terminal scraping.

It does not cover the beginning of a run, though. Restore and build happen before
a single result exists, and on a real solution that is most of the wall clock. So
there is a second signal: the process table.

```mermaid
flowchart TD
  subgraph signals["Two signals, because neither is enough"]
    P["process table<br/>dotnet test / testhost / vstest.console"]
    F["**/TestResults/**/*.trx"]
  end

  P -->|"a run exists, no results yet"| B["show it as building"]
  F -->|"results, as they land"| R["counts, progress, failures"]
  B --> RUN[one run on screen]
  R --> RUN
  RUN --> E{"closing TestRun tag?"}
  E -->|yes| DONE["passed or failed"]
  E -->|"no, and nothing for 90s<br/>with no process"| STOP["stopped"]
```

The placeholder run created from the process is dropped the moment a report for
that worktree appears, so you see one run, not two.

## Reading a file that is being written

The report is not a document for most of a run: there is no `</TestRun>`, and the
last element is usually half typed. So it is never handed to an XML parser whole.

```mermaid
flowchart LR
  A["read from the byte offset<br/>we stopped at last time"] --> B["scan forward for complete<br/>UnitTestResult elements"]
  B --> C["parse each one on its own"]
  C --> D["merge by testId"]
  D --> E["advance the offset to the end<br/>of the last complete element"]
  E -->|"next poll"| A
```

Three details that are easy to get wrong:

- **Nesting.** A data-driven test writes its cases as `UnitTestResult` elements
  inside an `InnerResults` block of another one. Matching the first closing tag
  truncates the outer element and produces unparseable XML, so the scanner tracks
  depth.
- **Sharing.** The file is opened with `FileShare.ReadWrite` because the writer
  still has it open.
- **Movement.** The offset stops just past the last complete element, so there
  are always a few bytes of trailing whitespace to re-read. Treating that as
  activity would keep a dead run looking alive forever, so only new results, a
  new total or the closing tag count.

The total comes from the run's own summary counters, which are written at the
end. Before that the total is unknown, and the UI shows a count rather than
inventing a denominator for a percentage.

## Two clocks

A run carries two independent notions of time and they must not be mixed.

| Question | Answered by |
| --- | --- |
| How long did the run take? | The report's `Times start` and the file's last write. A run that finished before the dashboard opened reports its real duration, not the time since. |
| Has this run stalled? | The dashboard's own clock. That is the one that says how long *we* have been waiting. |

## Making an agent's runs visible at all

Neither runner writes a report unless it is asked to, and an agent typing
`dotnet test` will not ask. Without that, agent-initiated runs are invisible.

The Tests tab detects this and offers to write one file at the repository root:

```xml
<!-- Directory.Build.targets -->
<Project>
  <PropertyGroup Condition="'$(IsTestProject)' == 'true'">
    <VSTestLogger Condition="'$(VSTestLogger)' == ''">trx%3BLogFileName=$(MSBuildProjectName).trx</VSTestLogger>
    <TestingPlatformCommandLineArguments>$(TestingPlatformCommandLineArguments) --report-trx</TestingPlatformCommandLineArguments>
  </PropertyGroup>
</Project>
```

`Directory.Build.targets`, not `.props`: `IsTestProject` is set by the test SDK
during its own import, which happens after props and before targets. A condition
on it in props is evaluated before anything has set it and silently matches
nothing.

This edits your repository, so it is always offered and never done on its own.
The tab shows exactly what would be written, and adds the block to an existing
file rather than replacing it.

## What each runner gives you

| Runner | Detected by | What you see |
| --- | --- | --- |
| Microsoft.Testing.Platform | `UseMicrosoftTestingPlatformRunner`, `EnableMSTestRunner`, `EnableNUnitRunner`, `MSTest.Sdk`, or a `Microsoft.Testing.*` package reference, in the project or in `Directory.Build.*` | Live counts, live failures, live progress |
| VSTest | `Microsoft.NET.Test.Sdk` with none of the above | The run appears as soon as it starts, and the results all arrive at the end |

The tab says which it is rather than leaving a still progress bar unexplained.

Detection reads project files as text rather than evaluating them with MSBuild.
Evaluation would be exact but costs a design-time build per project, which is far
too slow for something wanted on open, so the tab states this as an observation.

## Belt and braces

The file watcher is the fast path and usually the only one that fires, but it is
allowed to drop events: its buffer can overflow under a busy build, and on
network and container-mounted filesystems it may see nothing at all. A run the
dashboard silently failed to notice is the one failure mode that makes the whole
feature untrustworthy, so a cheap walk of the watched worktrees backs it up every
five seconds.

## The explorer

Below the latest run, the tab lists every test the worktree's recent runs have
reported, grouped by namespace, class and method, with a name search and a
status filter. There is no discovery step: the extension follows reports rather
than running anything of its own, so the tree is the newest result per test
across the runs it holds. A test that has never run is not in it until
something runs it, and a partial run only replaces the tests it touched.

Clear empties the tree without touching the reports: they are the user's files.
The tracker remembers each cleared report's length and last write and skips it
while those match, so the periodic scan does not read it back, and a new run
writing the same file shows up as usual. Reload forgets the runs and the cleared
list, re-probes the test projects, and reads every report in the worktree again.

```mermaid
flowchart LR
    runs["Runs, newest first"] --> latest["Newest result per test"]
    latest --> filter["Status and name filter"]
    filter --> tree["Namespace / class / method tree"]
    tree --> picked["Picked rows"]
    picked --> filters["One VSTest filter per project"]
    filters --> seq["dotnet test --project ... --filter ..., one after another"]
```

Rows are picked as in the file trees: a click picks one, Cmd or Ctrl-click adds
or removes, Shift-click takes the range as drawn. Folding is the chevron or a
double click, so picking a namespace does not fold it away.

Running picked rows turns them into a filter in VSTest syntax, the one form every
runner takes, xunit.v3 on the testing platform included (it also has its own
`--filter-class` family, but will not mix it with this). A namespace or class
matches by prefix, a method by exact name, which also takes in every case of a
theory; a single case cannot be named, so picking one runs its method. With a
filter on, a group names only the methods of it on screen instead, so what you
see is what runs.

A run is started per project, one after another, rather than one filtered run
across the solution. A filter that matches nothing in a project still builds and
runs that project and writes an empty report, which would show as a run of its
own, and parallel runs of projects that share a dependency fight over its `obj`.
