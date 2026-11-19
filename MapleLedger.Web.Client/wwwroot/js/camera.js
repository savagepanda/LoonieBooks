// Webcam capture for receipt photos. Frames never leave the browser from here: they come back to
// .NET as JPEG bytes and are stored with the ledger like any other receipt.

let stream = null;

// Starts the camera (the back camera on a phone) at the highest resolution it offers up to 4K,
// since small receipt print needs every pixel. Returns the actual resolution, e.g. "1920x1080".
export async function start(video) {
    stop(video);
    if (!navigator.mediaDevices?.getUserMedia) {
        throw new Error('unsupported');
    }

    try {
        stream = await navigator.mediaDevices.getUserMedia({
            audio: false,
            video: { facingMode: { ideal: 'environment' }, width: { ideal: 3840 }, height: { ideal: 2160 } },
        });
    } catch (e) {
        // Surface the DOMException name ("NotAllowedError", "NotFoundError", ...) for a friendly message.
        throw new Error(e?.name || 'failed');
    }

    video.srcObject = stream;
    await video.play();
    const settings = stream.getVideoTracks()[0]?.getSettings() ?? {};
    return `${settings.width ?? video.videoWidth}x${settings.height ?? video.videoHeight}`;
}

export function stop(video) {
    stream?.getTracks().forEach(track => track.stop());
    stream = null;
    if (video) video.srcObject = null;
}

// Grabs the current frame as a JPEG, scaled so its longer side is at most maxSide pixels.
export async function capture(video, maxSide, quality) {
    if (!video.videoWidth) throw new Error('not-ready');
    return toJpeg(video, video.videoWidth, video.videoHeight, maxSide, quality);
}

// Re-encodes a photo the browser can decode (JPEG, PNG, WebP) as a smaller JPEG.
export async function shrink(bytes, type, maxSide, quality) {
    const bitmap = await createImageBitmap(new Blob([bytes], { type }));
    try {
        return await toJpeg(bitmap, bitmap.width, bitmap.height, maxSide, quality);
    } finally {
        bitmap.close();
    }
}

async function toJpeg(source, width, height, maxSide, quality) {
    const scale = Math.min(1, maxSide / Math.max(width, height));
    const canvas = document.createElement('canvas');
    canvas.width = Math.round(width * scale);
    canvas.height = Math.round(height * scale);
    canvas.getContext('2d').drawImage(source, 0, 0, canvas.width, canvas.height);
    const blob = await new Promise(resolve => canvas.toBlob(resolve, 'image/jpeg', quality));
    return new Uint8Array(await blob.arrayBuffer());
}

// Asks the browser to share a window, tab or screen, grabs one frame as a PNG (sharp text for OCR),
// and stops sharing straight away. The frame stays in this browser.
export async function captureScreen() {
    if (!navigator.mediaDevices?.getDisplayMedia) {
        throw new Error('unsupported');
    }

    let screen;
    try {
        screen = await navigator.mediaDevices.getDisplayMedia({ video: true, audio: false, selfBrowserSurface: 'exclude' });
    } catch (e) {
        throw new Error(e?.name || 'failed');
    }

    try {
        const video = document.createElement('video');
        video.muted = true;
        video.playsInline = true;
        video.srcObject = screen;
        await video.play();
        // The first frames can be blank while sharing starts.
        await new Promise(resolve => setTimeout(resolve, 500));
        if (!video.videoWidth) throw new Error('not-ready');

        const canvas = document.createElement('canvas');
        canvas.width = video.videoWidth;
        canvas.height = video.videoHeight;
        canvas.getContext('2d').drawImage(video, 0, 0);
        video.srcObject = null;
        const blob = await new Promise(resolve => canvas.toBlob(resolve, 'image/png'));
        return new Uint8Array(await blob.arrayBuffer());
    } finally {
        screen.getTracks().forEach(track => track.stop());
    }
}
