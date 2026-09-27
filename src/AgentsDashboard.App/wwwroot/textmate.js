// VS Code's colouring for the editor: its TextMate tokenizer running the same
// grammars VS Code runs, in place of Monaco's smaller built-in ones. Razor is
// the reason. Monaco's own Razor grammar knows little of what sits inside it,
// where the real one hands C# to the C# grammar and markup to the HTML grammar,
// and those to CSS and JavaScript in turn.
//
// Everything here is lazy. Nothing is fetched until Monaco itself loads, and a
// grammar is fetched only when a file in its language is opened (or a diff of
// one is coloured). If the files were never vendored, registration finds no
// index and Monaco's own grammars stay in place, so the editor still colours.

(() => {
    // Monaco's language id to the grammar's name in tm-grammars. Only languages
    // Monaco already knows are listed: it is Monaco that maps a file extension to
    // a language, and this only changes how that language is coloured.
    const grammarFor = {
        razor: 'razor',
        csharp: 'csharp',
        fsharp: 'fsharp',
        html: 'html',
        css: 'css',
        scss: 'scss',
        less: 'less',
        javascript: 'javascript',
        typescript: 'typescript',
        json: 'json',
        xml: 'xml',
        yaml: 'yaml',
        markdown: 'markdown',
        shell: 'shellscript',
        powershell: 'powershell',
        bat: 'bat',
        python: 'python',
        sql: 'sql',
        dockerfile: 'docker',
        go: 'go',
        rust: 'rust',
        java: 'java',
        kotlin: 'kotlin',
        swift: 'swift',
        cpp: 'cpp',
        ruby: 'ruby',
        php: 'php',
        lua: 'lua',
        ini: 'ini',
        graphql: 'graphql',
    };

    // A line longer than this is left to finish on the next line's state rather
    // than hold up the editor: minified files have lines of megabytes.
    const editorTimeLimitMs = 500;
    let timeLimitMs = editorTimeLimitMs;

    function asset(path) {
        return new URL(path, document.baseURI).toString();
    }

    let engine = null;

    // The tokenizer, the regex engine and the index of grammars, loaded once.
    // Both scripts are UMD builds, and with Monaco's AMD loader on the page they
    // register as anonymous modules, so they are loaded through it rather than
    // with script tags, which would trip over the anonymous define.
    function load() {
        if (engine) {
            return engine;
        }

        engine = (async () => {
            const index = await fetch(asset('textmate/index.json')).then((r) => {
                if (!r.ok) {
                    throw new Error('No TextMate grammars vendored.');
                }

                return r.json();
            });

            const [tm, onig] = await new Promise((resolve, reject) => {
                window.require.config({
                    paths: {
                        'vscode-textmate': asset('textmate/vscode-textmate'),
                        'vscode-oniguruma': asset('textmate/vscode-oniguruma'),
                    },
                });
                window.require(['vscode-textmate', 'vscode-oniguruma'], (a, b) => resolve([a, b]), reject);
            });

            await onig.loadWASM(await fetch(asset('textmate/onig.wasm')).then((r) => r.arrayBuffer()));

            const registry = new tm.Registry({
                onigLib: Promise.resolve({
                    createOnigScanner: (patterns) => new onig.OnigScanner(patterns),
                    createOnigString: (text) => new onig.OnigString(text),
                }),
                // Called for the grammar asked for and for every grammar it
                // embeds, by scope name: Razor asks for source.cs and
                // text.html.basic, HTML for source.css and source.js.
                loadGrammar: async (scopeName) => {
                    const file = index.scopes[scopeName];
                    if (!file) {
                        return null;
                    }

                    const text = await fetch(asset('textmate/grammars/' + file)).then((r) => r.text());
                    return tm.parseRawGrammar(text, file);
                },
            });

            return { tm, registry, index };
        })();

        return engine;
    }

    // What Monaco carries between lines: the grammar's rule stack. Equal stacks
    // are what let Monaco stop re-tokenizing below an edit.
    class LineState {
        constructor(stack) {
            this.stack = stack;
        }

        clone() {
            return new LineState(this.stack);
        }

        equals(other) {
            return other instanceof LineState && (other.stack === this.stack || other.stack.equals(this.stack));
        }
    }

    // A token's scopes run from the grammar's root to the most specific, such as
    // text.aspnetcorerazor, meta.tag.html, entity.name.tag.html. The last is what
    // the theme colours; Monaco matches its rules by dotted prefix, so a rule for
    // entity.name.tag covers the .html one and every other language's.
    function tokensProvider(tm, grammar) {
        return {
            getInitialState: () => new LineState(tm.INITIAL),
            tokenize: (line, state) => {
                const result = grammar.tokenizeLine(line, state.stack, timeLimitMs);
                return {
                    tokens: result.tokens.map((token) => ({
                        startIndex: token.startIndex,
                        scopes: token.scopes[token.scopes.length - 1],
                    })),
                    endState: new LineState(result.ruleStack),
                };
            },
        };
    }

    window.agentsTextmate = {
        // Runs fn with a tighter per-line limit. The diff colours thousands of
        // lines in slices on the page's main thread, where one line at 500 ms
        // would blow every slice's budget; the editor keeps the generous limit.
        // Monaco calls the tokenizer synchronously inside fn, and fn does not
        // await, so nothing else can tokenize while the limit is lowered.
        withTimeLimit: (ms, fn) => {
            timeLimitMs = ms;
            try {
                return fn();
            } finally {
                timeLimitMs = editorTimeLimitMs;
            }
        },

        // Puts a grammar-backed tokenizer behind every listed language Monaco
        // knows and the index has a grammar for. A factory rather than a provider,
        // so the grammar is fetched when a model in that language first needs
        // colouring. Registering replaces Monaco's own factory for the language.
        register: async (monaco) => {
            let loaded;
            try {
                loaded = await load();
            } catch {
                return;
            }

            const known = new Set(monaco.languages.getLanguages().map((l) => l.id));
            for (const [language, name] of Object.entries(grammarFor)) {
                const scopeName = loaded.index.names[name];
                if (!known.has(language) || !scopeName) {
                    continue;
                }

                monaco.languages.registerTokensProviderFactory(language, {
                    create: async () => {
                        const grammar = await loaded.registry.loadGrammar(scopeName);
                        return grammar ? tokensProvider(loaded.tm, grammar) : null;
                    },
                });
            }
        },
    };
})();
