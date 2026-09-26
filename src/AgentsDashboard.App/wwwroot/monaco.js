// Monaco, loaded only once a page actually asks for an editor.
//
// The bundle is 24 MB on disk and a few megabytes over the wire, and every page
// in this app except the Files tab has no use for it, so nothing is fetched at
// startup: ensureLoaded() injects the AMD loader the first time create() is
// called and memoises the promise, and every later editor reuses it.
//
// The editor's text lives in the browser and the file lives on the server, so
// the crossings that matter are Ctrl+S (the whole text goes up), the dirty flag
// (a single boolean, sent only when it flips) and, for C#, a debounced copy of
// the buffer so Roslyn answers for what you typed. None of them is in the
// keystroke path: a round trip per character is what this shape avoids.

let loading = null;
let nextHandle = 1;

const editors = new Map();

// Model URI to editor state. The C# providers below are registered once for the
// whole page, and a provider is handed a model rather than an editor, so this is
// how a hover in one editor reaches the .NET object that owns it.
const models = new Map();

// Where each file was scrolled to and where its caret was, by model URI. Kept
// for the life of the page, because the Files tab is torn down every time you
// switch to another tab and the editor with it: without this every return to a
// file would land back at the top.
const viewStates = new Map();

// The gutter colours VS Code uses for its own change marks.
const markColours = {
    added: '#2ea043',
    modified: '#1b81a8',
    deleted: '#f85149',
};

function remember(editor) {
    const model = editor.getModel();
    if (model) {
        viewStates.set(model.uri.toString(), editor.saveViewState());
    }
}

function restore(editor) {
    const model = editor.getModel();
    const saved = model && viewStates.get(model.uri.toString());
    if (saved) {
        editor.restoreViewState(saved);
    }
}

// The AMD loader resolves relative paths against the document, and the Files tab
// lives at /worktree/<escaped path>/files, so a bare "monaco/vs" would be looked
// for under the worktree segment. Absolute from the base href is the only form
// that survives a deep link.
function asset(path) {
    return new URL(path, document.baseURI).href;
}

function ensureLoaded() {
    if (loading) {
        return loading;
    }

    loading = new Promise((resolve, reject) => {
        const script = document.createElement('script');
        script.src = asset('monaco/vs/loader.js');
        script.onload = () => {
            // editor.main.js installs its own MonacoEnvironment.getWorker, which
            // resolves the JSON, CSS, HTML and TypeScript workers next to the
            // bundle. Same origin here, so there is nothing to override.
            window.require.config({ paths: { vs: asset('monaco/vs') } });
            window.require(['vs/editor/editor.main'], () => resolve(window.monaco), reject);
        };
        script.onerror = () => reject(new Error('Monaco failed to load.'));
        document.head.appendChild(script);
    });

    return loading;
}

function prefersDark() {
    return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
}

function themeName() {
    return prefersDark() ? 'vs-dark' : 'vs';
}

// Extension to language id, built from Monaco's own registry rather than a hand
// written table, so anything it ships support for is coloured without being
// listed here. The overrides below are the cases the registry gets wrong for
// this repository: .razor is not registered anywhere, and MSBuild's project
// files are XML with project-specific extensions.
let extensions = null;

const overrides = {
    '.razor': 'razor',
    '.cshtml': 'razor',
    '.csproj': 'xml',
    '.props': 'xml',
    '.targets': 'xml',
    '.slnx': 'xml',
};

function languageFor(monaco, path) {
    if (!path) {
        return 'plaintext';
    }

    const name = path.split(/[\\/]/).pop().toLowerCase();
    const dot = name.lastIndexOf('.');
    if (dot < 0) {
        return 'plaintext';
    }

    const extension = name.slice(dot);
    if (overrides[extension]) {
        return overrides[extension];
    }

    if (!extensions) {
        extensions = new Map();
        for (const language of monaco.languages.getLanguages()) {
            for (const known of language.extensions || []) {
                extensions.set(known.toLowerCase(), language.id);
            }
        }
    }

    return extensions.get(extension) || 'plaintext';
}

function mono() {
    const value = getComputedStyle(document.documentElement).getPropertyValue('--mono');
    return value.trim() || 'monospace';
}

// Both the Monaco command and the host's keydown listener below can see the same
// Ctrl+S, depending on where focus sits inside the editor's chrome. Saving twice
// would send the second copy with a stamp the first save has not returned yet,
// which reads on the server as a conflict, so a save that lands on the heels of
// another is dropped.
function requestSave(state) {
    const now = Date.now();
    if (now - state.savedAt < 300) {
        return;
    }

    state.savedAt = now;
    state.dotnet.invokeMethodAsync('SaveFromEditor', state.editor.getValue());
}

function reportDirty(state) {
    const dirty = state.editor.getValue() !== state.baseline;
    if (dirty === state.dirty) {
        return;
    }

    state.dirty = dirty;
    state.dotnet.invokeMethodAsync('SetDirty', dirty);
}

// Each editor's model is created with the file's absolute path as its URI, not
// left anonymous. A definition in another file comes back from Roslyn as a path,
// and Monaco can only express that as a file URI: with anonymous models every
// cross-file jump would land on inmemory://model/1 and be dropped.
function makeModel(monaco, text, path, absolutePath) {
    const language = languageFor(monaco, path);
    if (!absolutePath) {
        return monaco.editor.createModel(text, language);
    }

    const uri = monaco.Uri.file(absolutePath);

    // Two models may not share a URI. Reopening a file the tab showed earlier is
    // ordinary, so the stale one is dropped rather than treated as an error.
    monaco.editor.getModel(uri)?.dispose();

    return monaco.editor.createModel(text, language, uri);
}

function ownerOf(model) {
    return model ? models.get(model.uri.toString()) || null : null;
}

function caretOf(editor) {
    return editor.getPosition() || { lineNumber: 1, column: 1 };
}

let providers = false;

// One registration for the page, not one per editor: Monaco's registries are
// global, and a second editor would otherwise install a second provider and
// every hover would be answered twice.
function registerProviders(monaco) {
    if (providers) {
        return;
    }

    providers = true;

    monaco.languages.registerHoverProvider('csharp', {
        provideHover: async (model, position) => {
            const state = ownerOf(model);
            if (!state) {
                return null;
            }

            const info = await state.dotnet.invokeMethodAsync('Hover', position.lineNumber, position.column);
            if (!info) {
                return null;
            }

            const contents = [{ value: '```csharp\n' + info.signature + '\n```' }];
            if (info.summary) {
                contents.push({ value: info.summary });
            }

            return { contents };
        },
    });

    monaco.languages.registerDefinitionProvider('csharp', {
        provideDefinition: async (model, position) => {
            const state = ownerOf(model);
            if (!state) {
                return null;
            }

            const found = await state.dotnet.invokeMethodAsync('Definition', position.lineNumber, position.column);

            return (found || []).map(location => ({
                uri: monaco.Uri.file(location.path),
                range: {
                    startLineNumber: location.line,
                    startColumn: location.column,
                    endLineNumber: location.endLine,
                    endColumn: location.endColumn,
                },
            }));
        },
    });

    // F12 and Cmd+click on something in another file. Monaco would otherwise want
    // a model for that file to open it in; the opener hands the path to the Files
    // tab instead, which opens it the same way a clicked link does.
    monaco.editor.registerEditorOpener({
        openCodeEditor(source, resource, selectionOrPosition) {
            const state = ownerOf(source.getModel());
            if (!state) {
                return false;
            }

            const target = selectionOrPosition || {};
            const line = target.startLineNumber || target.lineNumber || 1;
            const column = target.startColumn || target.column || 1;

            const model = state.editor.getModel();
            if (model && model.uri.toString() === resource.toString()) {
                state.editor.setPosition({ lineNumber: line, column });
                state.editor.revealLineInCenter(line);
                state.editor.focus();
                return true;
            }

            state.dotnet.invokeMethodAsync('OpenLocation', resource.fsPath, line, column);
            return true;
        },
    });
}

// References and callers are answered into a panel under the editor, so these are
// plain actions rather than a reference provider: Monaco's peek widget wants a
// text model for every file it lists, which means shipping every referenced file
// into the browser to show ten lines of it.
function addActions(monaco, state) {
    state.editor.addAction({
        id: 'agents.references',
        label: 'Find All References',
        keybindings: [monaco.KeyMod.Shift | monaco.KeyCode.F12],
        contextMenuGroupId: 'navigation',
        contextMenuOrder: 1.5,
        run: (editor) => {
            const caret = caretOf(editor);
            state.dotnet.invokeMethodAsync('ShowReferences', caret.lineNumber, caret.column);
        },
    });

    state.editor.addAction({
        id: 'agents.callHierarchy',
        label: 'Show Call Hierarchy',
        keybindings: [monaco.KeyMod.Shift | monaco.KeyMod.Alt | monaco.KeyCode.KeyH],
        contextMenuGroupId: 'navigation',
        contextMenuOrder: 1.6,
        run: (editor) => {
            const caret = caretOf(editor);
            state.dotnet.invokeMethodAsync('ShowCallHierarchy', caret.lineNumber, caret.column);
        },
    });
}

// Typing is still not sent on every keystroke, but references that ignore the
// buffer are wrong the moment you add a call, so the text goes up on a pause.
function scheduleSync(state) {
    clearTimeout(state.syncing);
    state.syncing = setTimeout(() => {
        state.dotnet.invokeMethodAsync('TextChanged', state.editor.getValue());
    }, 400);
}

window.agentsEditor = {
    create: async (element, dotnetRef, options) => {
        const monaco = await ensureLoaded();
        const settings = options || {};

        registerProviders(monaco);

        const model = makeModel(monaco, settings.text || '', settings.path, settings.absolutePath);

        const editor = monaco.editor.create(element, {
            model,
            theme: themeName(),
            readOnly: !!settings.readOnly,
            automaticLayout: true,
            minimap: { enabled: true },
            wordWrap: 'off',
            fontSize: 13,
            fontFamily: mono(),
            scrollBeyondLastLine: false,
            renderWhitespace: 'selection',
            tabSize: 4,
            insertSpaces: true,
        });

        const state = {
            monaco,
            editor,
            element,
            dotnet: dotnetRef,
            baseline: settings.text || '',
            dirty: false,
            savedAt: 0,
            suppress: false,
            syncing: 0,
        };

        models.set(model.uri.toString(), state);

        editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => requestSave(state));
        addActions(monaco, state);

        // The command above only fires while the code area has focus. The find
        // widget and the other chrome inside the host swallow it, and the browser
        // would then open its own save dialog over the app.
        state.onKeyDown = (event) => {
            if ((event.metaKey || event.ctrlKey) && !event.altKey && event.key.toLowerCase() === 's') {
                event.preventDefault();
                requestSave(state);
            }
        };

        element.addEventListener('keydown', state.onKeyDown);

        editor.onDidChangeModelContent(() => {
            if (!state.suppress) {
                reportDirty(state);
                scheduleSync(state);
            }
        });

        state.marks = editor.createDecorationsCollection([]);

        if (settings.line) {
            editor.revealLineInCenter(settings.line);
            editor.setPosition({ lineNumber: settings.line, column: 1 });
        } else {
            restore(editor);
        }

        const handle = nextHandle++;
        editors.set(handle, state);
        return handle;
    },

    // Replacing the text is also what resets the dirty baseline: it is used for
    // opening another file, for a reload after a conflict, and after a save that
    // came back with a new stamp. In all three the editor is now in step with the
    // disk, so anything the user had pending is deliberately gone.
    setText: (handle, text, stamp, path, absolutePath, hasLine) => {
        const state = editors.get(handle);
        if (!state) {
            return;
        }

        const monaco = state.monaco;
        const value = text || '';
        const current = state.editor.getModel();
        const wanted = absolutePath ? monaco.Uri.file(absolutePath).toString() : null;

        state.suppress = true;
        state.baseline = value;

        if (wanted && current && current.uri.toString() !== wanted) {
            // Another file, so another URI: setValue would leave the model named
            // after the file that was open before, and every definition Roslyn
            // reports for the new file would be resolved against the old name.
            remember(state.editor);
            state.marks.clear();
            const next = makeModel(monaco, value, path, absolutePath);

            models.delete(current.uri.toString());
            models.set(next.uri.toString(), state);

            state.editor.setModel(next);
            current.dispose();

            if (!hasLine) {
                restore(state.editor);
            }
        } else {
            state.editor.setValue(value);

            if (path && current) {
                monaco.editor.setModelLanguage(current, languageFor(monaco, path));
            }
        }

        state.suppress = false;

        if (state.dirty) {
            state.dirty = false;
            state.dotnet.invokeMethodAsync('SetDirty', false);
        }
    },

    getText: (handle) => editors.get(handle)?.editor.getValue() ?? '',

    // Changed lines against the diff base, drawn as VS Code draws them: a bar in
    // the gutter beside the line numbers, a tick in the scrollbar so changes far
    // down the file can be seen and clicked to, and the same colour in the
    // minimap. A deletion has no line of its own, so it is a small marker at the
    // foot of the line above the gap. The decorations move with edits, so the
    // marks stay on their lines while the file is being changed.
    setMarks: (handle, marks) => {
        const state = editors.get(handle);
        if (!state) {
            return;
        }

        const monaco = state.monaco;
        const model = state.editor.getModel();
        const last = model ? model.getLineCount() : 0;

        state.marks.set((marks || [])
            .filter((mark) => mark.line >= 1 && mark.line <= last)
            .map((mark) => ({
                range: new monaco.Range(mark.line, 1, mark.line, 1),
                options: {
                    isWholeLine: true,
                    linesDecorationsClassName: 'change-mark change-' + mark.kind,
                    overviewRuler: {
                        color: markColours[mark.kind],
                        position: monaco.editor.OverviewRulerLane.Left,
                    },
                    minimap: {
                        color: markColours[mark.kind],
                        position: monaco.editor.MinimapPosition.Gutter,
                    },
                },
            })));
    },

    revealLine: (handle, line) => {
        const state = editors.get(handle);
        if (!state || !line) {
            return;
        }

        state.editor.revealLineInCenter(line);
        state.editor.setPosition({ lineNumber: line, column: 1 });
    },

    // Colours lines for the Changes tab with Monaco's own tokenizer, so the diff
    // is coloured exactly as the Files tab and VS Code colour the same file.
    // Nothing is drawn here: the server renders the diff, and this only answers
    // which class each run gets. The classes are Monaco's mtkN colour classes,
    // whose rules the theme service injects globally, so they apply outside an
    // editor too.
    //
    // Each block is a run of lines tokenized as one sequence (a diff hunk read
    // down one side), so a comment or string opened on one line carries on to
    // the next. Returns null for a block when Monaco has no language for the
    // file, which leaves the server's own highlighter to it.
    colourLines: async (path, blocks) => {
        const monaco = await ensureLoaded();

        // Initialises the theme service, which is what injects the mtkN rules,
        // when no editor has been created yet.
        monaco.editor.setTheme(themeName());

        const language = languageFor(monaco, path);
        if (language === 'plaintext') {
            return blocks.map(() => null);
        }

        // Languages load lazily. colorize waits for the tokenizer to arrive,
        // where tokenizing a model straight away would read every line as plain.
        await monaco.editor.colorize('', language, {});

        return blocks.map((lines) => {
            // A stray carriage return inside a line would read to Monaco as a line
            // break and shift every answer after it by one. Same length, so the
            // offsets still line up with the server's text.
            const text = lines.map((line) => line.replace(/\r/g, ' ')).join('\n');
            const model = monaco.editor.createModel(text, language);
            try {
                model.tokenization.forceTokenization(model.getLineCount());
                const ends = [];
                const classes = [];
                for (let n = 1; n <= lines.length; n++) {
                    const tokens = model.tokenization.getLineTokens(n);
                    const lineEnds = [];
                    const lineClasses = [];
                    for (let i = 0; i < tokens.getCount(); i++) {
                        lineEnds.push(tokens.getEndOffset(i));
                        lineClasses.push(tokens.getClassName(i));
                    }
                    ends.push(lineEnds);
                    classes.push(lineClasses);
                }
                return { ends, classes };
            } finally {
                model.dispose();
            }
        });
    },

    // A fenced code block from the Markdown preview, coloured the way the editor
    // colours that language. Returns Monaco's HTML for it, or null when Monaco has
    // no language by that name, in which case the block stays plain.
    colorizeHtml: async (text, language) => {
        const monaco = await ensureLoaded();
        monaco.editor.setTheme(themeName());

        const wanted = (language || '').toLowerCase();
        const aliases = { bash: 'shell', zsh: 'shell', sh: 'shell', console: 'shell', ts: 'typescript', js: 'javascript', yml: 'yaml', cs: 'csharp', 'c#': 'csharp', ps1: 'powershell', md: 'markdown', razor: 'razor', cshtml: 'razor', xaml: 'xml', csproj: 'xml' };
        let id = aliases[wanted] || null;
        if (!id) {
            const found = monaco.languages.getLanguages().find((l) =>
                l.id.toLowerCase() === wanted || (l.aliases || []).some((a) => a.toLowerCase() === wanted));
            id = found ? found.id : null;
        }

        if (!id) {
            return null;
        }

        return await monaco.editor.colorize(text, id, { tabSize: 4 });
    },

    setTheme: (theme) => {
        window.monaco?.editor.setTheme(theme);
    },

    layout: (handle) => {
        editors.get(handle)?.editor.layout();
    },

    dispose: (handle) => {
        const state = editors.get(handle);
        if (!state) {
            return;
        }

        editors.delete(handle);
        clearTimeout(state.syncing);
        remember(state.editor);
        state.element.removeEventListener('keydown', state.onKeyDown);

        const model = state.editor.getModel();
        if (model) {
            models.delete(model.uri.toString());
            model.dispose();
        }

        state.editor.dispose();
    },
};

// The app has no theme switch of its own: it follows the OS, and so does Monaco.
if (window.matchMedia) {
    window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
        window.monaco?.editor.setTheme(themeName());
    });
}
