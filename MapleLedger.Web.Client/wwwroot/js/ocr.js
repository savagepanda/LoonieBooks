// Reads receipt photos with Tesseract.js, entirely in this browser. The engine, its WebAssembly
// core and the English and French models are served from this site (wwwroot/lib/tesseract), not
// a CDN, and the photo is never uploaded. The models are cached in IndexedDB after the first use.

let workerPromise = null;
let progressTarget = null;

const base = () => new URL('lib/tesseract/', document.baseURI).href;

function loadScript() {
    if (window.Tesseract) return Promise.resolve();
    return new Promise((resolve, reject) => {
        const script = document.createElement('script');
        script.src = base() + 'tesseract.min.js';
        script.onload = resolve;
        script.onerror = () => reject(new Error('Could not load the OCR engine.'));
        document.head.appendChild(script);
    });
}

async function worker() {
    await loadScript();
    workerPromise ??= Tesseract.createWorker(['eng', 'fra'], Tesseract.OEM.LSTM_ONLY, {
        workerPath: base() + 'worker.min.js',
        corePath: base() + 'core',
        langPath: base() + 'lang',
        gzip: true,
        workerBlobURL: false,
        logger: m => progressTarget?.invokeMethodAsync('OnOcrProgress', m.status ?? '', m.progress ?? 0).catch(() => { }),
    }).catch(e => {
        workerPromise = null;
        throw e;
    });
    return workerPromise;
}

// Returns the text Tesseract found. `progress` is a .NET object with an OnOcrProgress method.
export async function recognize(bytes, contentType, progress) {
    progressTarget = progress;
    try {
        const w = await worker();
        // Receipts are a single column of text; this mode keeps lines in reading order.
        await w.setParameters({ tessedit_pageseg_mode: Tesseract.PSM.SINGLE_COLUMN, preserve_interword_spaces: '1' });
        const { data } = await w.recognize(new Blob([bytes], { type: contentType }));
        return data.text ?? '';
    } finally {
        progressTarget = null;
    }
}

export async function terminate() {
    const w = await workerPromise?.catch(() => null);
    workerPromise = null;
    await w?.terminate();
}

// Reads a picture of a statement (a photo, or a screenshot of the bank's site) and returns its words
// with their positions, for LoonieBooks to rebuild the transaction table from: the same way it reads
// PDF statements. Positions are what matter here, so the page's reading order doesn't.
export async function recognizeWords(bytes, contentType, progress) {
    progressTarget = progress;
    try {
        const w = await worker();
        // Sparse text: a table's lone dates and amounts are exactly what the automatic layout skips.
        await w.setParameters({ tessedit_pageseg_mode: Tesseract.PSM.SPARSE_TEXT, preserve_interword_spaces: '0' });
        const image = await enlarge(new Blob([bytes], { type: contentType }));
        // Photos are rarely straight: Tesseract measures the tilt and straightens the page first,
        // so the words of one row share a baseline from the date on the left to the amount on the right.
        const { data } = await w.recognize(image, { rotateAuto: true }, { blocks: true, text: false });
        return statementWords(data);
    } finally {
        progressTarget = null;
    }
}

// Screen text is small: Tesseract reads it much better at twice the size.
async function enlarge(blob) {
    const bitmap = await createImageBitmap(blob);
    try {
        const longest = Math.max(bitmap.width, bitmap.height);
        if (longest >= 1800) return blob;
        const scale = Math.min(2, 3000 / longest);
        const canvas = document.createElement('canvas');
        canvas.width = Math.round(bitmap.width * scale);
        canvas.height = Math.round(bitmap.height * scale);
        const context = canvas.getContext('2d');
        context.imageSmoothingQuality = 'high';
        context.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
        return await new Promise(resolve => canvas.toBlob(resolve, 'image/png'));
    } finally {
        bitmap.close();
    }
}

// Flattens Tesseract's blocks into words: { text, left, right, baseline, size }, with the baseline
// growing upward (negated pixel rows) as LoonieBooks' statement reader expects.
function statementWords(data) {
    const lines = (data.blocks ?? []).flatMap(b => b.paragraphs ?? []).flatMap(p => p.lines ?? []);

    const found = [];
    for (const line of lines) {
        const b = line.baseline;
        for (const word of line.words ?? []) {
            const text = (word.text ?? '').trim();
            // Very low confidence is usually an icon or a ruled line read as letters.
            if (!text || (word.confidence ?? 100) < 25) continue;
            const box = word.bbox;
            // The line's baseline where this word sits; the box's bottom when there's none.
            const middle = (box.x0 + box.x1) / 2;
            const base = b && b.x1 > b.x0 ? b.y0 + (b.y1 - b.y0) * (middle - b.x0) / (b.x1 - b.x0) : box.y1;
            found.push({ text, box, base, height: box.y1 - box.y0 });
        }
    }

    // One text size for the page: words' heights are mostly capitals and digits, about 0.7 of the
    // font size, so the median scaled up is close to it. It sets how wide a gap separates cells.
    const heights = found.map(w => w.height).sort((a, b) => a - b);
    const size = heights.length ? heights[Math.floor(heights.length / 2)] * 1.4 : 0;

    return found.map(({ text, box, base }) => ({ text, left: box.x0, right: box.x1, baseline: -base, size }));
}
