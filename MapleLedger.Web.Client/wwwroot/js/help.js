// The help page: brings an article opened from a link (help?a=...) to the top of the window.
export function scrollToId(id) {
    document.getElementById(id)?.scrollIntoView({ block: 'start' });
}

// A mouse or trackpad: the search box can take focus without popping up an on-screen keyboard.
export function hasFinePointer() {
    return matchMedia('(pointer: fine)').matches;
}
