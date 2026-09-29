// Monaco, loaded only once a page actually asks for an editor.
//
// The bundle is 24 MB on disk and a few megabytes over the wire, and nothing but
// an open file (and the diff's colouring) has any use for it, so nothing is
// fetched at startup: ensureLoaded() injects the AMD loader the first time it is
// needed and memoises the promise, and every later editor reuses it.
//
// The editor's text lives in the browser and the file lives on the server, so
// the crossings that matter are Ctrl+S (the whole text goes up), the dirty flag
// (a single boolean, sent only when it flips) and, for C#, a debounced copy of
// the buffer so Roslyn answers for what you typed. None of them is in the
// keystroke path: a round trip per character is what this shape avoids.

let loading = null;
let nextHandle = 1;

const editors = new Map();

// Extensions' DiffViews, by handle. Kept apart from the editors above: they are
// read-only, own no file, and answer none of the calls a file's editor does
// (saving, word wrap, dirty state).
const diffViews = new Map();

// Model URI to editor state. The C# providers below are registered once for the
// whole page, and a provider is handed a model rather than an editor, so this is
// how a hover in one editor reaches the .NET object that owns it.
const models = new Map();

// Where each file was scrolled to and where its caret was, by model URI. Kept
// for the life of the page, because a file's editor is torn down when its tab is
// closed or you switch to another agent: without this every return to a file
// would land back at the top.
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

// The AMD loader resolves relative paths against the document, and an agent's
// page lives at /chat/<session>, so a bare "monaco/vs" would be looked for under
// that segment. Absolute from the base href is the only form
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
            window.require(['vs/editor/editor.main'], async () => {
                defineThemes(window.monaco);

                // VS Code's grammars in place of Monaco's own, before any model
                // is coloured. Without them vendored this changes nothing.
                await window.agentsTextmate?.register(window.monaco);
                resolve(window.monaco);
            }, reject);
        };
        script.onerror = () => reject(new Error('Monaco failed to load.'));
        document.head.appendChild(script);
    });

    return loading;
}

function themeName() {
    return window.agentsTheme?.isDark() !== false ? 'agents-dark' : 'agents-light';
}

// Monaco's own themes with VS Code's Dark+ and Light+ colours for two kinds of
// token the built-in themes have no rules for: the TextMate scopes the grammars
// in textmate.js report (keyword.control, entity.name.tag, storage.type...), and
// the semantic token types the C# provider below reports. Without these most of
// a file would stay the plain foreground. Monaco matches a rule by dotted prefix,
// so entity.name.tag covers entity.name.tag.html and every other language's.
function defineThemes(monaco) {
    const scopes = (c) => [
        { token: 'comment', foreground: c.comment },
        { token: 'string', foreground: c.string },
        { token: 'string.regexp', foreground: c.regexp },
        { token: 'constant.character.escape', foreground: c.escape },
        { token: 'constant.numeric', foreground: c.number },
        { token: 'keyword.other.unit', foreground: c.number },
        { token: 'constant.language', foreground: c.keyword },
        { token: 'constant.other', foreground: c.constant },
        { token: 'keyword', foreground: c.keyword },
        { token: 'keyword.control', foreground: c.control },
        { token: 'keyword.operator', foreground: c.plain },
        { token: 'keyword.operator.new', foreground: c.keyword },
        { token: 'keyword.operator.expression', foreground: c.keyword },
        { token: 'keyword.preprocessor', foreground: c.keyword },
        { token: 'storage', foreground: c.keyword },
        { token: 'storage.type', foreground: c.keyword },
        { token: 'storage.modifier', foreground: c.keyword },
        { token: 'entity.name.type', foreground: c.type },
        { token: 'entity.name.class', foreground: c.type },
        { token: 'entity.name.namespace', foreground: c.type },
        { token: 'entity.other.inherited-class', foreground: c.type },
        { token: 'support.type', foreground: c.type },
        { token: 'support.class', foreground: c.type },
        { token: 'entity.name.function', foreground: c.method },
        { token: 'support.function', foreground: c.method },
        { token: 'variable', foreground: c.member },
        { token: 'variable.language', foreground: c.keyword },
        { token: 'variable.other.constant', foreground: c.constant },
        { token: 'variable.other.enummember', foreground: c.constant },
        { token: 'meta.object-literal.key', foreground: c.member },
        { token: 'support.type.property-name', foreground: c.property },
        { token: 'entity.name.tag', foreground: c.tag },
        { token: 'entity.name.tag.css', foreground: c.selector },
        { token: 'entity.other.attribute-name', foreground: c.attribute },
        { token: 'entity.other.attribute-name.class.css', foreground: c.selector },
        { token: 'entity.other.attribute-name.id.css', foreground: c.selector },
        { token: 'punctuation.definition.tag', foreground: c.tagPunctuation },
        { token: 'markup.heading', foreground: c.heading, fontStyle: 'bold' },
        { token: 'markup.bold', fontStyle: 'bold' },
        { token: 'markup.italic', fontStyle: 'italic' },
        { token: 'markup.inline.raw', foreground: c.string },
        // Razor's transitions into code: the @ and the directives after it.
        { token: 'keyword.control.cshtml', foreground: c.keyword },
        { token: 'keyword.control.razor', foreground: c.keyword },
    ];

    const rules = (c) => [
        { token: 'namespace', foreground: c.plain },
        { token: 'class', foreground: c.type },
        { token: 'struct', foreground: c.type },
        { token: 'enum', foreground: c.type },
        { token: 'typeParameter', foreground: c.type },
        { token: 'interface', foreground: c.iface },
        { token: 'method', foreground: c.method },
        { token: 'property', foreground: c.member },
        { token: 'field', foreground: c.member },
        { token: 'event', foreground: c.member },
        { token: 'parameter', foreground: c.member },
        { token: 'variable', foreground: c.member },
        { token: 'enumMember', foreground: c.constant },
        { token: 'label', foreground: c.plain },
    ];

    const dark = {
        plain: 'D4D4D4', type: '4EC9B0', iface: 'B8D7A3', method: 'DCDCAA', member: '9CDCFE', constant: '4FC1FF',
        comment: '6A9955', string: 'CE9178', regexp: 'D16969', escape: 'D7BA7D', number: 'B5CEA8',
        keyword: '569CD6', control: 'C586C0', property: '9CDCFE', tag: '569CD6', tagPunctuation: '808080',
        attribute: '9CDCFE', selector: 'D7BA7D', heading: '569CD6',
    };

    const light = {
        plain: '000000', type: '267F99', iface: '267F99', method: '795E26', member: '001080', constant: '0070C1',
        comment: '008000', string: 'A31515', regexp: '811F3F', escape: 'EE0000', number: '098658',
        keyword: '0000FF', control: 'AF00DB', property: 'E50000', tag: '800000', tagPunctuation: '800000',
        attribute: 'E50000', selector: '800000', heading: '800000',
    };

    // Only the background follows the app's theme, and only for a theme that
    // sets --editor-bg: the token colours stay Dark+ and Light+, and Dark and
    // Light keep Monaco's own background as they always have. Redefined on every
    // theme change, which is why this reads the page rather than a constant.
    const background = window.agentsTheme?.variable('--editor-bg');
    const colors = background ? { 'editor.background': background, 'minimap.background': background } : {};

    monaco.editor.defineTheme('agents-dark', {
        base: 'vs-dark',
        inherit: true,
        rules: [...scopes(dark), ...rules(dark)],
        colors,
    });

    monaco.editor.defineTheme('agents-light', {
        base: 'vs',
        inherit: true,
        rules: [...scopes(light), ...rules(light)],
        colors,
    });
}

// The semantic token types, in the order Monaco indexes them. The server sends
// kinds by name, so this list is the only place the order matters.
const tokenTypes = ['namespace', 'class', 'struct', 'interface', 'enum', 'enumMember', 'typeParameter',
    'method', 'property', 'event', 'field', 'parameter', 'variable', 'label'];

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

// Ctrl+= and Ctrl+- size the text in every open editor at once, the way VS Code's
// editor zoom does, and the size is kept per machine so a reopened file does not
// snap back. The key is read in a capture listener on the host rather than bound
// as a Monaco command: the webview would otherwise zoom the whole page on the
// same keys whenever focus sat in the find widget or the minimap.
const defaultFontSize = 13;
const fontSizeKey = 'agents.editorFontSize';

function savedFontSize() {
    const value = Number(localStorage.getItem(fontSizeKey));
    return value >= 6 && value <= 40 ? value : defaultFontSize;
}

function setFontSize(size) {
    const value = Math.min(40, Math.max(6, size));
    if (value === defaultFontSize) {
        localStorage.removeItem(fontSizeKey);
    } else {
        localStorage.setItem(fontSizeKey, String(value));
    }

    for (const state of editors.values()) {
        state.editor.updateOptions({ fontSize: value });
    }

    for (const view of diffViews.values()) {
        view.diff.updateOptions({ fontSize: value });
    }
}

// Alt+Z wraps long lines at the editor's edge, as in VS Code. Like the text size
// it applies to every open editor and is kept per machine: whether a wide file is
// readable is a matter of the window, not of the file.
const wordWrapKey = 'agents.editorWordWrap';

function savedWordWrap() {
    return localStorage.getItem(wordWrapKey) === 'on' ? 'on' : 'off';
}

function toggleWordWrap() {
    const value = savedWordWrap() === 'on' ? 'off' : 'on';
    if (value === 'on') {
        localStorage.setItem(wordWrapKey, value);
    } else {
        localStorage.removeItem(wordWrapKey);
    }

    // Every editor is told, not just the one asked: each tab's toolbar button
    // shows the setting, and Alt+Z in one tab changes it for all of them.
    for (const state of editors.values()) {
        state.editor.updateOptions({ wordWrap: value });
        state.dotnet.invokeMethodAsync('WordWrapChanged', value === 'on');
    }
}

// Whether a diff shows its two sides next to each other or interleaved in one
// column, as VS Code's "Toggle Inline View". Kept per machine and applied to
// every open diff, like word wrap: it depends on how wide the window is, not
// on the file.
const sideBySideKey = 'agents.diffInline';

function savedSideBySide() {
    return localStorage.getItem(sideBySideKey) !== 'on';
}

function toggleSideBySide() {
    const sideBySide = !savedSideBySide();
    if (sideBySide) {
        localStorage.removeItem(sideBySideKey);
    } else {
        localStorage.setItem(sideBySideKey, 'on');
    }

    for (const state of editors.values()) {
        if (state.diff) {
            state.diff.updateOptions({ renderSideBySide: sideBySide });
            state.dotnet.invokeMethodAsync('SideBySideChanged', sideBySide);
        }
    }

    // An extension's diff that did not choose a layout follows the app's.
    for (const view of diffViews.values()) {
        if (view.settings.sideBySide === null || view.settings.sideBySide === undefined) {
            view.diff.updateOptions({ renderSideBySide: sideBySide });
        }
    }
}

// event.key is '=' or '+' depending on Shift and the layout, so the physical key
// and the numpad are both accepted.
function zoomStep(event) {
    if (!(event.metaKey || event.ctrlKey) || event.altKey) {
        return 0;
    }

    if (event.key === '=' || event.key === '+' || event.code === 'Equal' || event.code === 'NumpadAdd') {
        return 1;
    }

    if (event.key === '-' || event.key === '_' || event.code === 'Minus' || event.code === 'NumpadSubtract') {
        return -1;
    }

    if (event.key === '0' || event.code === 'Digit0' || event.code === 'Numpad0') {
        return null;
    }

    return 0;
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

// The model keeps the line endings it was created with, and the text the server
// sends may not, so two texts are compared with the endings taken out.
function sameText(a, b) {
    return a.replace(/\r\n/g, '\n') === b.replace(/\r\n/g, '\n');
}

function reportDirty(state) {
    const dirty = !sameText(state.editor.getValue(), state.baseline);
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
//
// A diff tab's model is the same file under a query naming the side, so it can
// be open beside the file's own tab: two models may not share a URI. A
// definition jump from it still lands on the plain file URI, which is a
// different model, so Monaco hands it to the opener and the file opens in its
// own tab, as VS Code does from a diff.
function modelUri(monaco, absolutePath, variant) {
    const uri = monaco.Uri.file(absolutePath);
    return variant ? uri.with({ query: variant }) : uri;
}

function makeModel(monaco, text, path, absolutePath, variant) {
    const language = languageFor(monaco, path);
    if (!absolutePath) {
        return monaco.editor.createModel(text, language);
    }

    const uri = modelUri(monaco, absolutePath, variant);

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

// Languages some extension answers for, each registered once.
const registered = new Set();

// Fired when a worktree's code intelligence loads or unloads, so every editor
// asks for its colouring again. Names are coloured by an extension, which is
// off until the user loads it, and an answer given before that was empty.
let semanticsChanged = null;

// One registration per language for the page, not one per editor: Monaco's
// registries are global, and a second editor would otherwise install a second
// provider and every hover would be answered twice. The languages are the ones
// the loaded extensions answer for; the app has none of its own, and one that
// arrives later is registered then. One that goes away stays registered and the
// server answers its questions with nothing.
function registerProviders(monaco, languages) {
    if (!semanticsChanged) {
        semanticsChanged = new monaco.Emitter();
        registerOpener(monaco);
    }

    for (const language of languages || []) {
        if (!registered.has(language)) {
            registered.add(language);
            registerLanguage(monaco, language);
        }
    }
}

function registerLanguage(monaco, language) {
    monaco.languages.registerHoverProvider(language, {
        provideHover: async (model, position) => {
            const state = ownerOf(model);
            if (!state) {
                return null;
            }

            const info = await state.dotnet.invokeMethodAsync('Hover', position.lineNumber, position.column);
            if (!info) {
                return null;
            }

            const contents = [{ value: '```' + language + '\n' + info.signature + '\n```' }];
            if (info.summary) {
                contents.push({ value: info.summary });
            }

            return { contents };
        },
    });

    // What each name is comes from the extension, as VS Code's comes from its
    // language server. The buffer is pushed first when an edit has not reached
    // the server yet, so the answer is for the text on screen; an answer that
    // arrives after another edit is dropped rather than painted onto lines that
    // have moved, and Monaco asks again for the newer text anyway.
    monaco.languages.registerDocumentSemanticTokensProvider(language, {
        getLegend: () => ({ tokenTypes, tokenModifiers: [] }),
        onDidChange: semanticsChanged.event,
        provideDocumentSemanticTokens: async (model) => {
            const state = ownerOf(model);
            if (!state) {
                return null;
            }

            await flushSync(state);
            const version = model.getVersionId();
            const answer = await state.dotnet.invokeMethodAsync('Classify');
            if (!answer || model.isDisposed() || model.getVersionId() !== version) {
                return null;
            }

            const index = answer.kinds.map((kind) => tokenTypes.indexOf(kind));
            const data = [];
            let lastLine = 0;
            let lastColumn = 0;
            for (let i = 0; i < answer.data.length; i += 4) {
                const type = index[answer.data[i + 3]];
                if (type < 0) {
                    continue;
                }

                // Monaco's encoding is zero-based and relative to the token before.
                const line = answer.data[i] - 1;
                const column = answer.data[i + 1] - 1;
                data.push(line - lastLine, line === lastLine ? column - lastColumn : column, answer.data[i + 2], type, 0);
                lastLine = line;
                lastColumn = column;
            }

            return { data: new Uint32Array(data) };
        },
        releaseDocumentSemanticTokens: () => { },
    });

    monaco.languages.registerDefinitionProvider(language, {
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
}

function registerOpener(monaco) {
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

            // Where the jump left from, so the mouse's back button can return.
            const from = caretOf(state.editor).lineNumber;
            const model = state.editor.getModel();
            if (model && model.uri.toString() === resource.toString()) {
                state.dotnet.invokeMethodAsync('JumpedWithin', from);
                state.editor.setPosition({ lineNumber: line, column });
                state.editor.revealLineInCenter(line);
                state.editor.focus();
                return true;
            }

            state.dotnet.invokeMethodAsync('OpenLocation', resource.fsPath, line, column, from);
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

    state.editor.addAction({
        id: 'agents.toggleWordWrap',
        label: 'Toggle Word Wrap',
        keybindings: [monaco.KeyMod.Alt | monaco.KeyCode.KeyZ],
        contextMenuGroupId: '9_view',
        run: () => toggleWordWrap(),
    });
}

// Typing is still not sent on every keystroke, but references that ignore the
// buffer are wrong the moment you add a call, so the text goes up on a pause.
function scheduleSync(state) {
    clearTimeout(state.syncing);
    state.pendingSync = true;
    state.syncing = setTimeout(() => {
        state.pendingSync = false;
        state.dotnet.invokeMethodAsync('TextChanged', state.editor.getValue());
    }, 400);
}

// Sends an edit still waiting on the pause now, for a query that needs the
// server to have the text on screen.
async function flushSync(state) {
    if (!state.pendingSync) {
        return;
    }

    clearTimeout(state.syncing);
    state.pendingSync = false;
    await state.dotnet.invokeMethodAsync('TextChanged', state.editor.getValue());
}

// The nearest ancestor that scrolls, which is what a DiffView reveals a line
// in: the view is as tall as its content, so it never scrolls itself.
function scrollParent(element) {
    for (let node = element.parentElement; node; node = node.parentElement) {
        const overflow = getComputedStyle(node).overflowY;
        if ((overflow === 'auto' || overflow === 'scroll') && node.scrollHeight > node.clientHeight) {
            return node;
        }
    }

    return document.scrollingElement;
}

function diffViewSideBySide(view) {
    return view.settings.sideBySide ?? savedSideBySide();
}

// Sized to what the diff shows rather than to its box, so a page of them reads
// as one document. Folded unchanged regions are hidden areas in the two
// editors, so their content height already leaves them out. Inline, the
// original editor is hidden and the modified one carries the removed lines.
function fitDiffView(view) {
    const original = view.diff.getOriginalEditor();
    const modified = view.diff.getModifiedEditor();
    const height = diffViewSideBySide(view)
        ? Math.max(original.getContentHeight(), modified.getContentHeight())
        : modified.getContentHeight();

    const px = Math.max(24, Math.ceil(height)) + 'px';
    if (view.element.style.height !== px) {
        view.element.style.height = px;
        view.diff.layout();
    }
}

function diffViewMarks(view) {
    const monaco = view.monaco;
    for (const side of ['original', 'modified']) {
        const editor = side === 'original' ? view.diff.getOriginalEditor() : view.diff.getModifiedEditor();
        const last = editor.getModel()?.getLineCount() ?? 0;
        const decorations = [];
        for (const mark of view.settings.marks || []) {
            if (mark.side !== side || mark.start < 1 || mark.start > last) {
                continue;
            }

            const end = Math.min(Math.max(mark.start, mark.end), last);
            decorations.push({
                range: new monaco.Range(mark.start, 1, end, 1),
                options: { isWholeLine: true, className: 'diff-view-marked' },
            });
            decorations.push({
                range: new monaco.Range(mark.start, 1, mark.start, 1),
                options: {
                    glyphMarginClassName: 'diff-view-mark',
                    glyphMarginHoverMessage: mark.tooltip ? { value: mark.tooltip } : undefined,
                },
            });
        }

        view.marks[side].set(decorations);
    }
}

function diffViewOptions(settings) {
    return {
        renderSideBySide: settings.sideBySide ?? savedSideBySide(),
        hideUnchangedRegions: {
            enabled: settings.collapse !== false,
            contextLineCount: 3,
            minimumLineCount: 3,
            revealLineCount: 20,
        },
        glyphMargin: !!settings.commentable || (settings.marks || []).length > 0,
    };
}

// Lines a click in the gutter asks to comment on: the line clicked, or the
// selection when the click is inside it. The selection is the one from before
// the press, since Monaco moves it on a gutter click.
function commentLines(selection, line) {
    if (selection && !selection.isEmpty() && line >= selection.startLineNumber && line <= selection.endLineNumber) {
        const end = selection.endColumn === 1 && selection.endLineNumber > selection.startLineNumber
            ? selection.endLineNumber - 1
            : selection.endLineNumber;
        return [selection.startLineNumber, end];
    }

    return [line, line];
}

window.agentsEditor = {
    createDiffView: async (element, dotnetRef, options) => {
        const monaco = await ensureLoaded();
        const settings = options || {};
        const language = languageFor(monaco, settings.path || '');
        const original = monaco.editor.createModel(settings.original || '', language);
        const modified = monaco.editor.createModel(settings.modified || '', language);

        const diff = monaco.editor.createDiffEditor(element, {
            theme: themeName(),
            readOnly: true,
            originalEditable: false,
            automaticLayout: true,
            useInlineViewWhenSpaceIsLimited: false,
            ignoreTrimWhitespace: false,
            renderOverviewRuler: false,
            renderMarginRevertIcon: false,
            minimap: { enabled: false },
            folding: false,
            stickyScroll: { enabled: false },
            scrollBeyondLastLine: false,
            // The page scrolls, not the diff: a wheel over it goes on to the page.
            scrollbar: { vertical: 'hidden', verticalScrollbarSize: 0, alwaysConsumeMouseWheel: false },
            fontSize: savedFontSize(),
            fontFamily: mono(),
            wordWrap: savedWordWrap(),
            ...diffViewOptions(settings),
        });
        diff.setModel({ original, modified });

        const view = {
            monaco,
            diff,
            original,
            modified,
            element,
            dotnet: dotnetRef,
            settings,
            marks: {
                original: diff.getOriginalEditor().createDecorationsCollection([]),
                modified: diff.getModifiedEditor().createDecorationsCollection([]),
            },
            hover: {
                original: diff.getOriginalEditor().createDecorationsCollection([]),
                modified: diff.getModifiedEditor().createDecorationsCollection([]),
            },
            before: { original: null, modified: null },
            disposables: [],
        };

        // Captured on the way down, before Monaco's own handler moves the
        // selection to the line clicked.
        view.onPress = () => {
            view.before.original = diff.getOriginalEditor().getSelection();
            view.before.modified = diff.getModifiedEditor().getSelection();
        };
        element.addEventListener('mousedown', view.onPress, true);

        for (const side of ['original', 'modified']) {
            const editor = side === 'original' ? diff.getOriginalEditor() : diff.getModifiedEditor();
            const glyph = monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN;
            const lineNumbers = monaco.editor.MouseTargetType.GUTTER_LINE_NUMBERS;
            const content = monaco.editor.MouseTargetType.CONTENT_TEXT;
            const empty = monaco.editor.MouseTargetType.CONTENT_EMPTY;

            view.disposables.push(editor.onMouseMove((e) => {
                const line = e.target.position?.lineNumber;
                if (!view.settings.commentable || !line || ![glyph, lineNumbers, content, empty].includes(e.target.type)) {
                    view.hover[side].clear();
                    return;
                }

                view.hover[side].set([{
                    range: new monaco.Range(line, 1, line, 1),
                    options: { glyphMarginClassName: 'diff-view-add' },
                }]);
            }));
            view.disposables.push(editor.onMouseLeave(() => view.hover[side].clear()));
            view.disposables.push(editor.onMouseDown((e) => {
                if (!view.settings.commentable || e.target.type !== glyph || !e.target.position) {
                    return;
                }

                const [start, end] = commentLines(view.before[side], e.target.position.lineNumber);
                view.dotnet.invokeMethodAsync('Comment', side, start, end);
            }));
            view.disposables.push(editor.onDidContentSizeChange(() => fitDiffView(view)));
        }

        view.disposables.push(diff.onDidUpdateDiff(() => fitDiffView(view)));

        diffViewMarks(view);
        fitDiffView(view);

        const handle = nextHandle++;
        diffViews.set(handle, view);
        return handle;
    },

    updateDiffView: (handle, options, texts) => {
        const view = diffViews.get(handle);
        if (!view) {
            return;
        }

        view.settings = options || {};
        if (texts) {
            if (!sameText(view.original.getValue(), view.settings.original || '')) {
                view.original.setValue(view.settings.original || '');
            }

            if (!sameText(view.modified.getValue(), view.settings.modified || '')) {
                view.modified.setValue(view.settings.modified || '');
            }

            const language = languageFor(view.monaco, view.settings.path || '');
            view.monaco.editor.setModelLanguage(view.original, language);
            view.monaco.editor.setModelLanguage(view.modified, language);
        }

        view.diff.updateOptions(diffViewOptions(view.settings));
        diffViewMarks(view);
        fitDiffView(view);
    },

    revealDiffView: (handle, side, start, end) => {
        const view = diffViews.get(handle);
        if (!view) {
            return;
        }

        // Inline, the original editor is hidden; its lines are shown, if at
        // all, among the modified ones, so the reveal lands on the view itself.
        const inline = !diffViewSideBySide(view);
        const editor = side === 'original' && !inline ? view.diff.getOriginalEditor() : view.diff.getModifiedEditor();
        const top = side === 'original' && inline ? 0 : editor.getTopForLineNumber(start);
        const scroller = scrollParent(view.element);
        const from = scroller === document.scrollingElement ? 0 : scroller.getBoundingClientRect().top;
        scroller.scrollTop += view.element.getBoundingClientRect().top - from + top - 80;

        if (!(side === 'original' && inline)) {
            const model = editor.getModel();
            const last = Math.min(Math.max(start, end), model.getLineCount());
            editor.setSelection(new view.monaco.Range(start, 1, last, model.getLineMaxColumn(last)));
        }
    },

    disposeDiffView: (handle) => {
        const view = diffViews.get(handle);
        if (!view) {
            return;
        }

        diffViews.delete(handle);
        view.element.removeEventListener('mousedown', view.onPress, true);
        for (const disposable of view.disposables) {
            disposable.dispose();
        }

        view.diff.dispose();
        view.original.dispose();
        view.modified.dispose();
    },

    create: async (element, dotnetRef, options) => {
        const monaco = await ensureLoaded();
        const settings = options || {};

        registerProviders(monaco, settings.languages);

        const isDiff = typeof settings.original === 'string';
        const variant = isDiff ? settings.variant || 'diff' : null;
        const model = makeModel(monaco, settings.text || '', settings.path, settings.absolutePath, variant);

        const common = {
            theme: themeName(),
            'semanticHighlighting.enabled': true,
            readOnly: !!settings.readOnly,
            automaticLayout: true,
            minimap: { enabled: true },
            wordWrap: savedWordWrap(),
            fontSize: savedFontSize(),
            fontFamily: mono(),
            scrollBeyondLastLine: false,
            renderWhitespace: 'selection',
            tabSize: 4,
            insertSpaces: true,
        };

        // A diff is Monaco's own diff editor, side by side as VS Code shows a
        // changed file. Everything else here works on its right-hand editor,
        // which holds the file and is the one that can be edited and saved.
        let diff = null;
        let original = null;
        let editor;
        if (isDiff) {
            diff = monaco.editor.createDiffEditor(element, {
                ...common,
                originalEditable: false,
                renderSideBySide: savedSideBySide(),
                // Below this width Monaco would quietly switch to inline on its
                // own, and the toggle would then say one thing and show another.
                useInlineViewWhenSpaceIsLimited: false,
                ignoreTrimWhitespace: false,
            });
            original = monaco.editor.createModel(settings.original, languageFor(monaco, settings.path));
            diff.setModel({ original, modified: model });
            editor = diff.getModifiedEditor();
        } else {
            editor = monaco.editor.create(element, { ...common, model });
        }

        const state = {
            monaco,
            editor,
            diff,
            original,
            variant,
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

        state.onZoomKey = (event) => {
            const step = zoomStep(event);
            if (step === 0) {
                return;
            }

            event.preventDefault();
            event.stopPropagation();
            setFontSize(step === null ? defaultFontSize : savedFontSize() + step);
        };

        element.addEventListener('keydown', state.onZoomKey, true);

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
        } else if (diff) {
            // The diff is computed off the main thread, so the first change is
            // only known once it lands. That is where VS Code opens a diff.
            const first = diff.onDidUpdateDiff(() => {
                first.dispose();
                const change = (diff.getLineChanges() || [])[0];
                if (change) {
                    const line = Math.max(1, change.modifiedStartLineNumber || change.modifiedEndLineNumber || 1);
                    editor.revealLineInCenter(line);
                    editor.setPosition({ lineNumber: line, column: 1 });
                }
            });
        } else {
            restore(editor);
        }

        const handle = nextHandle++;
        editors.set(handle, state);

        if (savedWordWrap() === 'on') {
            dotnetRef.invokeMethodAsync('WordWrapChanged', true);
        }

        if (diff) {
            dotnetRef.invokeMethodAsync('SideBySideChanged', savedSideBySide());
        }

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
        const wanted = absolutePath ? modelUri(monaco, absolutePath, state.variant).toString() : null;

        state.suppress = true;
        state.baseline = value;

        if (wanted && current && current.uri.toString() !== wanted) {
            // Another file, so another URI: setValue would leave the model named
            // after the file that was open before, and every definition Roslyn
            // reports for the new file would be resolved against the old name.
            remember(state.editor);
            state.marks.clear();
            const next = makeModel(monaco, value, path, absolutePath, state.variant);

            models.delete(current.uri.toString());
            models.set(next.uri.toString(), state);

            if (state.diff) {
                state.diff.setModel({ original: state.original, modified: next });
            } else {
                state.editor.setModel(next);
            }

            current.dispose();

            if (!hasLine) {
                restore(state.editor);
            }
        } else if (current) {
            // The same file, which is every save: a save comes back with a new
            // stamp and lands here. setValue would throw away the undo stack, so
            // Ctrl+Z after a save would do nothing. When the buffer already
            // holds what was saved there is nothing to replace; when it differs
            // (a reload after a conflict) the text goes in as one edit, which
            // Ctrl+Z can take back like any other.
            if (!sameText(current.getValue(), value)) {
                current.pushStackElement();
                current.pushEditOperations(
                    [],
                    [{ range: current.getFullModelRange(), text: value }],
                    () => null);
                current.pushStackElement();
            }

            if (path) {
                monaco.editor.setModelLanguage(current, languageFor(monaco, path));
            }
        }

        if (state.original && path) {
            monaco.editor.setModelLanguage(state.original, languageFor(monaco, path));
        }

        state.suppress = false;

        if (state.dirty) {
            state.dirty = false;
            state.dotnet.invokeMethodAsync('SetDirty', false);
        }
    },

    toggleWordWrap: () => toggleWordWrap(),

    toggleSideBySide: () => toggleSideBySide(),

    // The left side of a diff, read again when a stage or a commit moves it.
    // Kept as one edit rather than a new model, so the diff editor keeps its
    // scroll and the right-hand side is not touched at all.
    setOriginal: (handle, text) => {
        const state = editors.get(handle);
        if (!state || !state.original) {
            return;
        }

        const value = text || '';
        if (!sameText(state.original.getValue(), value)) {
            state.original.setValue(value);
        }
    },

    getText: (handle) => editors.get(handle)?.editor.getValue() ?? '',

    // Changed lines against HEAD, drawn as VS Code draws them: a bar in
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

    refreshSemantics: (languages) => {
        if (!window.monaco) {
            return;
        }

        registerProviders(window.monaco, languages);
        semanticsChanged.fire();
    },

    setTheme: (theme) => {
        window.monaco?.editor.setTheme(theme);
    },

    // The caret line of the editor on screen, for going back to where you were.
    // Every open file keeps an editor, so it is the one not in a hidden tab.
    // Puts the caret in the editor of a file just opened from outside it, Go to
    // File above all, so typing carries on in the file as it does in VS Code.
    // The editor may not exist yet when this is asked (the tab renders, then
    // Monaco is created), so it looks again for a moment before giving up.
    focusFile: (absolutePath) => {
        let tries = 0;
        const attempt = () => {
            const monaco = window.monaco;
            if (monaco) {
                const wanted = monaco.Uri.file(absolutePath).toString();
                for (const state of editors.values()) {
                    const model = state.editor.getModel();
                    if (state.element.isConnected && !state.element.closest('.hidden') && model && model.uri.toString() === wanted) {
                        state.editor.focus();
                        return;
                    }
                }
            }

            if (++tries < 40) {
                setTimeout(attempt, 50);
            }
        };
        attempt();
    },

    visibleCaret: () => {
        for (const state of editors.values()) {
            if (state.element.isConnected && !state.element.closest('.hidden')) {
                return caretOf(state.editor).lineNumber;
            }
        }

        return null;
    },

    layout: (handle) => {
        const state = editors.get(handle);
        (state?.diff || state?.editor)?.layout();
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
        state.element.removeEventListener('keydown', state.onZoomKey, true);

        const model = state.editor.getModel();

        // The diff editor owns its two inner editors and goes first: a model
        // disposed while an editor still shows it throws.
        (state.diff || state.editor).dispose();
        state.original?.dispose();

        if (model) {
            models.delete(model.uri.toString());
            model.dispose();
        }
    },
};

// theme.js raises this when the palette changes, from Settings or, on System,
// from the OS.
window.addEventListener('agents-theme-change', () => {
    if (window.monaco) {
        defineThemes(window.monaco);
        window.monaco.editor.setTheme(themeName());
    }
});
