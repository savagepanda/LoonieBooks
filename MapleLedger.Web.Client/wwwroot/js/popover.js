// Keeps a position: fixed panel next to the button that opened it, so it isn't clipped by a
// scrolling container (tables sit in .table-responsive). Flips above the button when there's no
// room below, stays inside the viewport, and follows scrolling and resizing until disposed.
export function anchor(button, panel) {
    const gap = 4, margin = 8;
    const place = () => {
        const r = button.getBoundingClientRect();
        const vw = document.documentElement.clientWidth, vh = document.documentElement.clientHeight;
        const w = panel.offsetWidth, h = panel.offsetHeight;
        const left = Math.max(margin, Math.min(r.left, vw - w - margin));
        let top = r.bottom + gap;
        if (top + h > vh - margin && r.top - gap - h >= margin) top = r.top - gap - h;
        else top = Math.max(margin, Math.min(top, vh - h - margin));
        panel.style.left = `${left}px`;
        panel.style.top = `${top}px`;
        panel.style.visibility = 'visible';
    };

    place();
    addEventListener('scroll', place, true);
    addEventListener('resize', place);
    return {
        dispose() {
            removeEventListener('scroll', place, true);
            removeEventListener('resize', place);
        },
    };
}

// A mouse or trackpad: a search box can take focus without popping up an on-screen keyboard.
export function hasFinePointer() {
    return matchMedia('(pointer: fine)').matches;
}
