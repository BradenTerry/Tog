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
