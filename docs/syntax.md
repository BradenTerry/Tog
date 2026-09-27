# Syntax colouring

Code in the diff is coloured by the same tokenizer as the editor, so a file
looks the same in both. The diff itself is still rendered on the server; Monaco
only answers which class each run of a line gets.

## VS Code's grammars

Monaco ships hand-written grammars of its own, and for Razor that grammar knows
little of the C# and markup inside a file. So for the languages listed in
`textmate.js`, Monaco's tokenizer is replaced by VS Code's: `vscode-textmate`
running the TextMate grammars VS Code runs, with Oniguruma (as WASM) for the
regular expressions they are written in. Razor's grammar embeds the C#, HTML,
CSS and JavaScript ones, so a `.razor` file reads as it does in VS Code.

- **Vendored, not committed.** `tools/vendor-textmate.sh` fetches the tokenizer,
  the regex engine and the `tm-grammars` collection (about 260 grammars, 13 MB)
  into `wwwroot/textmate` on the first build, with an index of grammar by scope
  name. Without them the editor keeps Monaco's grammars and still colours.
- **Loaded through Monaco's AMD loader.** Both scripts are UMD builds. With
  Monaco's loader on the page they register as anonymous AMD modules, so a plain
  script tag would trip over the anonymous `define`; `textmate.js` loads them
  with `require` instead.
- **Lazy.** The tokenizer loads with Monaco. A grammar, and each one it embeds,
  is fetched the first time a file in its language needs colouring, through a
  `registerTokensProviderFactory` per language, which also replaces Monaco's own.
- **Scopes to colours.** A token's most specific scope (`entity.name.tag.html`)
  is its Monaco token, and the themes in `monaco.js` carry Dark+ and Light+ rules
  for the scope names. Monaco matches a rule by dotted prefix, so one rule for
  `entity.name.tag` covers every language's tags.
- **C# can get both.** The grammar colours C# as you type, and with the C#
  extension loaded and Load pressed, Roslyn's semantic tokens then tell types,
  methods and locals apart on top of it (see
  [code-intelligence.md](code-intelligence.md)), which is how VS Code does it.

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

### Slices and caps

Tokenizing runs on the WebView's main thread, the one that handles input and
paints the page, and a file of thousands of lines through a TextMate grammar
takes seconds. So `colourLines` tokenizes one line at a time and yields to the
event loop (through a `MessageChannel`, which is not clamped like
`setTimeout(0)`) whenever about 10 ms of work has gone by. The page keeps
answering while a big file colours. A few limits keep any one file from costing
too much:

- **Long lines are left plain.** A line over 4000 characters goes into the
  model empty, so the lines around it keep their numbers, and comes back with no
  runs. Minified files have lines of megabytes.
- **A tighter per-line limit.** The editor lets the TextMate tokenizer spend
  500 ms on a line. `agentsTextmate.withTimeLimit` lowers that to 20 ms for the
  diff's synchronous slices only; a line that hits it finishes on the next
  line's state.
- **The reply is capped at about 1 MB.** The answer goes up the circuit, and a
  message over `MaximumReceiveMessageSize` (4 MB) drops the circuit without an
  error. Past the estimate the remaining lines get no runs and stay plain; the
  server reads a short array as exactly that.
- **A newer call wins.** A second call for the same path, as happens when an
  agent edits the file mid-colouring, makes the older one give up at its next
  yield. It answers `null` for every block, which the server treats as Monaco
  having no language, and the newer answer replaces it.

The classes index the theme's colour map, which differs between the light and
dark themes. A diff coloured before the theme switches between a dark and a
light one keeps the old indices until it is reopened.

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
