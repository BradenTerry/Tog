# Syntax colouring

Code is coloured by Monaco, in the editor and in a diff tab alike, since a diff
tab is Monaco's own diff editor (see [review.md](review.md)). Nothing is
coloured on the server.

## VS Code's grammars

Monaco ships hand-written grammars of its own, and for Razor that grammar knows
little of the C# and markup inside a file. So for the languages listed in
`textmate.js`, Monaco's tokenizer is replaced by VS Code's: `vscode-textmate`
running the TextMate grammars VS Code runs, with Oniguruma (as WASM) for the
regular expressions they are written in. Razor's grammar embeds the C#, HTML,
CSS and JavaScript ones, so a `.razor` file reads as it does in VS Code.

- **Vendored, not committed.** `tools/vendor-textmate.mjs` fetches the tokenizer,
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
