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
    return Number.isNaN(line) ? null : { file: row.dataset.file, side: row.dataset.side, line };
}

function clearPreview() {
    for (const row of document.querySelectorAll('.diff-line.picking')) {
        row.classList.remove('picking');
    }
}

function paint(to) {
    if (!drag) {
        return;
    }

    const low = Math.min(drag.from, to);
    const high = Math.max(drag.from, to);

    for (const row of drag.rows) {
        row.element.classList.toggle('picking', row.line >= low && row.line <= high);
    }
}

// Only the lines that can take part in this drag: the same file, and the same
// side of it. Collected once so a move does not walk the whole page.
function rowsFor(file, side) {
    const rows = [];
    for (const element of document.querySelectorAll('.diff-line')) {
        if (element.dataset.file === file && element.dataset.side === side) {
            const line = Number.parseInt(element.dataset.line, 10);
            if (!Number.isNaN(line)) {
                rows.push({ element, line });
            }
        }
    }

    return rows;
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

    drag = {
        file: start.file,
        side: start.side,
        from: start.line,
        to: start.line,
        extend: event.shiftKey,
        rows: rowsFor(start.file, start.side),
    };

    scroller = event.target.closest('.main') ?? document.scrollingElement;
    paint(start.line);
}

function onMove(event) {
    if (!drag) {
        return;
    }

    const over = lineOf(document.elementFromPoint(event.clientX, event.clientY) ?? event.target);
    if (over && over.file === drag.file && over.side === drag.side) {
        drag.to = over.line;
        paint(over.line);
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

    const { file, side, from, to, extend } = drag;
    drag = null;
    clearInterval(edgeTimer);
    edgeTimer = null;

    // The preview is dropped before the server is told, so the class it renders
    // is the only one on the row afterwards and the two cannot disagree.
    clearPreview();

    dotnet?.invokeMethodAsync(
        'SelectLines', file, side, Math.min(from, to), Math.max(from, to), extend);
}

// A mouse click on the gutter has already been handled here, so it must not also
// reach the server's own click handler. A keyboard activation reports a detail of
// zero and is let through, which is what keeps the gutter usable without a mouse.
function onClick(event) {
    if (event.detail > 0 && event.target.closest('.ln-pick')) {
        event.stopPropagation();
    }
}

window.agentsDashboard = {
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
