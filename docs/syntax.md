# Syntax colouring

Code in the diff is coloured by Monaco's tokenizer, the same one the editor
and VS Code use, so a file looks the same in the diff and the editor and every language Monaco
ships is covered. The diff itself is still rendered on the server; Monaco only
answers which class each run of a line gets.

## Monaco as a tokenizer, not a renderer

`agentsEditor.colourLines` in `monaco.js` takes a file's path and its lines as
blocks, tokenizes each block in a throwaway model, and returns each line's run
ends and Monaco's `mtkN` classes. Those classes are global rules the theme
service injects, so they colour a plain span outside any editor. `mtk1` is the
theme's plain foreground and is dropped, so uncoloured code keeps the page's own
text colour.

The Changes document asks once per file, after the render that first draws it, and
shows that file plain until the answer arrives. One call per file keeps each
answer well inside the circuit's message size. Two details are easy to break:

- **Languages load lazily.** Tokenizing a model straight after creating it reads
  every line as plain, because the grammar has not arrived. `colourLines` awaits
  `monaco.editor.colorize` on an empty string first, which waits for it.
- **A stray `\r` inside a line is a line break to Monaco.** It would shift every
  later answer by one line, so it is replaced with a space of the same length.

The classes index the theme's colour map, which differs between the light and
dark themes. A diff coloured before the OS switches theme keeps the old indices
until it is reloaded.

## The server highlighter is the fallback

The server-side tokenizer below still exists. It colours a file when Monaco has
no language for it (`.gitignore`, `.editorconfig`) and every file once Monaco has
failed to load. What follows describes it; the two-pass reading of a hunk
applies to both, since Monaco is handed the same two blocks per hunk.

## Why the server highlighter does not rewrite the DOM

Blazor owns the DOM. A client-side highlighter rewrites the rendered lines, and
the next render puts them back; re-running it after every render turns adding one
review comment into a full re-highlight of the page. Emitting the coloured runs
as part of the render is both simpler and correct by construction, and there is
no library to vendor and nothing to load.

## What it does

One configurable tokenizer covers the C-family and everything shaped like it,
picked by file extension:

| Shape | Extensions |
| --- | --- |
| C-family | `.cs` `.fs` `.js` `.ts` `.jsx` `.tsx` `.java` `.kt` `.go` `.rs` `.c` `.cpp` `.swift` and friends |
| Hash comments, no blocks | `.py` `.sh` `.bash` `.yml` `.toml` `.ini` `.gitignore` `.ps1` |
| Keys and values | `.json` `.yml` `.toml`, where a name before a colon is coloured apart from the value |
| Markup | `.xml` `.html` `.razor` `.csproj` `.props` `.targets` `.slnx` `.config` `.runsettings` `.xaml` |
| Markdown | `.md` |

Markup and Markdown get tokenizers of their own, because they are not shaped like
C at all. Anything else is left plain, which costs nothing.

Seven colours: comment, string, number, keyword, type, meta (a directive, an
attribute name, an object key) and operator. That is enough for code to read as
code; it is not enough to parse a language properly, and it does not try.

## Line at a time, with state

Both views render one line at a time, so the highlighter does too. The little
that carries over between lines is threaded through explicitly:

```mermaid
flowchart LR
  A[line] --> B[tokenize]
  B --> C[runs for this line]
  B --> D{unterminated?}
  D -->|"/* with no */"| E[block comment]
  D -->|"@\" with no close"| F[multiline string]
  D -->|"``` opened"| G[markdown fence]
  E --> H[state for the next line]
  F --> H
  G --> H
  H --> B
```

A file is one sequence, so the browser feeds it straight through. A diff is not:
it interleaves two versions of the file, and reading them as one stream would let
an added line's opening quote be closed by a removed line's, colouring the rest of
the hunk as a string. So each hunk is read twice, once down the new side (context
and additions) and once down the old (context and deletions), and each line takes
its own pass's result. State resets at each hunk, because a hunk begins after a
gap in the file and is not a continuation of what came before it.

Two deliberate limits fall out of this:

- An **unterminated ordinary string** does not continue onto the next line. A lone
  quote is far more often one inside prose than a string that runs on, and
  swallowing the rest of the file is a much worse failure than leaving one line
  uncoloured. Verbatim, raw and template strings do continue, because those
  genuinely span lines.
- A hunk that **begins inside** a block comment shows its first lines as code.
  There is nothing above them to say otherwise.

## Cost

A file is coloured once, when it is opened or first drawn, and the runs are kept
until the diff is reloaded. Nothing is recomputed when you add a comment or fold
a directory.
