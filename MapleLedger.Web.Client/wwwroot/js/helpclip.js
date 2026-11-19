// Plays a help clip (Help/HelpClip.razor) while it's on screen and pauses it once it isn't, so a page
// of open articles only downloads the clips someone is looking at. A click pauses or resumes it. With
// reduced motion asked for, nothing starts by itself: the browser's own controls are shown instead.
export function attach(video) {
    const frame = video.parentElement;
    const reduced = matchMedia('(prefers-reduced-motion: reduce)').matches;
    let pausedByReader = false;

    const sync = () => frame.classList.toggle('is-playing', !video.paused);
    video.addEventListener('play', sync);
    video.addEventListener('pause', sync);
    const unsync = () => { video.removeEventListener('play', sync); video.removeEventListener('pause', sync); };

    if (reduced) {
        video.controls = true;
        return { detach: unsync };
    }

    const onClick = () => {
        if (video.paused) { pausedByReader = false; video.play().catch(() => { }); }
        else { pausedByReader = true; video.pause(); }
    };
    frame.addEventListener('click', onClick);

    const observer = new IntersectionObserver(entries => {
        for (const e of entries) {
            if (e.isIntersecting && e.intersectionRatio >= 0.6) {
                if (!pausedByReader) video.play().catch(() => { });
            } else if (!video.paused) {
                video.pause();
            }
        }
    }, { threshold: [0, 0.6] });
    observer.observe(video);

    return {
        detach() {
            observer.disconnect();
            frame.removeEventListener('click', onClick);
            unsync();
        }
    };
}
