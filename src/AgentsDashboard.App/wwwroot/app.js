// Three things the server side cannot do for itself: put text on the clipboard
// of whichever machine is looking at the page, keep the window title honest
// about how many agents are blocked, and follow a drag.
//
// The drag is the interesting one. Selecting lines in a diff is a stream of
// mousemove events, and sending each one over the Blazor circuit would make a
// gesture cost hundreds of round trips. So the drag is followed entirely here,
// painting a preview with a class the server never sets, and the server is told
// once, on release, what was selected.

let drag = null;
// Each diff document on the page, and the component its picks are reported to.
// There can be several, a tab per changed file, so a drag answers to the one it
// started in rather than to whichever registered last.
const diffOwners = new WeakMap();
let wired = false;
let scroller = null;
let edgeTimer = null;

// How far above the bottom still counts as reading the bottom. Small, so a
// nudge of the wheel is enough to stop the thread following.
const PIN_SLACK = 40;

// Wires a chat thread once. Blazor replaces the element when you switch agent
// or tab, so the flag lives on the element and a new one is wired afresh.
function followThread(thread) {
    if (thread.dataset.following) {
        return;
    }

    thread.dataset.following = 'true';
    thread.dataset.pinned = 'true';

    // Not while the scrollbar is held: the drag decides where the thread is.
    const stick = () => {
        if (thread.dataset.pinned === 'true' && !held) {
            thread.scrollTop = thread.scrollHeight;
        }
    };

    // Only you scrolling can unpin the thread. Layout moves the scroll position
    // too, the message box growing or a reply being redrawn, and the browser
    // reports those as scroll events like any other, often a frame late when the
    // sizes have moved again. So a scroll away from the bottom counts only
    // shortly after a wheel, a touch, a key or a press on the scrollbar;
    // anything else puts a pinned thread back at the bottom.
    let touchedAt = 0;
    let held = false;
    const touched = () => { touchedAt = performance.now(); };
    thread.addEventListener('touchmove', touched, { passive: true });

    // Heading up unpins at once, on the input itself. The browser moves the
    // thread first and reports the scroll a frame later, and while a reply is
    // streaming in, the observers below fire in that gap: waiting for the scroll
    // event let them put a still-pinned thread back at the bottom, throwing
    // away your scroll before it ever counted.
    const leave = () => {
        touched();
        if (thread.scrollHeight > thread.clientHeight) {
            thread.dataset.pinned = 'false';
        }
    };
    thread.addEventListener('wheel', (e) => {
        if (e.deltaY < 0) {
            leave();
        } else {
            touched();
        }
    }, { passive: true });
    thread.addEventListener('keydown', (e) => {
        if (['ArrowUp', 'PageUp', 'Home'].includes(e.key)) {
            leave();
        } else {
            touched();
        }
    }, { passive: true });

    // Dragging the scrollbar can go on for longer than the window above, so a
    // press counts for as long as the button is down.
    thread.addEventListener('mousedown', () => { held = true; touched(); }, { passive: true });
    window.addEventListener('mouseup', () => {
        if (held) {
            held = false;
            touched();
        }
    }, { passive: true });

    thread.addEventListener('scroll', () => {
        const distance = thread.scrollHeight - thread.scrollTop - thread.clientHeight;
        if (distance <= PIN_SLACK) {
            thread.dataset.pinned = 'true';
        } else if (held || performance.now() - touchedAt < 1000) {
            thread.dataset.pinned = 'false';
        } else {
            stick();
        }
    }, { passive: true });

    new MutationObserver(stick).observe(thread, { childList: true, subtree: true, characterData: true, attributes: true, attributeFilter: ['open', 'class'] });
    new ResizeObserver(stick).observe(thread);
}

// Where each remembered scroller was, by key, for the life of the page. The tabs
// that own these scrollers are torn down when you switch away, and a new
// element comes back in their place.
const scrollMemory = new Map();

// Keeps an element's scroll position under a key, and puts it back when an
// element with the same key appears again. Content usually arrives after the
// element does (a diff is read from git, a tree is listed), so the restore is
// retried as the content grows, until it lands or you scroll yourself.
function keepScroll(element, key) {
    if (!element || element.dataset.keepScroll === key) {
        return;
    }

    element.dataset.keepScroll = key;
    let wanted = scrollMemory.get(key) || 0;

    const settle = () => {
        if (wanted <= 0) {
            return;
        }

        element.scrollTop = wanted;
        if (Math.abs(element.scrollTop - wanted) < 2) {
            wanted = 0;
        }
    };

    // Any sign of you scrolling ends the restore, so it never fights you.
    for (const type of ['wheel', 'touchstart', 'keydown', 'mousedown']) {
        element.addEventListener(type, () => { wanted = 0; }, { passive: true });
    }

    // Restoring scrolls too, and a half-finished restore is not a position
    // worth remembering, so only settled positions are recorded.
    element.addEventListener('scroll', () => {
        if (wanted <= 0) {
            scrollMemory.set(key, element.scrollTop);
        }
    }, { passive: true });

    new MutationObserver(settle).observe(element, { childList: true, subtree: true });
    settle();
}

// The nearest ancestor that scrolls, which is what "near the screen" is measured
// against: the document's own scroller, not the window.
function scrollParent(element) {
    for (let node = element.parentElement; node && node !== document.body; node = node.parentElement) {
        const overflow = getComputedStyle(node).overflowY;
        if (overflow === 'auto' || overflow === 'scroll') {
            return node;
        }
    }

    return null;
}

// Tells the Changes document which files are within a screen or so of the viewport,
// so it draws only their lines, along with the measured height of every file it
// has drawn, so a file that scrolls away leaves a block exactly its own height.
// Reports are batched: a fast scroll crosses many files, and one round trip per
// file would be the lag this exists to remove.
function watchDiffWindow(column, reference) {
    if (!column || column.dataset.windowed) {
        return;
    }

    column.dataset.windowed = 'true';
    const near = new Map();
    let timer = 0;

    const report = () => {
        timer = 0;
        if (!column.isConnected) {
            io.disconnect();
            mo.disconnect();
            return;
        }

        const paths = [];
        for (const [article, isNear] of near) {
            if (isNear && article.isConnected) {
                paths.push(article.dataset.diffFile);
            } else if (!article.isConnected) {
                near.delete(article);
            }
        }

        const heights = {};
        for (const body of column.querySelectorAll('[data-diff-body]')) {
            heights[body.dataset.diffBody] = body.offsetHeight;
        }

        reference.invokeMethodAsync('SetWindow', paths, heights).catch(() => { });
    };

    const schedule = () => {
        if (!timer) {
            timer = setTimeout(report, 60);
        }
    };

    const io = new IntersectionObserver((entries) => {
        for (const entry of entries) {
            near.set(entry.target, entry.isIntersecting);
        }

        schedule();
    }, { root: scrollParent(column), rootMargin: '1500px 0px' });

    const watchAll = () => {
        for (const article of column.querySelectorAll('article[data-diff-file]')) {
            if (!article.dataset.watched) {
                article.dataset.watched = 'true';
                io.observe(article);
            }
        }
    };

    const mo = new MutationObserver(watchAll);
    mo.observe(column, { childList: true });
    watchAll();
}

// Mermaid is one 5.5 MB script, so it is fetched the first time a preview has a
// diagram in it and never otherwise.
let mermaidLoading = null;

function loadMermaid() {
    if (!mermaidLoading) {
        mermaidLoading = new Promise((resolve, reject) => {
            const script = document.createElement('script');
            script.src = new URL('mermaid/mermaid.min.js', document.baseURI).href;
            script.onload = () => resolve(window.mermaid);
            script.onerror = () => reject(new Error('Mermaid failed to load.'));
            document.head.appendChild(script);
        });
    }

    return mermaidLoading;
}

// Finishes a Markdown preview the server rendered: colours its code blocks with
// Monaco, draws its diagrams, and routes links to other files in the repository
// to the editor. Each step marks what it has done, so running this again
// after a re-render only touches what is new.
async function enhanceMarkdown(element, reference) {
    if (!element) {
        return;
    }

    if (!element.dataset.linked) {
        element.dataset.linked = 'true';

        // Capture, and stopped here: otherwise the router would take the click
        // first and treat the link as a page of this app.
        element.addEventListener('click', (event) => {
            const link = event.target.closest('a[data-file]');
            if (link && element.contains(link)) {
                event.preventDefault();
                event.stopPropagation();
                reference.invokeMethodAsync('OpenLinked', link.dataset.file).catch(() => { });
            }
        }, true);
    }

    for (const code of element.querySelectorAll('pre > code[class*="language-"]')) {
        if (code.dataset.coloured) {
            continue;
        }

        code.dataset.coloured = 'true';
        const language = [...code.classList].find((c) => c.startsWith('language-')).slice('language-'.length);
        try {
            const html = await window.agentsEditor.colorizeHtml(code.textContent, language);
            if (html) {
                code.innerHTML = html;
            }
        } catch {
            // Monaco did not load. The block stays plain, which is still readable.
        }
    }

    const diagrams = [...element.querySelectorAll('.mermaid:not([data-processed])')];
    if (diagrams.length > 0) {
        try {
            const mermaid = await loadMermaid();
            const dark = window.agentsTheme?.isDark() !== false;
            mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', theme: dark ? 'dark' : 'default' });
            await mermaid.run({ nodes: diagrams, suppressErrors: true });
        } catch {
            // Without Mermaid the diagram's source stays on the page as text.
        }
    }
}

function lineOf(element) {
    const row = element.closest?.('.diff-line');
    if (!row || row.dataset.line === undefined) {
        return null;
    }

    const line = Number.parseInt(row.dataset.line, 10);
    return Number.isNaN(line) ? null : { row, file: row.dataset.file, side: row.dataset.side, line };
}

function clearPreview() {
    for (const row of document.querySelectorAll('.diff-line.picking')) {
        row.classList.remove('picking');
    }
}

// What the gesture has covered so far: the rows between where it started and
// where the pointer is, narrowed to one side of the diff.
//
// A comment belongs to one side, but a changed block is removed lines followed
// by added ones, so dragging down the gutter crosses from one side to the other
// almost every time. The side that wins is the side under the pointer, and rows
// of the other side inside the span are stepped over rather than ending the
// drag. Anchoring on the side the press happened to land on instead stopped the
// range at the first row of the other kind, which for a one-line replacement
// meant a drag of any length still selected a single line.
function span(to) {
    const low = Math.min(drag.from, to);
    const high = Math.max(drag.from, to);
    const side = drag.rows[to].side;
    const rows = [];

    for (let i = low; i <= high; i++) {
        if (drag.rows[i].side === side) {
            rows.push(drag.rows[i]);
        }
    }

    return { side, rows };
}

function paint(to) {
    if (!drag) {
        return;
    }

    const picked = new Set(span(to).rows.map(row => row.element));
    for (const row of drag.rows) {
        row.element.classList.toggle('picking', picked.has(row.element));
    }
}

// Every line of this file in the document the drag started in, both sides, in
// the order they are drawn: the drag walks rows rather than line numbers,
// because the two sides number themselves independently. Collected once so a
// move does not walk the whole page.
function rowsFor(file, owner) {
    const rows = [];
    for (const element of owner.querySelectorAll('.diff-line')) {
        if (element.dataset.file === file) {
            const line = Number.parseInt(element.dataset.line, 10);
            if (!Number.isNaN(line)) {
                rows.push({ element, line, side: element.dataset.side });
            }
        }
    }

    return rows;
}

function indexOf(row) {
    return drag ? drag.rows.findIndex(candidate => candidate.element === row) : -1;
}

function onDown(event) {
    if (event.button !== 0 || !event.target.closest('.ln-pick')) {
        return;
    }

    const start = lineOf(event.target);
    const owner = event.target.closest('[data-diff-doc]');
    if (!start || !owner || !diffOwners.has(owner)) {
        return;
    }

    // Stops the browser starting a text selection across the diff as you drag.
    event.preventDefault();

    drag = { file: start.file, rows: rowsFor(start.file, owner), from: 0, to: 0, extend: event.shiftKey, reference: diffOwners.get(owner) };

    const anchor = indexOf(start.row);
    if (anchor < 0) {
        drag = null;
        return;
    }

    drag.from = anchor;
    drag.to = anchor;

    scroller = event.target.closest('.doc-scroll, .main') ?? document.scrollingElement;
    paint(anchor);
}

function onMove(event) {
    if (!drag) {
        return;
    }

    const over = lineOf(document.elementFromPoint(event.clientX, event.clientY) ?? event.target);
    if (over && over.file === drag.file) {
        const index = indexOf(over.row);
        if (index >= 0) {
            drag.to = index;
            paint(index);
        }
    }

    edgeScroll(event.clientY);
}

// A range longer than the window is the normal case in a real diff, so dragging
// past the edge has to keep going.
function edgeScroll(y) {
    if (!scroller) {
        return;
    }

    const box = scroller.getBoundingClientRect
        ? scroller.getBoundingClientRect()
        : { top: 0, bottom: window.innerHeight };

    const margin = 40;
    const speed = y < box.top + margin ? -12 : y > box.bottom - margin ? 12 : 0;

    if (speed === 0) {
        clearInterval(edgeTimer);
        edgeTimer = null;
        return;
    }

    if (!edgeTimer) {
        edgeTimer = setInterval(() => scroller.scrollBy(0, speed), 16);
    }
}

function onUp() {
    if (!drag) {
        return;
    }

    const file = drag.file;
    const extend = drag.extend;
    const reference = drag.reference;
    const picked = span(drag.to);
    drag = null;
    clearInterval(edgeTimer);
    edgeTimer = null;

    // The preview is dropped before the server is told, so the class it renders
    // is the only one on the row afterwards and the two cannot disagree.
    clearPreview();

    const lines = picked.rows.map(row => row.line);
    reference.invokeMethodAsync(
        'SelectLines', file, picked.side, Math.min(...lines), Math.max(...lines), extend);
}

// A mouse click on the gutter has already been handled here, so it must not also
// reach the server's own click handler. A keyboard activation reports a detail of
// zero and is let through, which is what keeps the gutter usable without a mouse.
function onClick(event) {
    if (event.detail > 0 && event.target.closest('.ln-pick')) {
        event.stopPropagation();
    }
}

// The chat composer. Enter sends and Shift+Enter starts a new line, the way every
// messaging app works; a textarea on its own does the opposite. The box grows with
// what is typed, up to a limit, so a long message is readable before it goes.
// Measuring the text means letting the box fall back to its natural height for
// a moment. The row around it is held at its current height while that happens:
// otherwise the thread above grows for that instant, the browser clamps its
// scroll, and the scroll event that follows arrives after the box has grown
// back, reading as you having scrolled up and unpinning the thread.
function growComposer(box) {
    const row = box.parentElement;
    if (row) {
        row.style.minHeight = row.offsetHeight + 'px';
    }

    box.style.height = 'auto';
    box.style.height = Math.min(box.scrollHeight, 240) + 'px';

    if (row) {
        row.style.minHeight = '';
    }
}

// Slash commands in the composer: typing a slash at the start of a word lists
// the commands the agent takes (its skills, your custom commands, its own) and
// narrows them as you type. It all happens here rather than on the circuit, so
// the list keeps up with typing. The server only hands over the commands, and
// the menu element it renders is left empty for this to fill; Blazor never
// touches an element's children it did not render.
//
// Tab completes the name and leaves the caret after it for an argument. Enter
// does the same for a command that takes an argument, and sends one that does
// not, the way the terminal runs it. Only a command at the start of the message
// is run as one; further in, it is text the agent reads, which is how a skill
// is asked for mid-sentence, so there Enter only completes.
const commandMenus = new WeakMap();
let commandMenuCount = 0;

function commandState(box) {
    let state = commandMenus.get(box);
    if (!state) {
        state = { commands: [], menu: null, shown: [], index: 0, id: 'cmd' + (++commandMenuCount) };
        commandMenus.set(box, state);
    }

    return state;
}

// The slash word the caret is at the end of, and where it starts. Only a slash
// at the start of the box or after a space counts, so a path is left alone.
function commandToken(box) {
    const caret = box.selectionStart ?? box.value.length;
    const match = /(^|\s)\/(\S*)$/.exec(box.value.slice(0, caret));
    if (!match) {
        return null;
    }

    const start = match.index + match[1].length;
    const end = start + 1 + (/^\S*/.exec(box.value.slice(start + 1))?.[0].length ?? 0);
    return { query: match[2].toLowerCase(), start, end };
}

function commandQuery(box) {
    return commandToken(box)?.query ?? null;
}

function closeCommands(box) {
    const state = commandState(box);
    state.shown = [];
    if (state.menu) {
        state.menu.replaceChildren();
    }

    box.removeAttribute('aria-activedescendant');
    box.setAttribute('aria-expanded', 'false');
}

function updateCommands(box) {
    const state = commandState(box);
    const query = commandQuery(box);
    if (query === null || !state.menu || state.commands.length === 0) {
        closeCommands(box);
        return;
    }

    const name = (c) => c.name.toLowerCase();
    const starts = state.commands.filter((c) => name(c).startsWith(query));
    const contains = state.commands.filter((c) => !name(c).startsWith(query) && name(c).includes(query));
    const shown = starts.concat(contains);
    if (shown.length === 0) {
        closeCommands(box);
        return;
    }

    const same = shown.length === state.shown.length && shown.every((c, i) => c === state.shown[i]);
    state.index = same ? Math.min(state.index, shown.length - 1) : 0;
    state.shown = shown;
    drawCommands(box);
}

function drawCommands(box) {
    const state = commandState(box);
    const items = state.shown.map((command, i) => {
        const item = document.createElement('div');
        item.className = 'command-item' + (i === state.index ? ' selected' : '');
        item.id = state.id + '-' + i;
        item.setAttribute('role', 'option');
        item.setAttribute('aria-selected', i === state.index ? 'true' : 'false');

        const name = document.createElement('span');
        name.className = 'command-name mono';
        name.textContent = '/' + command.name;
        item.append(name);

        if (command.hint) {
            const hint = document.createElement('span');
            hint.className = 'command-hint mono';
            hint.textContent = command.hint;
            item.append(hint);
        }

        const description = document.createElement('span');
        description.className = 'command-desc';
        description.textContent = command.description;
        description.title = command.description;
        item.append(description);

        // Mousedown, not click, and cancelled: a click would take focus from the
        // box first, and losing focus closes the menu.
        item.addEventListener('mousedown', (event) => {
            event.preventDefault();
            state.index = i;
            acceptCommand(box, false);
        });
        return item;
    });

    const foot = document.createElement('div');
    foot.className = 'command-foot';
    foot.textContent = commandToken(box)?.start === 0
        ? 'Tab to complete, Enter to run, Esc to close'
        : 'Tab or Enter to complete, Esc to close';

    state.menu.replaceChildren(...items, foot);
    box.setAttribute('aria-expanded', 'true');
    box.setAttribute('aria-activedescendant', state.id + '-' + state.index);
    items[state.index]?.scrollIntoView({ block: 'nearest' });
}

function acceptCommand(box, run) {
    const state = commandState(box);
    const command = state.shown[state.index];
    if (!command) {
        return;
    }

    // Replaces the slash word, keeping everything around it.
    const token = commandToken(box);
    if (!token) {
        closeCommands(box);
        return;
    }

    const before = box.value.slice(0, token.start);
    const rest = box.value.slice(token.end).replace(/^\s+/, '');
    const send = run && token.start === 0 && !command.hint && rest.length === 0;
    const head = before + '/' + command.name + (send ? '' : ' ');
    box.value = head + rest;
    box.setSelectionRange(head.length, head.length);
    closeCommands(box);

    // The server learns what is in the box from input events, so it has to see
    // one before the submit that follows, which it then handles in order.
    box.dispatchEvent(new Event('input', { bubbles: true }));
    if (send) {
        box.form?.requestSubmit();
    }
}

// Handles a key while the menu is open. Returns whether it did.
function commandKey(box, event) {
    const state = commandState(box);
    if (state.shown.length === 0 || event.isComposing) {
        return false;
    }

    switch (event.key) {
        case 'ArrowDown':
        case 'ArrowUp':
            state.index = (state.index + (event.key === 'ArrowDown' ? 1 : -1) + state.shown.length) % state.shown.length;
            drawCommands(box);
            return true;
        case 'Tab':
            if (event.shiftKey) {
                return false;
            }

            acceptCommand(box, false);
            return true;
        case 'Enter':
            if (event.shiftKey) {
                return false;
            }

            acceptCommand(box, true);
            return true;
        case 'Escape':
            closeCommands(box);
            return true;
        default:
            return false;
    }
}

// A form of questions from the agent. Enter in a text box would submit the whole
// form, which with three questions is rarely what was meant after answering the
// first, so it moves on to the next question instead and only submits from the
// last; Ctrl or Cmd+Enter submits from anywhere. The form takes the focus when it
// appears, unless you are typing somewhere else, so it can be answered without
// reaching for the mouse.
function bindQuestions(form) {
    if (!form || form.dataset.bound) {
        return;
    }

    form.dataset.bound = '1';
    form.addEventListener('keydown', (event) => {
        if (event.key !== 'Enter' || event.isComposing || event.shiftKey) {
            return;
        }

        if (event.ctrlKey || event.metaKey) {
            event.preventDefault();
            form.requestSubmit();
            return;
        }

        if (event.target.tagName !== 'INPUT' || !['text', 'number', 'email', 'url', 'date', 'datetime-local'].includes(event.target.type)) {
            return;
        }

        const sets = [...form.querySelectorAll('fieldset')];
        const here = sets.findIndex(set => set.contains(event.target));
        const next = here >= 0 ? sets[here + 1] : null;
        if (next) {
            event.preventDefault();
            next.querySelector('input, select, textarea')?.focus();
        }
    });

    const active = document.activeElement;
    if (!active || active === document.body || !active.matches('input, textarea, select, [contenteditable="true"]')) {
        form.querySelector('fieldset input, fieldset select')?.focus({ preventScroll: true });
    }
}

window.agentsDashboard = {
    bindQuestions: (form) => bindQuestions(form),
    bindComposer: (box) => {
        if (!box || box.dataset.bound) {
            return;
        }

        box.dataset.bound = '1';
        box.addEventListener('input', () => {
            growComposer(box);
            updateCommands(box);
        });
        box.addEventListener('click', () => updateCommands(box));
        box.addEventListener('blur', () => closeCommands(box));
        box.addEventListener('keydown', (event) => {
            if (commandKey(box, event)) {
                event.preventDefault();
                event.stopPropagation();
                return;
            }

            if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
                event.preventDefault();
                box.form?.requestSubmit();
            }
        });
        growComposer(box);
    },
    // The commands the agent takes, and the element to list them in. Sent again
    // whenever the agent lists them afresh.
    setComposerCommands: (box, menu, commands) => {
        if (!box) {
            return;
        }

        const state = commandState(box);
        state.menu = menu;
        state.commands = commands || [];
        menu?.setAttribute('id', state.id);
        box.setAttribute('aria-controls', state.id);
        if (document.activeElement === box) {
            updateCommands(box);
        }
    },
    setComposer: (box, text) => {
        if (box) {
            box.value = text || '';
            growComposer(box);
        }
    },
    // An agent brought into view takes the keyboard, with the caret after any
    // draft. Not while a dialog is up, a menu is open or you are typing
    // somewhere else, such as the editor: the chat is rebuilt for reasons
    // besides a switch. A new agent's panel only takes focus once the monitor
    // lists it, a second or two after it starts, by which time the agent
    // picker may be open again, and taking focus from it leaves the arrows
    // moving the caret instead of walking the list.
    focusComposer: (box) => {
        const at = document.activeElement;
        if (!box || box.disabled || document.querySelector('[aria-modal="true"]')
            || at?.closest?.('.context-menu')
            || (at && at !== box && at.matches?.('input, textarea, select, [contenteditable="true"]'))) {
            return;
        }

        box.focus({ preventScroll: true });
        box.setSelectionRange(box.value.length, box.value.length);
    },
    resetComposer: (box) => {
        if (box) {
            box.value = '';
            closeCommands(box);
            growComposer(box);
            box.focus();
        }
    },
    // Follows the newest message the way a chat does: pinned to the bottom while
    // you are reading the bottom, left alone once you scroll up to read history.
    // Forced when the thread is first opened or you have just sent something.
    //
    // Whether you are pinned is decided by your own scrolling, not measured when
    // new content arrives: by then a long reply has already pushed the bottom
    // out of reach and a distance check would read you as having scrolled away.
    // The observers keep a pinned thread at the bottom through anything that
    // grows it, a reply being written, a folded step opened, the composer
    // getting taller, without waiting for the server to say something changed.
    keepScroll: (element, key) => keepScroll(element, key),
    enhanceMarkdown: (element, reference) => enhanceMarkdown(element, reference),
    watchDiffWindow: (column, reference) => watchDiffWindow(column, reference),
    scrollThread: (thread, force) => {
        if (!thread) {
            return;
        }

        followThread(thread);
        if (force) {
            thread.dataset.pinned = 'true';
        }

        if (thread.dataset.pinned === 'true') {
            thread.scrollTop = thread.scrollHeight;
        }
    },
    // Keeps a context menu inside the window, flipping it to the other side of
    // the pointer where it would run off, and focuses it so Escape closes it.
    fitMenu: (menu) => {
        const r = menu.getBoundingClientRect();
        if (r.right > window.innerWidth - 4) {
            menu.style.left = `${Math.max(4, r.left - r.width)}px`;
        }
        if (r.bottom > window.innerHeight - 4) {
            menu.style.top = `${Math.max(4, r.top - r.height)}px`;
        }
        bindMenuKeys(menu);

        // The checked item (the agent in view, the worktree picked) takes focus,
        // so the arrows start from where you are; otherwise the menu itself does,
        // and the first arrow goes to the first item.
        const checked = menu.querySelector('.context-item[aria-checked="true"]:not(:disabled)');
        (checked || menu).focus();
        checked?.scrollIntoView({ block: 'nearest' });
    },
    // After a menu's items change under it (the agent list's filter), focus
    // goes back to the checked item, or the first, or the menu when none are
    // left, so the next arrow key still has somewhere to start.
    focusMenuItem: (element) => {
        const menu = element?.closest('.context-menu');
        if (!menu) {
            return;
        }

        const target = menu.querySelector('.context-item[aria-checked="true"]:not(:disabled)')
            || menu.querySelector('.context-item[role="menuitemradio"]:not(:disabled)')
            || menu;
        target.focus();
        target.scrollIntoView?.({ block: 'nearest' });
    },
    // Where a menu opened from the keyboard goes: under its button, left edges
    // lined up, as it would be had the button been clicked at its corner.
    anchorBelow: (element) => {
        const r = element.getBoundingClientRect();
        return [r.left, r.bottom + 4];
    },
    copy: async (text) => {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch {
            return false;
        }
    },
    setTitle: (title) => {
        document.title = title;
    },
    // Instant, not smooth: jumping to a file in a large diff can be tens of
    // thousands of pixels, which animates slowly and lands you somewhere you did
    // not watch yourself travel to. Smooth scrolling is also ignored outright in
    // some embedded webviews, so the jump would simply not happen.
    scrollTo: (id, block) => {
        document.getElementById(id)?.scrollIntoView({ block: block ?? 'start' });
    },
    startDiffSelection: (element, reference) => {
        diffOwners.set(element, reference);
        if (wired) {
            return;
        }

        wired = true;
        document.addEventListener('mousedown', onDown, true);
        document.addEventListener('mousemove', onMove, true);
        document.addEventListener('mouseup', onUp, true);
        document.addEventListener('click', onClick, true);
    },
    stopDiffSelection: (element) => {
        if (element) {
            diffOwners.delete(element);
        }

        if (drag && element && element.contains(drag.rows[0]?.element)) {
            drag = null;
        }

        if (drag) {
            return;
        }

        clearInterval(edgeTimer);
        edgeTimer = null;
        clearPreview();
    },
    // A Picker's list, placed under its trigger, or over it when there is more
    // room above. Fixed to the viewport so nothing that scrolls clips it.
    openPicker: (trigger, list, activeId) => {
        if (!trigger || !list) {
            return;
        }

        bindPicker(trigger);
        // Safari does not focus a button on click, and the keyboard follows focus.
        trigger.focus({ preventScroll: true });
        placePicker(trigger, list);
        if (activeId) {
            document.getElementById(activeId)?.scrollIntoView({ block: 'nearest' });
        }
    },
    revealOption: (id) => document.getElementById(id)?.scrollIntoView({ block: 'nearest' }),
    bindPicker: (trigger) => trigger && bindPicker(trigger),
    // A vertical tab list walks with the arrow keys, which would also scroll
    // the page it sits in.
    bindTablist: (list) => list?.addEventListener('keydown', (event) => {
        if (['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) {
            event.preventDefault();
        }
    }),
};

const PICKER_GAP = 4;
const PICKER_MARGIN = 8;
const PICKER_MAX = 320;

function placePicker(trigger, list) {
    const box = trigger.getBoundingClientRect();
    list.style.minWidth = `${box.width}px`;

    const below = window.innerHeight - box.bottom - PICKER_GAP - PICKER_MARGIN;
    const above = box.top - PICKER_GAP - PICKER_MARGIN;
    const wanted = Math.min(list.scrollHeight, PICKER_MAX);
    const up = wanted > below && above > below;

    list.style.maxHeight = `${Math.max(120, Math.min(PICKER_MAX, up ? above : below))}px`;
    list.style.top = up ? '' : `${box.bottom + PICKER_GAP}px`;
    list.style.bottom = up ? `${window.innerHeight - box.top + PICKER_GAP}px` : '';

    const width = list.offsetWidth;
    list.style.left = `${Math.max(PICKER_MARGIN, Math.min(box.left, window.innerWidth - width - PICKER_MARGIN))}px`;
    list.dataset.side = up ? 'up' : 'down';
    list.dataset.placed = 'true';
}

// The arrow keys would scroll whatever is under the trigger as well as move
// through the list; Blazor cannot decide per key whether to prevent that.
function bindPicker(trigger) {
    if (trigger.dataset.pickerBound) {
        return;
    }

    trigger.dataset.pickerBound = 'true';
    trigger.addEventListener('keydown', (event) => {
        if (['ArrowDown', 'ArrowUp', 'Home', 'End', 'PageUp', 'PageDown'].includes(event.key)) {
            event.preventDefault();
        }
    });
}

// Up and down walk a menu's items, wrapping at the ends, and Home and End jump
// to them; Enter is the focused button's own click. Only the menu's items are
// stops, so a button inside an item's note (Remove... on a cleanup offer) is
// left to Tab.
function bindMenuKeys(menu) {
    if (menu.dataset.keysBound) {
        return;
    }

    menu.dataset.keysBound = 'true';
    menu.addEventListener('keydown', (event) => {
        if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) {
            return;
        }

        const items = [...menu.querySelectorAll('.context-item:not(:disabled)')];
        if (items.length === 0) {
            return;
        }

        event.preventDefault();
        const at = items.indexOf(document.activeElement);
        const next = event.key === 'Home' ? 0
            : event.key === 'End' ? items.length - 1
            : at < 0 ? (event.key === 'ArrowDown' ? 0 : items.length - 1)
            : (at + (event.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length;
        items[next].focus();
        items[next].scrollIntoView({ block: 'nearest' });
    });
}

// Tab inside a modal dialog. The window is WebKit, which like Safari tabs only
// between text fields unless the Mac's keyboard navigation setting is on, and
// a dialog such as New agent is mostly buttons (every Picker is one), so Tab
// went from the prompt straight out of it. This walks every control, and wraps
// at the ends so focus stays in the dialog rather than reaching the page behind.
(() => {
    const focusable = 'a[href], button:not(:disabled), input:not(:disabled):not([type="hidden"]), '
        + 'select:not(:disabled), textarea:not(:disabled), [tabindex]:not([tabindex="-1"])';

    document.addEventListener('keydown', (event) => {
        if (event.key !== 'Tab' || event.defaultPrevented || event.ctrlKey || event.metaKey || event.altKey) {
            return;
        }

        const dialogs = document.querySelectorAll('[aria-modal="true"]');
        const dialog = dialogs[dialogs.length - 1];
        if (!dialog) {
            return;
        }

        const stops = [...dialog.querySelectorAll(focusable)]
            .filter((el) => el.getClientRects().length > 0 && !el.closest('[inert]'));
        if (stops.length === 0) {
            return;
        }

        event.preventDefault();
        const at = stops.indexOf(document.activeElement);
        const next = at < 0
            ? (event.shiftKey ? stops.length - 1 : 0)
            : (at + (event.shiftKey ? -1 : 1) + stops.length) % stops.length;
        stops[next].focus();
    });
})();

// Escape while a modal dialog is up closes it wherever focus is. Each dialog
// hears Escape through its own keydown, which only fires while focus is inside
// it, and focus does leave: a question form in the chat behind takes it when it
// appears, and a click on a disabled button or a control that is swapped out
// can drop it on the page. Escape then went to the page and the dialog stayed.
// The key is taken in the capture phase, before the editor behind can eat it,
// and sent again from the dialog, which also gets the focus back.
document.addEventListener('keydown', (event) => {
    if (event.key !== 'Escape' || event.isComposing) {
        return;
    }

    const dialogs = document.querySelectorAll('[aria-modal="true"]');
    const dialog = dialogs[dialogs.length - 1];
    if (!dialog || dialog.contains(event.target)) {
        return;
    }

    event.preventDefault();
    event.stopPropagation();
    dialog.focus({ preventScroll: true });
    dialog.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', code: 'Escape', bubbles: true, cancelable: true }));
}, true);

// Enter on a checkbox ticks it, as Space does. Someone tabbing through a form
// reaches for Enter, and without this it does nothing, or submits the form.
// Ctrl or Cmd+Enter is left alone, so a question form still submits from one.
document.addEventListener('keydown', (event) => {
    const box = event.target;
    if (event.key !== 'Enter' || event.defaultPrevented || event.isComposing
        || event.ctrlKey || event.metaKey || event.altKey || event.shiftKey
        || box.tagName !== 'INPUT' || box.type !== 'checkbox' || box.disabled) {
        return;
    }

    event.preventDefault();
    box.click();
});

// Back and forward through the jumps go to definition has made, on the mouse's
// side buttons as in VS Code. The keys (Ctrl+- and Ctrl+Shift+- by default) are
// bindings like any other, in agentsKeys below. The buttons would otherwise go back in the browser's history, which here means to
// the agent you were on before, so their default is stopped on the press and
// the release alike: which of the two navigates differs between engines.
(() => {
    let reference = null;

    function go(forward) {
        const line = window.agentsEditor?.visibleCaret?.() ?? null;
        reference?.invokeMethodAsync(forward ? 'Forward' : 'Back', line).catch(() => { });
    }

    for (const type of ['mousedown', 'mouseup', 'auxclick']) {
        document.addEventListener(type, (event) => {
            if (!reference || (event.button !== 3 && event.button !== 4)) {
                return;
            }

            event.preventDefault();
            event.stopPropagation();
            if (type === 'mouseup') {
                go(event.button === 4);
            }
        }, true);
    }

    window.agentsDashboard.watchNavigation = (ref) => { reference = ref; };
})();

// Where the borders below keep their sizes. localStorage alone forgets them at
// every start, since the window is served from a new port each time and the
// browser keeps storage per origin, so each size is also sent to the server,
// which writes it to the layout file and hands the saved ones back when a window
// opens. localStorage still answers first, so a reload paints the right sizes
// before the circuit is up.
window.agentsLayout = (() => {
    let owner = null;
    const appliers = {};

    return {
        // A border's module registers how to put a saved size on screen.
        register: (name, apply) => { appliers[name] = apply; },
        watch: (reference, sizes) => {
            owner = reference;
            for (const [name, value] of Object.entries(sizes ?? {})) {
                try {
                    localStorage.setItem('agentsDashboard.' + name, value);
                } catch {
                }

                appliers[name]?.(value);
            }
        },
        // A size set by hand, or null when it was reset.
        saved: (name, value) => {
            owner?.invokeMethodAsync('SaveSize', name, value == null ? null : String(value)).catch(() => {});
        },
        sectionWeights: (side, sections, weights) => {
            owner?.invokeMethodAsync('SaveSectionWeights', side, sections, weights).catch(() => {});
        },
    };
})();

// The borders between the panels, dragged. Done here rather than on the circuit
// for the same reason as line selection: a drag is a stream of moves, and a round
// trip for each would lag behind the pointer. Each size is a CSS variable on the
// root element, not a style on the layout, so a Blazor render never puts it
// back, and it is kept per machine. Double-click puts one back to its default;
// the arrow keys move a focused border, so none of this is mouse-only.
(() => {
    // Which variable each border sets, which way it measures, and its limits.
    // A panel is never so big the editor beside it has no room left.
    const sashes = {
        left: { name: '--wb-left', axis: 'x', min: 160, max: () => window.innerWidth * 0.4 },
        right: { name: '--wb-right', axis: 'x', min: 200, max: () => window.innerWidth * 0.45 },
        bottom: { name: '--wb-bottom', axis: 'y', min: 120, max: () => window.innerHeight - 180 },
    };

    const storageKey = (sash) => 'agentsDashboard.' + sash.name.slice(2);
    const root = document.documentElement;

    function clamp(sash, px) {
        return Math.round(Math.min(Math.max(sash.min, sash.max()), Math.max(sash.min, px)));
    }

    function current(which) {
        const sash = sashes[which];
        const panel = panelOf(which);
        if (panel) {
            const box = panel.getBoundingClientRect();
            return sash.axis === 'x' ? box.width : box.height;
        }

        return parseInt(getComputedStyle(root).getPropertyValue(sash.name), 10) || sash.min;
    }

    // The element whose size the border sets.
    function panelOf(which) {
        return document.querySelector({
            left: '.wb-left',
            right: '.wb-right',
            bottom: '.wb-bottom',
        }[which]);
    }

    function apply(which, px, save) {
        const sash = sashes[which];
        const size = clamp(sash, px);
        root.style.setProperty(sash.name, size + 'px');
        if (save) {
            try {
                localStorage.setItem(storageKey(sash), String(size));
            } catch {
            }

            agentsLayout.saved(sash.name.slice(2), size);
        }
    }

    function reset(which) {
        const sash = sashes[which];
        root.style.removeProperty(sash.name);
        try {
            localStorage.removeItem(storageKey(sash));
        } catch {
        }

        agentsLayout.saved(sash.name.slice(2), null);
    }

    for (const [which, sash] of Object.entries(sashes)) {
        agentsLayout.register(sash.name.slice(2), (value) => {
            const px = parseInt(value, 10);
            if (Number.isFinite(px)) {
                apply(which, px, false);
            }
        });

        try {
            const saved = parseInt(localStorage.getItem(storageKey(sash)) ?? '', 10);
            if (Number.isFinite(saved)) {
                apply(which, saved, false);
            }
        } catch {
        }
    }

    // How far the pointer has moved, turned into the panel's new size. The left
    // panel grows as the pointer goes right; the right panel and the bottom panel
    // grow as it goes left or up, since they sit on the far side of their border.
    function sizeFrom(which, start, startSize, event) {
        const sash = sashes[which];
        const delta = sash.axis === 'x' ? event.clientX - start : event.clientY - start;
        return which === 'left' ? startSize + delta : startSize - delta;
    }

    document.addEventListener('pointerdown', (event) => {
        const handle = event.target.closest?.('.sash[data-sash]');
        if (!handle || event.button !== 0) {
            return;
        }

        const which = handle.dataset.sash;
        const sash = sashes[which];
        if (!sash) {
            return;
        }

        event.preventDefault();
        const start = sash.axis === 'x' ? event.clientX : event.clientY;
        const startSize = current(which);
        const holding = sash.axis === 'x' ? 'resizing-col' : 'resizing-row';

        handle.setPointerCapture(event.pointerId);
        handle.classList.add('dragging');
        document.body.classList.add(holding);

        const move = (e) => apply(which, sizeFrom(which, start, startSize, e), false);
        const up = () => {
            handle.removeEventListener('pointermove', move);
            handle.removeEventListener('pointerup', up);
            handle.removeEventListener('pointercancel', up);
            handle.classList.remove('dragging');
            document.body.classList.remove(holding);
            apply(which, current(which), true);
        };

        handle.addEventListener('pointermove', move);
        handle.addEventListener('pointerup', up);
        handle.addEventListener('pointercancel', up);
    });

    document.addEventListener('dblclick', (event) => {
        const handle = event.target.closest?.('.sash[data-sash]');
        if (handle && sashes[handle.dataset.sash]) {
            reset(handle.dataset.sash);
        }
    });

    document.addEventListener('keydown', (event) => {
        const handle = event.target.closest?.('.sash[data-sash]');
        const which = handle?.dataset.sash;
        const sash = sashes[which];
        if (!sash) {
            return;
        }

        const step = event.shiftKey ? 64 : 16;
        const grow = which === 'left' ? 'ArrowRight' : sash.axis === 'x' ? 'ArrowLeft' : 'ArrowUp';
        const shrink = which === 'left' ? 'ArrowLeft' : sash.axis === 'x' ? 'ArrowRight' : 'ArrowDown';
        if (event.key === grow || event.key === shrink) {
            event.preventDefault();
            apply(which, current(which) + (event.key === grow ? step : -step), true);
        }
    });

    // A window made smaller can leave a saved size past what now fits.
    window.addEventListener('resize', () => {
        for (const [which, sash] of Object.entries(sashes)) {
            if (root.style.getPropertyValue(sash.name)) {
                apply(which, parseInt(root.style.getPropertyValue(sash.name), 10), false);
            }
        }
    });
})();

// A dragged editor tab carries nothing the page reads: the server knows which
// tab it is. WebKit will not start a drag with no data, though, and left to
// guess, the drag would carry the tab's text for Monaco to paste if dropped on
// an editor. An empty string and a move is all it needs.
document.addEventListener('dragstart', (event) => {
    if (event.target.closest?.('.etab, .ptab') && event.dataTransfer) {
        event.dataTransfer.setData('text/plain', '');
        event.dataTransfer.effectAllowed = 'move';
    }
});

// The border between the two sides of a split editor. A share of the editor's
// width rather than pixels, unlike the panel borders, so the sides keep their
// proportion as the window or the panels around them change size. Kept per
// machine; double-click evens it, and the arrow keys move it when focused.
(() => {
    const name = '--wb-split';
    const storageKey = 'agentsDashboard.wb-split';
    const root = document.documentElement;
    // Neither side is squeezed below this, so its tab strip and editor stay usable.
    const minSide = 200;

    function clamp(percent, width) {
        const floor = width > 0 ? Math.min(50, (minSide / width) * 100) : 15;
        return Math.min(100 - floor, Math.max(floor, percent));
    }

    function current() {
        return parseFloat(getComputedStyle(root).getPropertyValue(name)) || 50;
    }

    function apply(percent, width, save) {
        const share = Math.round(clamp(percent, width) * 10) / 10;
        root.style.setProperty(name, share + '%');
        if (save) {
            try {
                localStorage.setItem(storageKey, String(share));
            } catch {
            }

            agentsLayout.saved(name.slice(2), share);
        }
    }

    agentsLayout.register(name.slice(2), (value) => {
        const saved = parseFloat(value);
        if (Number.isFinite(saved)) {
            apply(saved, 0, false);
        }
    });

    try {
        const saved = parseFloat(localStorage.getItem(storageKey) ?? '');
        if (Number.isFinite(saved)) {
            apply(saved, 0, false);
        }
    } catch {
    }

    document.addEventListener('pointerdown', (event) => {
        const handle = event.target.closest?.('[data-editor-split]');
        if (!handle || event.button !== 0) {
            return;
        }

        event.preventDefault();
        const area = handle.parentElement.getBoundingClientRect();
        handle.setPointerCapture(event.pointerId);
        handle.classList.add('dragging');
        document.body.classList.add('resizing-col');

        const move = (e) => apply(((e.clientX - area.left) / area.width) * 100, area.width, false);
        const up = () => {
            handle.removeEventListener('pointermove', move);
            handle.removeEventListener('pointerup', up);
            handle.removeEventListener('pointercancel', up);
            handle.classList.remove('dragging');
            document.body.classList.remove('resizing-col');
            apply(current(), area.width, true);
        };

        handle.addEventListener('pointermove', move);
        handle.addEventListener('pointerup', up);
        handle.addEventListener('pointercancel', up);
    });

    document.addEventListener('dblclick', (event) => {
        if (event.target.closest?.('[data-editor-split]')) {
            apply(50, 0, true);
        }
    });

    document.addEventListener('keydown', (event) => {
        const handle = event.target.closest?.('[data-editor-split]');
        if (!handle || (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight')) {
            return;
        }

        event.preventDefault();
        const step = (event.shiftKey ? 8 : 2) * (event.key === 'ArrowRight' ? 1 : -1);
        apply(current() + step, handle.parentElement.getBoundingClientRect().width, true);
    });
})();

// The borders between the sections of a panel. Sections share the panel's height
// by flex-grow, so a drag writes each open section's height in pixels as its
// grow, which keeps every other section where it was, and sends them all to the
// server on release. The server renders the same numbers back, so a redraw does
// not undo the drag. Double-click shares the height evenly between the two.
(() => {
    const minHeight = 60;

    function open(handle) {
        return [...handle.closest('[data-panel]').querySelectorAll(':scope > .psections > .psection:not(.collapsed)')];
    }

    function report(handle, sections) {
        agentsLayout.sectionWeights(
            handle.closest('[data-panel]').dataset.panel,
            sections.map((s) => parseInt(s.dataset.section, 10)),
            sections.map((s) => parseFloat(s.style.flexGrow) || 1));
    }

    function neighbours(handle) {
        const above = handle.previousElementSibling;
        const below = handle.nextElementSibling;
        return above?.classList.contains('psection') && below?.classList.contains('psection') ? [above, below] : null;
    }

    document.addEventListener('pointerdown', (event) => {
        const handle = event.target.closest?.('.section-sash');
        const pair = handle && neighbours(handle);
        if (!pair || event.button !== 0) {
            return;
        }

        event.preventDefault();
        const sections = open(handle);
        for (const section of sections) {
            section.style.flexGrow = String(Math.round(section.getBoundingClientRect().height));
        }

        const [above, below] = pair;
        const start = event.clientY;
        const heights = [above.getBoundingClientRect().height, below.getBoundingClientRect().height];
        handle.setPointerCapture(event.pointerId);
        handle.classList.add('dragging');
        document.body.classList.add('resizing-row');

        const move = (e) => {
            const total = heights[0] + heights[1];
            const top = Math.min(total - minHeight, Math.max(minHeight, heights[0] + e.clientY - start));
            above.style.flexGrow = String(Math.round(top));
            below.style.flexGrow = String(Math.round(total - top));
        };
        const up = () => {
            handle.removeEventListener('pointermove', move);
            handle.removeEventListener('pointerup', up);
            handle.removeEventListener('pointercancel', up);
            handle.classList.remove('dragging');
            document.body.classList.remove('resizing-row');
            report(handle, sections);
        };

        handle.addEventListener('pointermove', move);
        handle.addEventListener('pointerup', up);
        handle.addEventListener('pointercancel', up);
    });

    document.addEventListener('dblclick', (event) => {
        const handle = event.target.closest?.('.section-sash');
        const pair = handle && neighbours(handle);
        if (!pair) {
            return;
        }

        const sections = open(handle);
        for (const section of sections) {
            section.style.flexGrow = String(Math.round(section.getBoundingClientRect().height));
        }

        const share = String((parseFloat(pair[0].style.flexGrow) + parseFloat(pair[1].style.flexGrow)) / 2);
        pair[0].style.flexGrow = share;
        pair[1].style.flexGrow = share;
        report(handle, sections);
    });
})();

// Where a dragged panel view would land is tinted here rather than on the
// circuit, which would take a round trip per move. Followed by dragover, which
// fires on whatever is under the pointer, rather than dragenter and dragleave,
// which a tab's own label would set off in pairs.
(() => {
    let over = null;
    const mark = (zone) => {
        if (zone !== over) {
            over?.classList.remove('over');
            zone?.classList.add('over');
            over = zone;
        }
    };

    document.addEventListener('dragover', (event) => mark(event.target.closest?.('[data-view-drop]') ?? null));
    document.addEventListener('drop', () => mark(null));
    document.addEventListener('dragend', () => mark(null));
})();

// Keyboard shortcuts. The server owns the map (defaults plus what was changed on
// the Settings page) and sends it here as chord -> command id; this listens on
// the whole document in the capture phase, so a shortcut works wherever focus
// is, Monaco included, and Monaco never sees a key that ran a command. Chords are
// written the way KeyChord in Core writes them, from the physical key
// (event.code), so Shift+- stays "shift+-" rather than becoming "_".
window.agentsKeys = (() => {
    const isMac = /Mac|iPhone|iPad/.test(navigator.platform);
    let bindings = new Map();
    let reference = null;

    const named = {
        Minus: '-', Equal: '=', BracketLeft: '[', BracketRight: ']', Backslash: '\\',
        Semicolon: ';', Quote: "'", Comma: ',', Period: '.', Slash: '/', Backquote: '`',
        ArrowUp: 'up', ArrowDown: 'down', ArrowLeft: 'left', ArrowRight: 'right',
        Enter: 'enter', NumpadEnter: 'enter', Escape: 'escape', Space: 'space', Tab: 'tab',
        Backspace: 'backspace', Delete: 'delete', Insert: 'insert', Home: 'home', End: 'end',
        PageUp: 'pageup', PageDown: 'pagedown',
    };
    const modifiers = new Set(['Shift', 'Control', 'Alt', 'Meta', 'OS', 'CapsLock', 'Fn']);

    function keyName(event) {
        const code = event.code || '';
        if (/^Key[A-Z]$/.test(code)) {
            return code.slice(3).toLowerCase();
        }
        if (/^Digit[0-9]$/.test(code)) {
            return code.slice(5);
        }
        if (/^F[0-9]{1,2}$/.test(code) || /^Numpad[0-9]$/.test(code)) {
            return code.toLowerCase();
        }
        if (named[code]) {
            return named[code];
        }
        return (event.key || '').toLowerCase() || null;
    }

    function chord(event) {
        if (modifiers.has(event.key)) {
            return null;
        }
        const key = keyName(event);
        if (!key) {
            return null;
        }
        const parts = [];
        if (event.ctrlKey) parts.push('ctrl');
        if (event.shiftKey) parts.push('shift');
        if (event.altKey) parts.push('alt');
        if (event.metaKey) parts.push(isMac ? 'cmd' : 'meta');
        parts.push(key);
        return parts.join('+');
    }

    document.addEventListener('keydown', (event) => {
        if (!reference || event.isComposing || event.target.closest?.('[data-keybinding-recorder]')) {
            return;
        }

        const pressed = chord(event);
        const id = pressed && bindings.get(pressed);
        if (!id) {
            return;
        }

        event.preventDefault();
        event.stopPropagation();
        const line = window.agentsEditor?.visibleCaret?.() ?? null;
        reference.invokeMethodAsync('RunCommand', id, line).catch(() => { });
    }, true);

    // Remembers what had focus when Go to File opened, so closing it without
    // opening anything puts the caret back where it was, as VS Code does.
    let returnFocus = null;

    return {
        isMac,
        watch: (ref, map) => {
            reference = ref;
            bindings = new Map(Object.entries(map || {}));
        },
        setBindings: (map) => {
            bindings = new Map(Object.entries(map || {}));
        },

        // The key recorder on the Keyboard shortcuts page: every key is taken,
        // reported as a chord, and Enter and Escape (alone) finish or cancel.
        record: (element, ref) => {
            if (!element || element.dataset.recorderBound) {
                return;
            }
            element.dataset.recorderBound = 'true';
            element.addEventListener('keydown', (event) => {
                event.preventDefault();
                event.stopPropagation();
                const plain = !event.ctrlKey && !event.metaKey && !event.altKey && !event.shiftKey;
                if (plain && event.key === 'Escape') {
                    ref.invokeMethodAsync('CancelRecording').catch(() => { });
                } else if (plain && event.key === 'Enter') {
                    ref.invokeMethodAsync('ConfirmRecording').catch(() => { });
                } else {
                    const pressed = chord(event);
                    if (pressed) {
                        ref.invokeMethodAsync('Recorded', pressed).catch(() => { });
                    }
                }
            });
            element.focus();
        },

        // Go to File's input. The keys that move through the list are stopped
        // here, since Blazor cannot decide per key whether to prevent the caret
        // moving, and sent up with the text as it stands, so a quick Enter after
        // typing never opens what the previous keystroke matched.
        bindQuickOpen: (input, ref, selectAll) => {
            if (!input) {
                return;
            }
            if (!input.dataset.quickOpenBound) {
                input.dataset.quickOpenBound = 'true';
                input.addEventListener('keydown', (event) => {
                    let key = null;
                    if (['ArrowDown', 'ArrowUp', 'PageDown', 'PageUp', 'Enter', 'NumpadEnter', 'Escape'].includes(event.key)) {
                        key = event.key === 'NumpadEnter' ? 'Enter' : event.key;
                    } else if (isMac && event.ctrlKey && !event.metaKey && (event.code === 'KeyN' || event.code === 'KeyP')) {
                        // Emacs keys, as the macOS text system and VS Code's list both take them.
                        key = event.code === 'KeyN' ? 'ArrowDown' : 'ArrowUp';
                    }
                    if (!key || event.isComposing) {
                        return;
                    }
                    event.preventDefault();
                    event.stopPropagation();
                    const side = isMac ? event.metaKey : event.ctrlKey;
                    ref.invokeMethodAsync('OnKey', key, side, input.value).catch(() => { });
                });
            }
            // Centred on the title bar's search box, which centres in the bar's
            // free space rather than on the window, so the box opens over it.
            const dialog = input.closest('.qo');
            const anchor = document.querySelector('.command-center');
            if (dialog && anchor && anchor.offsetWidth > 0) {
                const rect = anchor.getBoundingClientRect();
                const half = dialog.offsetWidth / 2;
                const centre = Math.min(window.innerWidth - half - 8, Math.max(half + 8, rect.left + rect.width / 2));
                dialog.style.left = centre + 'px';
            }
            if (document.activeElement !== input) {
                returnFocus = document.activeElement;
                input.focus();
            }
            if (selectAll) {
                input.select();
            } else {
                input.setSelectionRange(input.value.length, input.value.length);
            }
        },
        revealActive: (list) => {
            list?.querySelector('.qo-item.active')?.scrollIntoView({ block: 'nearest' });
        },
        restoreFocus: () => {
            const target = returnFocus;
            returnFocus = null;
            if (target && target.isConnected && typeof target.focus === 'function') {
                target.focus();
            }
        },
        forgetFocus: () => { returnFocus = null; },
    };
})();
