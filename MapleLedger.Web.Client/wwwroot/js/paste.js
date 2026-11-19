// Lets a page take a picture pasted with Ctrl+V, such as a screenshot from Win+Shift+S or the
// Snipping Tool. Text pastes are left alone, so pasting a copied table works as before.

const handlers = new Map();
let nextId = 1;

// `target` is a .NET object with an OnImagePasted(byte[] bytes, string contentType) method.
export function listen(target) {
    const handler = async e => {
        const items = [...(e.clipboardData?.items ?? [])];
        const image = items.find(i => i.kind === 'file' && i.type.startsWith('image/'));
        // Copying from a web page can bring an image along with the text: the text wins.
        if (!image || items.some(i => i.type === 'text/plain')) return;

        e.preventDefault();
        const file = image.getAsFile();
        if (!file) return;
        const bytes = new Uint8Array(await file.arrayBuffer());
        target.invokeMethodAsync('OnImagePasted', bytes, file.type).catch(() => { });
    };

    document.addEventListener('paste', handler);
    const id = nextId++;
    handlers.set(id, handler);
    return id;
}

export function stop(id) {
    const handler = handlers.get(id);
    if (handler) document.removeEventListener('paste', handler);
    handlers.delete(id);
}
