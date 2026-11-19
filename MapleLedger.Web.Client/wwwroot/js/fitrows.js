// Shows only the table rows that fit whole inside a height-limited box (the dashboard's
// transaction panel), hiding the rest, so the box never needs a scroll bar; "See full list" has
// them all. Where the box isn't height-limited (the narrow, stacked layout) it shows the first
// `max` rows instead. Re-fits when the box resizes or its rows change, until disposed.
export function fit(box, max) {
    const apply = () => {
        const rows = [...box.querySelectorAll(':scope > table > tbody > tr')];
        rows.forEach(r => { r.hidden = false; });
        if (getComputedStyle(box).position !== 'absolute') {
            rows.forEach((r, i) => { r.hidden = i >= max; });
            return;
        }

        const bottom = box.getBoundingClientRect().top + box.clientHeight;
        let shown = 0;
        for (const row of rows) {
            // Always at least one row, even in a box too short for it.
            if (shown > 0 && row.getBoundingClientRect().bottom > bottom + 0.5) break;
            shown++;
        }

        rows.forEach((r, i) => { r.hidden = i >= shown; });
    };
    // Hiding rows changes neither the box's size (it's pinned to its container) nor its children,
    // so neither observer fires again from a fit.
    const resized = new ResizeObserver(apply);
    resized.observe(box);
    // Rows added or removed (a new list); attribute changes aren't watched.
    const changed = new MutationObserver(apply);
    changed.observe(box, { childList: true, subtree: true });
    apply();

    return {
        dispose() {
            resized.disconnect();
            changed.disconnect();
        },
    };
}
