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

    const stick = () => {
        if (thread.dataset.pinned === 'true') {
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
    for (const type of ['wheel', 'touchmove', 'keydown']) {
        thread.addEventListener(type, touched, { passive: true });
    }

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
            const dark = window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
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

window.agentsDashboard = {
    bindComposer: (box) => {
        if (!box || box.dataset.bound) {
            return;
        }

        box.dataset.bound = '1';
        box.addEventListener('input', () => growComposer(box));
        box.addEventListener('keydown', (event) => {
            if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
                event.preventDefault();
                box.form?.requestSubmit();
            }
        });
        growComposer(box);
    },
    setComposer: (box, text) => {
        if (box) {
            box.value = text || '';
            growComposer(box);
        }
    },
    resetComposer: (box) => {
        if (box) {
            box.value = '';
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
    // Small per-machine preferences, such as which panels are showing.
    // Storage can be unavailable, in which case the default simply stands.
    getPref: (key) => {
        try {
            return localStorage.getItem('agentsDashboard.' + key);
        } catch {
            return null;
        }
    },
    setPref: (key, value) => {
        try {
            localStorage.setItem('agentsDashboard.' + key, value);
        } catch {
        }
    },
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
        menu.focus();
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
    scrollTo: (id) => {
        document.getElementById(id)?.scrollIntoView({ block: 'start' });
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
};

// Back and forward through the jumps go to definition has made, on the mouse's
// side buttons as in VS Code, and on its keys (Ctrl+- and Ctrl+Shift+-). The
// buttons would otherwise go back in the browser's history, which here means to
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

    document.addEventListener('keydown', (event) => {
        if (reference && event.ctrlKey && !event.metaKey && !event.altKey && event.code === 'Minus') {
            event.preventDefault();
            event.stopPropagation();
            go(event.shiftKey);
        }
    }, true);

    window.agentsDashboard.watchNavigation = (ref) => { reference = ref; };
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
        agents: { name: '--wb-agents', axis: 'x', min: 180, max: () => window.innerWidth * 0.4 },
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
            agents: '.agent-list',
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
        }
    }

    function reset(which) {
        const sash = sashes[which];
        root.style.removeProperty(sash.name);
        try {
            localStorage.removeItem(storageKey(sash));
        } catch {
        }
    }

    for (const [which, sash] of Object.entries(sashes)) {
        try {
            const saved = parseInt(localStorage.getItem(storageKey(sash)) ?? '', 10);
            if (Number.isFinite(saved)) {
                apply(which, saved, false);
            }
        } catch {
        }
    }

    // How far the pointer has moved, turned into the panel's new size. The left
    // panel grows as the pointer goes right; the right panel, the agent list and
    // the bottom panel grow as it goes left or up, since they sit on the far side
    // of their border.
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
