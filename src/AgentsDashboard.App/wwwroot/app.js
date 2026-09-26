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
let dotnet = null;
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
// against: the agent view's pane, not the window.
function scrollParent(element) {
    for (let node = element.parentElement; node && node !== document.body; node = node.parentElement) {
        const overflow = getComputedStyle(node).overflowY;
        if (overflow === 'auto' || overflow === 'scroll') {
            return node;
        }
    }

    return null;
}

// Tells the Changes tab which files are within a screen or so of the viewport,
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
// to the Files tab. Each step marks what it has done, so running this again
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

// Every line of this file, both sides, in the order they are drawn: the drag
// walks rows rather than line numbers, because the two sides number themselves
// independently. Collected once so a move does not walk the whole page.
function rowsFor(file) {
    const rows = [];
    for (const element of document.querySelectorAll('.diff-line')) {
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
    if (!start) {
        return;
    }

    // Stops the browser starting a text selection across the diff as you drag.
    event.preventDefault();

    drag = { file: start.file, rows: rowsFor(start.file), from: 0, to: 0, extend: event.shiftKey };

    const anchor = indexOf(start.row);
    if (anchor < 0) {
        drag = null;
        return;
    }

    drag.from = anchor;
    drag.to = anchor;

    scroller = event.target.closest('.main') ?? document.scrollingElement;
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
    const picked = span(drag.to);
    drag = null;
    clearInterval(edgeTimer);
    edgeTimer = null;

    // The preview is dropped before the server is told, so the class it renders
    // is the only one on the row afterwards and the two cannot disagree.
    clearPreview();

    const lines = picked.rows.map(row => row.line);
    dotnet?.invokeMethodAsync(
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
    // Small per-machine preferences, such as whether the agent list is folded.
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
    startDiffSelection: (reference) => {
        dotnet = reference;
        if (wired) {
            return;
        }

        wired = true;
        document.addEventListener('mousedown', onDown, true);
        document.addEventListener('mousemove', onMove, true);
        document.addEventListener('mouseup', onUp, true);
        document.addEventListener('click', onClick, true);
    },
    stopDiffSelection: () => {
        dotnet = null;
        drag = null;
        clearInterval(edgeTimer);
        edgeTimer = null;
        clearPreview();
    },
};

// The agent list's width, dragged by the handle on its right edge. Done here
// rather than on the circuit for the same reason as line selection: a drag is a
// stream of moves, and a round trip for each would lag behind the pointer. The
// width is a CSS variable on the root element, not a style on the shell, so a
// Blazor render never puts it back, and it is kept per machine like the fold.
(() => {
    const key = 'agentsDashboard.sidebarWidth';
    const fallback = 300;
    const min = 200;

    // Never so wide the page beside it has no room left.
    const max = () => Math.max(min, Math.min(640, Math.round(window.innerWidth * 0.5)));
    const clamp = (width) => Math.min(max(), Math.max(min, Math.round(width)));

    function current() {
        const set = parseInt(getComputedStyle(document.documentElement).getPropertyValue('--sidebar-width'), 10);
        return Number.isFinite(set) ? set : fallback;
    }

    function apply(width, save) {
        const px = clamp(width);
        document.documentElement.style.setProperty('--sidebar-width', px + 'px');
        document.querySelectorAll('.sidebar-resize').forEach(handle => {
            handle.setAttribute('aria-valuenow', String(px));
            handle.setAttribute('aria-valuemin', String(min));
            handle.setAttribute('aria-valuemax', String(max()));
        });

        if (save) {
            try {
                localStorage.setItem(key, String(px));
            } catch {
            }
        }
    }

    function reset() {
        document.documentElement.style.removeProperty('--sidebar-width');
        try {
            localStorage.removeItem(key);
        } catch {
        }
    }

    try {
        const saved = parseInt(localStorage.getItem(key) ?? '', 10);
        if (Number.isFinite(saved)) {
            apply(saved, false);
        }
    } catch {
    }

    document.addEventListener('pointerdown', (event) => {
        const handle = event.target.closest?.('.sidebar-resize');
        if (!handle || event.button !== 0) {
            return;
        }

        event.preventDefault();
        const sidebar = handle.closest('.sidebar');
        const left = sidebar ? sidebar.getBoundingClientRect().left : 0;
        handle.setPointerCapture(event.pointerId);
        document.body.classList.add('resizing-sidebar');

        const move = (e) => apply(e.clientX - left, false);
        const up = () => {
            handle.removeEventListener('pointermove', move);
            handle.removeEventListener('pointerup', up);
            handle.removeEventListener('pointercancel', up);
            document.body.classList.remove('resizing-sidebar');
            apply(current(), true);
        };

        handle.addEventListener('pointermove', move);
        handle.addEventListener('pointerup', up);
        handle.addEventListener('pointercancel', up);
    });

    // Double-click puts it back to the default width.
    document.addEventListener('dblclick', (event) => {
        if (event.target.closest?.('.sidebar-resize')) {
            reset();
        }
    });

    // The arrow keys move it too, so the handle is not mouse-only.
    document.addEventListener('keydown', (event) => {
        if (!event.target.closest?.('.sidebar-resize')) {
            return;
        }

        const step = event.shiftKey ? 64 : 16;
        if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
            event.preventDefault();
            apply(current() + (event.key === 'ArrowRight' ? step : -step), true);
        } else if (event.key === 'Home' || event.key === 'End') {
            event.preventDefault();
            apply(event.key === 'End' ? max() : min, true);
        }
    });

    // A window made narrower can leave a saved width past half of it.
    window.addEventListener('resize', () => {
        if (document.documentElement.style.getPropertyValue('--sidebar-width')) {
            apply(current(), false);
        }
    });
})();
