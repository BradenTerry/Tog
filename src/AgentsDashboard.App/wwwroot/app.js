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
function growComposer(box) {
    box.style.height = 'auto';
    box.style.height = Math.min(box.scrollHeight, 240) + 'px';
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
    scrollThread: (thread, force) => {
        if (!thread) {
            return;
        }

        const nearBottom = thread.scrollHeight - thread.scrollTop - thread.clientHeight < 120;
        if (force || nearBottom) {
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
