// Turning the mouse wheel over an element steps a <select> inside it to the next or previous option, as
// if picked from the list (a change event, so Blazor's @onchange runs). One notch is one step; a
// trackpad's stream of small deltas adds up to a step, and deltas just after a step are dropped so its
// momentum doesn't run through the whole list. At the first or last option the wheel scrolls the page as usual.
// Ctrl+wheel (zoom) and sideways scrolling are left alone.
export function attach(area, select) {
    const threshold = 40, cooldownMs = 200;
    let pending = 0, lastStep = 0;

    const onWheel = e => {
        // A wheel event that can't be cancelled is part of a page scroll already under way (the browser
        // latched it to the page): the pointer just passed over the chart, so leave the page scrolling.
        if (!e.cancelable || e.ctrlKey || Math.abs(e.deltaX) > Math.abs(e.deltaY) || e.deltaY === 0) return;
        const dir = Math.sign(e.deltaY);
        const next = select.selectedIndex + dir;
        if (select.disabled || next < 0 || next >= select.options.length) { pending = 0; return; }

        e.preventDefault();
        const now = performance.now();
        if (now - lastStep < cooldownMs) { pending = 0; return; }
        if (Math.sign(pending) !== dir) pending = 0;
        // Lines or pages (Firefox, some mice) count as whole notches.
        pending += e.deltaMode === 0 ? e.deltaY : dir * threshold;
        if (Math.abs(pending) < threshold) return;

        pending = 0;
        lastStep = now;
        select.selectedIndex = next;
        select.dispatchEvent(new Event('change', { bubbles: true }));
    };
    area.addEventListener('wheel', onWheel, { passive: false });

    return { detach: () => area.removeEventListener('wheel', onWheel) };
}
